using System.Security.Cryptography;
using System.Threading.Channels;
using Hime.Data;
using Hime.Data.Models;
using LiteDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Services;

/// <summary>
/// 归档所有群图片，以内容哈希统计高频表情包，并在普通群聊中受控地随机回复。
/// </summary>
public sealed class GroupStickerCollector : BackgroundService
{
    private readonly HimeDbContext _context;
    private readonly IncomingImageStore _imageStore;
    private readonly StickerEmotionAnalyzer _emotionAnalyzer;
    private readonly ImageService _imageService;
    private readonly GroupStickerOptions _options;
    private readonly ILogger<GroupStickerCollector> _logger;
    private readonly string _collectionDirectory;
    private readonly IReadOnlyList<string> _mirrorDirectories;
    private readonly HashSet<string> _allowedExtensions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, DateTime> _lastReplyAt = new();
    private readonly Channel<StickerAnalysisWorkItem> _analysisQueue;
    private DateTime _lastReviewCleanupAt = DateTime.MinValue;
    private long _pendingAnalysis;

    public long PendingAnalysisCount => Math.Max(0, Interlocked.Read(ref _pendingAnalysis));

    public GroupStickerCollector(
        HimeDbContext context,
        IncomingImageStore imageStore,
        StickerEmotionAnalyzer emotionAnalyzer,
        ImageService imageService,
        IOptions<GroupStickerOptions> options,
        ILogger<GroupStickerCollector> logger)
    {
        _context = context;
        _imageStore = imageStore;
        _emotionAnalyzer = emotionAnalyzer;
        _imageService = imageService;
        _options = options.Value;
        _logger = logger;
        _collectionDirectory = Path.IsPathRooted(_options.CollectionDirectory)
            ? _options.CollectionDirectory
            : Path.Combine(AppContext.BaseDirectory, _options.CollectionDirectory);
        _mirrorDirectories = _options.MirrorDirectories
            .Select(directory => Path.IsPathRooted(directory)
                ? directory
                : Path.Combine(AppContext.BaseDirectory, directory))
            .Select(Path.GetFullPath)
            .Where(directory => !directory.Equals(
                Path.GetFullPath(_collectionDirectory),
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _allowedExtensions = _options.AllowedExtensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_allowedExtensions.Count == 0)
            _allowedExtensions.Add(".gif");
        _analysisQueue = Channel.CreateBounded<StickerAnalysisWorkItem>(new BoundedChannelOptions(
            Math.Clamp(_options.AnalysisQueueCapacity, 8, 512))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    private ILiteCollection<GroupStickerRecord> Stickers =>
        _context.Database.GetCollection<GroupStickerRecord>("group_stickers");

    /// <summary>从指定群自身已收录的高频表情中，按情绪随机取一张；绝不跨群取图。</summary>
    public string? GetRandomCollectedSticker(long groupId, string emotion)
    {
        var canonicalEmotion = _imageService.NormalizeEmotion(emotion);
        var threshold = Math.Max(1, _options.MinOccurrences);
        var candidates = Stickers
            .Find(record => record.GroupId == groupId && record.Occurrences >= threshold)
            .Where(record =>
                string.Equals(record.Emotion, canonicalEmotion, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(record.CollectedPath) &&
                _imageService.IsApprovedStickerPath(record.CollectedPath))
            .ToList();

        return candidates.Count == 0
            ? null
            : candidates[Random.Shared.Next(candidates.Count)].CollectedPath;
    }

    public async Task<StickerProcessResult> ProcessAsync(
        MessageReceivedEvent e,
        bool allowRandomReply,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || e.Message.SourceType != MessageSourceType.Group)
            return StickerProcessResult.Empty;

        var userId = e.Sender?.UserId ?? e.Message.SenderId;
        var groupId = (long)e.Message.GroupId;
        // 只有 QQ 标记为 Sticker 的图片会进入表情包库；普通照片不下载、不分析、不收录。
        var archivedPaths = await _imageStore.ArchiveStickersAsync(e.Message.Body, userId, groupId, cancellationToken);
        var acceptedPaths = archivedPaths
            .Where(path => _allowedExtensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rejectedPaths = archivedPaths
            .Except(acceptedPaths, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var rejectedPath in rejectedPaths)
        {
            try
            {
                File.Delete(rejectedPath);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Unable to discard unsupported collected sticker {Path}", rejectedPath);
            }
        }
        if (rejectedPaths.Count > 0)
        {
            _logger.LogInformation(
                "Discarded {Count} non-GIF sticker(s); allowed types: {Extensions}",
                rejectedPaths.Count,
                string.Join(", ", _allowedExtensions));
        }

        foreach (var path in acceptedPaths)
        {
            if (!_options.AutoCollectStickers)
                break;
            if (!_analysisQueue.Writer.TryWrite(new StickerAnalysisWorkItem(groupId, path, e.Message.Body?.GetText() ?? string.Empty)))
            {
                _logger.LogWarning(
                    "Sticker analysis queue is full; deferred analysis was dropped (GroupId={GroupId}, Path={Path})",
                    groupId,
                    path);
            }
            else
            {
                Interlocked.Increment(ref _pendingAnalysis);
            }
        }

        var replied = false;
        GroupStickerRecord? selectedReply = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CleanupExpiredReviewFiles();
            if (allowRandomReply && ShouldReply(groupId))
            {
                var threshold = Math.Max(1, _options.MinOccurrences);
                var candidates = Stickers
                    .Find(record => record.GroupId == groupId && record.Occurrences >= threshold)
                    .Where(record =>
                        !string.IsNullOrWhiteSpace(record.CollectedPath) &&
                        _imageService.IsApprovedStickerPath(record.CollectedPath))
                    .ToList();

                if (candidates.Count > 0)
                {
                    selectedReply = candidates[Random.Shared.Next(candidates.Count)];
                    _lastReplyAt[groupId] = DateTime.UtcNow;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (selectedReply is not null)
        {
            var fileUri = new Uri(Path.GetFullPath(selectedReply.CollectedPath!)).AbsoluteUri;
            var message = new MessageBody().AddImage(fileUri, ImageSubType.Sticker);
            await e.Api.SendGroupMessageAsync(e.Message.GroupId, message, cancellationToken);
            replied = true;
            _logger.LogInformation(
                "随机回复群表情包 (GroupId={GroupId}, Path={Path}, Occurrences={Occurrences})",
                groupId,
                selectedReply.CollectedPath,
                selectedReply.Occurrences);
        }

        return new StickerProcessResult(acceptedPaths, replied, []);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _analysisQueue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (!File.Exists(item.SourcePath))
                    continue;

                // WDv3/ONNX inference is intentionally outside the collector lock.
                // Only the small LiteDB/update section is serialized.
                var evidence = _emotionAnalyzer.Analyze(item.SourcePath, item.ContextText);
                await _gate.WaitAsync(stoppingToken);
                try
                {
                    RegisterImage(item.GroupId, item.SourcePath, evidence);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Background sticker analysis failed (GroupId={GroupId}, Path={Path})",
                    item.GroupId,
                    item.SourcePath);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingAnalysis);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _analysisQueue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private StickerEmotionHint? RegisterImage(
        long groupId,
        string sourcePath,
        StickerEmotionEvidence evidence)
    {
        if (!File.Exists(sourcePath))
            return null;

        var hash = ComputeSha256(sourcePath);
        var id = $"{groupId}:{hash}";
        var now = DateTime.UtcNow;
        var record = Stickers.FindById(id) ?? new GroupStickerRecord
        {
            Id = id,
            GroupId = groupId,
            Sha256 = hash,
            FirstSeenAt = now
        };

        record.Occurrences++;
        record.LastSeenAt = now;
        record.SourcePath = sourcePath;
        record.EmotionVotes ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        record.EmotionVotes[evidence.Emotion] =
            record.EmotionVotes.GetValueOrDefault(evidence.Emotion) + evidence.Weight;
        record.LastAnalysisSource = evidence.Source;
        record.LastVisualConfidence = evidence.VisualConfidence;
        record.LastLocalVisualDescription = evidence.Inspection?.ToCompactText();
        record.LastSemanticTags = (evidence.SemanticTags ?? [])
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        record.EmotionScores = new Dictionary<string, double>(
            evidence.EmotionScores ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [evidence.Emotion] = Math.Clamp(evidence.VisualConfidence ?? 0.6, 0, 1)
            },
            StringComparer.OrdinalIgnoreCase);
        record.IntentTags = (evidence.IntentTags ?? [])
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        record.Emotion = record.EmotionVotes
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key == "neutral" ? 1 : 0)
            .ThenBy(pair => pair.Key)
            .First().Key;

        if (record.Occurrences >= Math.Max(1, _options.MinOccurrences))
        {
            record.CollectedPath = Promote(groupId, record.Emotion, hash, sourcePath, record.CollectedPath);
            // A high-frequency image is not necessarily an anime/game sticker. It is
            // staged for review instead of entering the AI's sendable emotion pool.
            if (!_options.RequireManualApproval)
                _imageService.RegisterEmotionImage(
                    record.Emotion,
                    record.CollectedPath,
                    record.LastSemanticTags,
                    record.EmotionScores,
                    record.IntentTags);
        }

        Stickers.Upsert(record);
        return new StickerEmotionHint(record.Emotion, evidence.Source, evidence.VisualConfidence, record.LastSemanticTags);
    }

    private sealed record StickerAnalysisWorkItem(long GroupId, string SourcePath, string ContextText);

    private string Promote(long groupId, string emotion, string hash, string sourcePath, string? previousPath)
    {
        var groupDirectory = Path.Combine(_collectionDirectory, groupId.ToString());
        Directory.CreateDirectory(groupDirectory);
        var destination = Path.Combine(
            groupDirectory,
            $"{emotion}_{hash[..24].ToLowerInvariant()}{Path.GetExtension(sourcePath).ToLowerInvariant()}");

        if (!string.IsNullOrWhiteSpace(previousPath) &&
            File.Exists(previousPath) &&
            !Path.GetFullPath(previousPath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(previousPath, destination, overwrite: true);
        }
        else if (!File.Exists(destination))
        {
            File.Copy(sourcePath, destination);
        }

        foreach (var mirrorRoot in _mirrorDirectories)
        {
            var mirrorGroupDirectory = Path.Combine(mirrorRoot, groupId.ToString());
            Directory.CreateDirectory(mirrorGroupDirectory);
            var mirrorDestination = Path.Combine(mirrorGroupDirectory, Path.GetFileName(destination));
            var previousMirrorPath = string.IsNullOrWhiteSpace(previousPath)
                ? null
                : Path.Combine(mirrorGroupDirectory, Path.GetFileName(previousPath));

            if (previousMirrorPath is not null &&
                File.Exists(previousMirrorPath) &&
                !Path.GetFullPath(previousMirrorPath).Equals(
                    Path.GetFullPath(mirrorDestination),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Move(previousMirrorPath, mirrorDestination, overwrite: true);
            }
            else
            {
                File.Copy(destination, mirrorDestination, overwrite: true);
            }
        }

        _logger.LogInformation(
            "收录高频群表情包 (GroupId={GroupId}, Emotion={Emotion}, Path={Path}, Mirrors={Mirrors})",
            groupId,
            emotion,
            destination,
            _mirrorDirectories.Count);
        return Path.GetFullPath(destination);
    }

    private void CleanupExpiredReviewFiles()
    {
        if (!_options.RequireManualApproval || _options.ReviewRetentionDays <= 0 ||
            DateTime.UtcNow - _lastReviewCleanupAt < TimeSpan.FromHours(1) ||
            !Directory.Exists(_collectionDirectory))
            return;

        _lastReviewCleanupAt = DateTime.UtcNow;
        var cutoff = DateTime.UtcNow.AddDays(-_options.ReviewRetentionDays);
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(_collectionDirectory, "*.*", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(path) >= cutoff)
                continue;

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Unable to expire unapproved sticker review file {Path}", path);
            }
        }

        if (removed > 0)
            _logger.LogInformation("Expired {Count} unapproved sticker review files", removed);
    }

    private bool ShouldReply(long groupId)
    {
        var probability = Math.Clamp(_options.ReplyProbability, 0, 1);
        if (probability <= 0 || Random.Shared.NextDouble() >= probability)
            return false;

        if (!_lastReplyAt.TryGetValue(groupId, out var lastReply))
            return true;

        return DateTime.UtcNow - lastReply >= TimeSpan.FromSeconds(Math.Max(0, _options.MinReplyIntervalSeconds));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public sealed record StickerEmotionHint(
    string Emotion,
    string Source,
    double? VisualConfidence,
    IReadOnlyList<string> SemanticTags);

public sealed record StickerProcessResult(
    IReadOnlyList<string> ArchivedPaths,
    bool RandomReplySent,
    IReadOnlyList<StickerEmotionHint> EmotionHints)
{
    public static readonly StickerProcessResult Empty = new(Array.Empty<string>(), false, Array.Empty<StickerEmotionHint>());
}
