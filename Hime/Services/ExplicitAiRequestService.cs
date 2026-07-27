using Hime.Commands;
using Hime.Messaging;
using Hime.Messaging.Interactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Owns the single explicit-AI entry point for private and group conversations.
/// HimeBotService only decides handler order; trigger parsing, private burst
/// coalescing and empty-input waits live here.
/// </summary>
public sealed class ExplicitAiRequestService : IDisposable
{
    private readonly ICommandBus _commandBus;
    private readonly IInteractionManager _interactions;
    private readonly IOptionsMonitor<PrivateConversationOptions> _options;
    private readonly EmotionalPragmaticsPlanner _emotionalPragmatics;
    private readonly ILogger<ExplicitAiRequestService> _logger;
    private readonly object _batchSync = new();
    private readonly Dictionary<PrivateConversationKey, PendingPrivateConversation> _pending = [];
    private readonly CancellationTokenSource _stop = new();

    public ExplicitAiRequestService(
        ICommandBus commandBus,
        IInteractionManager interactions,
        IOptionsMonitor<PrivateConversationOptions> options,
        EmotionalPragmaticsPlanner emotionalPragmatics,
        ILogger<ExplicitAiRequestService> logger)
    {
        _commandBus = commandBus;
        _interactions = interactions;
        _options = options;
        _emotionalPragmatics = emotionalPragmatics;
        _logger = logger;
    }

    /// <summary>
    /// Returns true when this message belongs to explicit AI/private handling and
    /// no later reactive handler may consume it.
    /// </summary>
    public async Task<bool> TryHandleAsync(
        IncomingMessage message,
        bool isBotDirected,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (TryExtractPrompt(message.Text, options.TriggerPrefix, out var prompt))
        {
            if (!message.IsGroup &&
                (!options.Enabled || message.SenderId == message.SelfId || !options.Allows(message.SenderId)))
            {
                return true;
            }

            if (IsResetPrompt(prompt, options.ResetPrompts))
            {
                await _commandBus.SendAsync(
                    new ClearAiConversationCommand(message.NativeEvent),
                    cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(prompt) || message.ContainsVisual)
            {
                if (message.IsGroup)
                {
                    await _commandBus.SendAsync(
                        new GenerateAiReplyCommand(message, prompt),
                        cancellationToken);
                }
                else
                {
                    QueuePrivateConversation(message, prompt);
                }
            }
            else if (message.IsGroup)
            {
                await AiCommand.Reply(message.NativeEvent, _emotionalPragmatics.GetEmptyMentionReply());
            }
            else
            {
                await RegisterPrivateWaitAsync(message, options, cancellationToken);
            }

            return true;
        }

        // Private conversations are opt-in through the configured trigger. A
        // normal friend message must not fall through to group-only handlers.
        if (!message.IsGroup)
            return true;

        if (!isBotDirected || message.Text.TrimStart().StartsWith("/", StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(message.Text) && !message.ContainsVisual)
        {
            await AiCommand.Reply(message.NativeEvent, _emotionalPragmatics.GetEmptyMentionReply());
            return true;
        }

        await _commandBus.SendAsync(
            new GenerateAiReplyCommand(message, message.Text),
            cancellationToken);
        return true;
    }

    public static bool TryExtractPrompt(
        string? rawText,
        string? configuredPrefix,
        out string prompt)
    {
        prompt = string.Empty;
        var text = rawText?.Trim() ?? string.Empty;
        var prefix = string.IsNullOrWhiteSpace(configuredPrefix)
            ? string.Empty
            : configuredPrefix.Trim();
        if (prefix.Length == 0 ||
            !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            (text.Length > prefix.Length && !char.IsWhiteSpace(text[prefix.Length])))
        {
            return false;
        }

        prompt = text[prefix.Length..].TrimStart();
        return true;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RegisterPrivateWaitAsync(
        IncomingMessage message,
        PrivateConversationOptions options,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.WaitTimeoutSeconds, 30, 600));
        var now = DateTimeOffset.UtcNow;
        await _interactions.RegisterAsync(
            new PendingInteraction(
                Guid.NewGuid().ToString("N"),
                message.ScopeKey,
                InteractionKinds.PrivateAiPrompt,
                InteractionMode.HardWait,
                message.SenderId,
                now,
                now.Add(timeout)),
            cancellationToken);
        await AiCommand.Reply(
            message.NativeEvent,
            $"我在，接着说就好。{Math.Ceiling(timeout.TotalMinutes):0} 分钟内发来的下一条消息会接在这次后面；不想问了就说“取消”。");
    }

    private void QueuePrivateConversation(IncomingMessage message, string prompt)
    {
        var key = new PrivateConversationKey(message.AccountId, message.SenderId);
        PendingPrivateConversation queue;
        var startWorker = false;
        var maximum = Math.Clamp(_options.CurrentValue.MaxMergedMessages, 1, 20);
        lock (_batchSync)
        {
            if (!_pending.TryGetValue(key, out queue!))
            {
                queue = new PendingPrivateConversation();
                _pending[key] = queue;
            }

            queue.Messages.Add(new PendingPrivateMessage(message, prompt));
            if (queue.Messages.Count > maximum)
                queue.Messages.RemoveRange(0, queue.Messages.Count - maximum);
            queue.LastReceivedUtc = DateTime.UtcNow;

            if (!queue.IsProcessing)
            {
                queue.IsProcessing = true;
                startWorker = true;
            }
        }

        if (startWorker)
            _ = ProcessPrivateConversationAsync(key, queue, _stop.Token);
    }

    private async Task ProcessPrivateConversationAsync(
        PrivateConversationKey key,
        PendingPrivateConversation queue,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (true)
                {
                    TimeSpan remaining;
                    lock (_batchSync)
                    {
                        var window = TimeSpan.FromSeconds(
                            Math.Clamp(_options.CurrentValue.MergeWindowSeconds, 0, 15));
                        remaining = queue.LastReceivedUtc + window - DateTime.UtcNow;
                    }

                    if (remaining <= TimeSpan.Zero)
                        break;
                    await Task.Delay(remaining, cancellationToken);
                }

                List<PendingPrivateMessage> batch;
                lock (_batchSync)
                {
                    var window = TimeSpan.FromSeconds(
                        Math.Clamp(_options.CurrentValue.MergeWindowSeconds, 0, 15));
                    if (DateTime.UtcNow - queue.LastReceivedUtc < window)
                        continue;

                    batch = queue.Messages.ToList();
                    queue.Messages.Clear();
                }

                if (batch.Count == 0)
                    continue;

                var latest = batch[^1].Message;
                try
                {
                    await _commandBus.SendAsync(
                        new GenerateAiReplyCommand(latest, BuildMergedPrompt(batch)),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Merged private conversation reply failed (UserId={UserId}).",
                        key.UserId);
                }

                lock (_batchSync)
                {
                    if (queue.Messages.Count == 0)
                    {
                        _pending.Remove(key);
                        queue.IsProcessing = false;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            lock (_batchSync)
            {
                if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, queue))
                {
                    _pending.Remove(key);
                    queue.IsProcessing = false;
                }
            }
        }
    }

    private static string BuildMergedPrompt(IReadOnlyList<PendingPrivateMessage> batch)
    {
        var textParts = batch
            .Select(item => item.Text.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Take(8)
            .ToArray();
        var visualCount = batch.Count(item => item.Message.ContainsVisual);

        if (textParts.Length == 0)
        {
            return visualCount > 1
                ? $"[The user sent {visualCount} images or stickers in quick succession without text. Give one brief, friendly reaction. Do not claim visual details without trusted local evidence.]"
                : "[The user sent an image or sticker without text. Give one brief, friendly reaction. Do not claim visual details without trusted local evidence.]";
        }

        var prefix = batch.Count > 1
            ? "[These private messages arrived in one short burst. Reply once to their combined conversational meaning instead of answering each line separately.]\n"
            : string.Empty;
        var visualHint = visualCount > 0
            ? $"\n[The burst also contains {visualCount} image or sticker message(s).]"
            : string.Empty;
        return prefix + string.Join('\n', textParts) + visualHint;
    }

    private static bool IsResetPrompt(string prompt, IEnumerable<string>? resetPrompts) =>
        (resetPrompts ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => prompt.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));

    private sealed class PendingPrivateConversation
    {
        public List<PendingPrivateMessage> Messages { get; } = [];
        public DateTime LastReceivedUtc { get; set; }
        public bool IsProcessing { get; set; }
    }

    private sealed record PendingPrivateMessage(IncomingMessage Message, string Text);
    private readonly record struct PrivateConversationKey(string AccountId, long UserId);
}
