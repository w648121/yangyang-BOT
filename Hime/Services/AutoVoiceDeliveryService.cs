using System.Text;
using System.Text.RegularExpressions;
using Hime.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// 在文字已送达后异步补发语音。语音合成服务自身串行化 GPU/CPU 推理，
/// 此服务不阻塞消息接收、AI 回复或主动 Agent 的下一次调度。
/// </summary>
public sealed class AutoVoiceDeliveryService : BackgroundService
{
    private static readonly Regex HiddenMarker = new(
        @"\[(?:emotion|情绪|情緒|sticker|表情|表情包|voice|语音|語音):[^\]\r\n]+\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JapaneseKana = new(
        @"[\u3040-\u30ff]",
        RegexOptions.Compiled);

    private static readonly Regex HanCharacter = new(
        @"[\u3400-\u9fff]",
        RegexOptions.Compiled);

    // Emoji, variation selectors and other display-only symbols should not be
    // handed to the local text frontend.  In particular, Python on Windows
    // can reject the Japanese wave dash (U+301C) through its GBK code path.
    private static readonly Regex NonSpeechSymbols = new(
        @"[\p{So}\p{Cs}\p{Cf}]",
        RegexOptions.Compiled);

    private static readonly Regex DestinationContext = new(
        @"^(?:(?:proactive|reactive|targeted)-)?(?<kind>group|friend):(?<id>\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly VoiceSynthesisService _voiceSynthesis;
    private readonly VoiceSynthesisOptions _options;
    private readonly ILogger<AutoVoiceDeliveryService> _logger;
    private readonly RuntimeDiagnostics _diagnostics;
    private readonly VoiceOutboxStore _outbox;
    private readonly IServiceProvider _services;
    private readonly object _queueSync = new();
    private readonly List<VoiceDeliveryJob> _queue = [];
    private readonly HashSet<string> _queuedOutboxIds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _queueSignal = new(0);
    private long _sequence;

    public int PendingCount
    {
        get
        {
            lock (_queueSync)
                return _queue.Count;
        }
    }

    public AutoVoiceDeliveryService(
        VoiceSynthesisService voiceSynthesis,
        IOptions<VoiceSynthesisOptions> options,
        RuntimeDiagnostics diagnostics,
        VoiceOutboxStore outbox,
        IServiceProvider services,
        ILogger<AutoVoiceDeliveryService> logger)
    {
        _voiceSynthesis = voiceSynthesis;
        _options = options.Value;
        _diagnostics = diagnostics;
        _outbox = outbox;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues a speech delivery without delaying its accompanying text message.
    /// The callback owns the actual QQ destination and must send a local WAV path.
    /// </summary>
    public void Enqueue(
        string? visibleText,
        Func<string, CancellationToken, Task> sendAudioAsync,
        string? requestedVoice = null,
        string? context = null,
        string? emotion = null)
    {
        if (!_options.Enabled || !_options.AutoReplyVoiceEnabled || sendAudioAsync is null)
            return;

        var voice = string.IsNullOrWhiteSpace(requestedVoice)
            ? _options.AutoReplyVoice
            : requestedVoice.Trim();
        if (!_voiceSynthesis.AvailableVoices.Contains(voice, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "自动语音请求了未知声线 {Voice}，回退到默认中文声线 {Fallback} ({Context})",
                voice,
                _options.AutoReplyVoice,
                context ?? "unknown");
            voice = _options.AutoReplyVoice;
            if (!_voiceSynthesis.AvailableVoices.Contains(voice, StringComparer.OrdinalIgnoreCase))
            {
                _diagnostics.Increment("voice.skipped.unknown");
                return;
            }
        }

        var speechText = SelectSpeechText(visibleText);
        if (string.IsNullOrWhiteSpace(speechText))
        {
            _diagnostics.Increment("voice.skipped.empty");
            return;
        }

        VoiceOutboxEntry? outboxEntry = null;
        if (_options.Outbox.Enabled && TryParseDestination(context, out var destinationKind, out var destinationId))
        {
            outboxEntry = _outbox.TryCreate(
                speechText,
                voice,
                context,
                emotion,
                GetPriority(context),
                _diagnostics.CurrentCorrelationId,
                destinationKind,
                destinationId);
            if (outboxEntry is null)
            {
                _diagnostics.Increment("voice.outbox.duplicate");
                return;
            }
            _diagnostics.Increment("voice.outbox.created");
        }

        var job = new VoiceDeliveryJob(
            speechText,
            voice,
            sendAudioAsync,
            context,
            emotion,
            GetPriority(context),
            Interlocked.Increment(ref _sequence),
            _diagnostics.CurrentCorrelationId,
            DateTimeOffset.UtcNow,
            outboxEntry?.Id,
            outboxEntry?.DestinationKind,
            outboxEntry?.DestinationId ?? 0,
            IsRecovered: false);
        var releaseSignal = false;
        lock (_queueSync)
        {
            var capacity = Math.Clamp(_options.AutoReplyQueueCapacity, 1, 100);
            if (_queue.Count >= capacity)
            {
                var worst = _queue
                    .OrderByDescending(item => item.Priority)
                    .ThenBy(item => item.Sequence)
                    .First();
                if (job.Priority >= worst.Priority)
                {
                    _logger.LogWarning(
                        "自动语音队列已满，丢弃低优先级任务 (Context={Context}, Queue={QueueCount}/{Capacity})",
                        context ?? "unknown",
                        _queue.Count,
                        capacity);
                    _diagnostics.Increment("voice.dropped.full");
                    if (job.OutboxId is not null)
                        _outbox.Discard(job.OutboxId, "queue full");
                    return;
                }

                _queue.Remove(worst);
                if (worst.OutboxId is not null)
                {
                    _queuedOutboxIds.Remove(worst.OutboxId);
                    _outbox.Discard(worst.OutboxId, "replaced by higher priority task");
                }
                _diagnostics.Increment("voice.dropped.replaced");
                _logger.LogInformation(
                    "自动语音队列已满，以高优先级任务替换 {DroppedContext} (NewContext={Context})",
                    worst.Context ?? "unknown",
                    context ?? "unknown");
            }
            else
            {
                releaseSignal = true;
            }

            _queue.Add(job);
            if (job.OutboxId is not null)
                _queuedOutboxIds.Add(job.OutboxId);
            _diagnostics.Increment("voice.queued");
        }

        if (releaseSignal)
            _queueSignal.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRecovery = DateTimeOffset.UtcNow.AddSeconds(
            Math.Clamp(_options.Outbox.InitialRecoveryDelaySeconds, 0, 120));
        var recoveryInterval = TimeSpan.FromSeconds(Math.Clamp(_options.Outbox.RecoveryScanSeconds, 5, 300));
        while (!stoppingToken.IsCancellationRequested)
        {
            await _queueSignal.WaitAsync(recoveryInterval, stoppingToken);
            if (_options.Outbox.Enabled && DateTimeOffset.UtcNow >= nextRecovery)
            {
                RecoverPendingJobs();
                nextRecovery = DateTimeOffset.UtcNow.Add(recoveryInterval);
            }
            VoiceDeliveryJob? job;
            lock (_queueSync)
            {
                job = _queue
                    .OrderBy(item => item.Priority)
                    .ThenBy(item => item.Sequence)
                    .FirstOrDefault();
                if (job is not null)
                    _queue.Remove(job);
            }

            if (job is null)
                continue;

            var maximumAge = job.IsRecovered
                ? TimeSpan.FromMinutes(Math.Clamp(_options.Outbox.RecoveryMaximumAgeMinutes, 1, 60))
                : TimeSpan.FromSeconds(Math.Clamp(_options.AutoReplyMaxQueueAgeSeconds, 5, 300));
            if (DateTimeOffset.UtcNow - job.CreatedAt > maximumAge)
            {
                _logger.LogInformation(
                    "丢弃已经过时的自动语音 (Context={Context}, AgeSeconds={AgeSeconds:F1})",
                    job.Context ?? "unknown",
                    (DateTimeOffset.UtcNow - job.CreatedAt).TotalSeconds);
                _diagnostics.Increment("voice.dropped.stale");
                if (job.OutboxId is not null)
                {
                    _outbox.Discard(job.OutboxId, "queue task expired");
                    RemoveQueuedOutboxId(job.OutboxId);
                }
                continue;
            }

            try
            {
                await DeliverAsync(job, stoppingToken);
            }
            finally
            {
                if (job.OutboxId is not null)
                    RemoveQueuedOutboxId(job.OutboxId);
            }
        }
    }

    private async Task DeliverAsync(VoiceDeliveryJob job, CancellationToken cancellationToken)
    {
        using var correlation = _diagnostics.PushCorrelation(job.CorrelationId ?? $"voice-{job.Sequence}");
        try
        {
            VoiceSynthesisResult result = new(VoiceSynthesisStatus.Failed, Error: "尚未尝试合成。");
            const int maximumAttempts = 3;
            using var synthesisOperation = _diagnostics.Begin("voice.synthesize");
            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                result = await _voiceSynthesis.SynthesizeAsync(
                    job.SpeechText,
                    job.Voice,
                    cancellationToken,
                    job.Emotion);
                if (result.IsSuccess)
                    break;

                if (attempt >= maximumAttempts || !IsTransient(result))
                    break;

                _logger.LogWarning(
                    "自动语音合成遇到瞬时错误，将重试 (Voice={Voice}, Attempt={Attempt}/{Maximum}, Context={Context}, Error={Error})",
                    job.Voice,
                    attempt,
                    maximumAttempts,
                    job.Context ?? "unknown",
                    result.Error);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 3), cancellationToken);
            }

            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.FilePath))
            {
                synthesisOperation.Fail();
                synthesisOperation.Dispose();
                _diagnostics.Increment("voice.failed");
                _logger.LogWarning(
                    "自动语音合成失败 (Voice={Voice}, Status={Status}, Context={Context}, Error={Error})",
                    job.Voice,
                    result.Status,
                    job.Context ?? "unknown",
                    result.Error);
                if (job.OutboxId is not null)
                    _outbox.MarkFailure(job.OutboxId, result.Error);
                return;
            }

            synthesisOperation.Dispose();

            await _diagnostics.TrackAsync(
                "reply.send.audio",
                () => job.SendAudioAsync(result.FilePath, cancellationToken));
            _diagnostics.Increment("replies.audio.sent");
            if (job.OutboxId is not null)
            {
                _outbox.Complete(job.OutboxId);
                _diagnostics.Increment("voice.outbox.completed");
            }
            _logger.LogInformation(
                "自动语音已补发 (Voice={Voice}, Emotion={Emotion}, Cached={Cached}, Context={Context})",
                job.Voice,
                job.Emotion ?? "neutral",
                result.FromCache,
                job.Context ?? "unknown");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception ex)
        {
            if (job.OutboxId is not null)
                _outbox.MarkFailure(job.OutboxId, ex.Message);
            _logger.LogWarning(
                ex,
                "自动语音投递失败 (Voice={Voice}, Context={Context})",
                job.Voice,
                job.Context ?? "unknown");
        }
    }

    private void RecoverPendingJobs()
    {
        foreach (var entry in _outbox.GetRecoverable(DateTimeOffset.UtcNow))
        {
            var added = false;
            lock (_queueSync)
            {
                if (_queuedOutboxIds.Contains(entry.Id))
                    continue;
                var capacity = Math.Clamp(_options.AutoReplyQueueCapacity, 1, 100);
                if (_queue.Count >= capacity)
                    break;

                _queue.Add(new VoiceDeliveryJob(
                    entry.SpeechText,
                    entry.Voice,
                    CreateRecoveredCallback(entry.DestinationKind, entry.DestinationId),
                    entry.Context,
                    entry.Emotion,
                    entry.Priority,
                    Interlocked.Increment(ref _sequence),
                    entry.CorrelationId,
                    entry.CreatedAt,
                    entry.Id,
                    entry.DestinationKind,
                    entry.DestinationId,
                    IsRecovered: true));
                _queuedOutboxIds.Add(entry.Id);
                added = true;
            }

            if (!added)
                continue;
            _diagnostics.Increment("voice.outbox.recovered");
            _queueSignal.Release();
        }
    }

    private Func<string, CancellationToken, Task> CreateRecoveredCallback(string destinationKind, long destinationId) =>
        async (path, token) =>
        {
            var sender = _services.GetRequiredService<IGroupMessageSender>();
            if (!sender.IsReady)
                throw new InvalidOperationException("QQ message service is not connected.");
            if (string.Equals(destinationKind, "friend", StringComparison.OrdinalIgnoreCase))
                await sender.SendFriendAudioAsync(destinationId, path, token);
            else
                await sender.SendGroupAudioAsync(destinationId, path, token);
        };

    private void RemoveQueuedOutboxId(string id)
    {
        lock (_queueSync)
            _queuedOutboxIds.Remove(id);
    }

    private static bool TryParseDestination(string? context, out string kind, out long id)
    {
        kind = string.Empty;
        id = 0;
        var match = DestinationContext.Match(context ?? string.Empty);
        if (!match.Success || !long.TryParse(match.Groups["id"].Value, out id) || id <= 0)
            return false;
        kind = match.Groups["kind"].Value.ToLowerInvariant();
        return true;
    }

    private static int GetPriority(string? context)
    {
        var value = context ?? string.Empty;
        if (value.StartsWith("proactive-", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (value.StartsWith("reactive-", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (value.StartsWith("targeted-", StringComparison.OrdinalIgnoreCase))
            return 1;
        return 0;
    }

    private static bool IsTransient(VoiceSynthesisResult result)
    {
        if (result.Status == VoiceSynthesisStatus.TimedOut)
            return true;
        if (result.Status != VoiceSynthesisStatus.Failed)
            return false;

        var error = result.Error ?? string.Empty;
        return error.Contains("连接", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("HTTP 5", StringComparison.OrdinalIgnoreCase);
    }

    private string SelectSpeechText(string? visibleText)
    {
        var cleaned = HiddenMarker.Replace(visibleText ?? string.Empty, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            return string.Empty;

        if (_options.AutoReplySpeakChineseOnly)
        {
            var lines = cleaned
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => HanCharacter.IsMatch(line))
                .ToArray();
            var chineseLine = lines.LastOrDefault(line => !JapaneseKana.IsMatch(line))
                ?? lines.LastOrDefault();
            if (!string.IsNullOrWhiteSpace(chineseLine))
                cleaned = chineseLine;
        }

        cleaned = NormalizeForSpeech(cleaned);
        if (string.IsNullOrWhiteSpace(cleaned))
            return string.Empty;

        var limit = Math.Max(1, _options.MaxTextLength);
        return cleaned.Length <= limit ? cleaned : cleaned[..limit];
    }

    private static string NormalizeForSpeech(string text)
    {
        // U+FF5E is supported by GBK while U+301C is not.  FormKC then
        // normalizes other compatibility variants before the request reaches
        // GPT-SoVITS.
        var normalized = text
            .Replace('\u301c', '\uff5e')
            .Normalize(NormalizationForm.FormKC);

        return NonSpeechSymbols
            .Replace(normalized, " ")
            .Replace("\u200b", string.Empty)
            .Trim();
    }

    private sealed record VoiceDeliveryJob(
        string SpeechText,
        string Voice,
        Func<string, CancellationToken, Task> SendAudioAsync,
        string? Context,
        string? Emotion,
        int Priority,
        long Sequence,
        string? CorrelationId,
        DateTimeOffset CreatedAt,
        string? OutboxId,
        string? DestinationKind,
        long DestinationId,
        bool IsRecovered);
}
