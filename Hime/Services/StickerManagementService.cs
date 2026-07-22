using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sora.Entities.Message;

namespace Hime.Services;

/// <summary>Owner-operated import, re-analysis and manual labeling for approved stickers.</summary>
public sealed class StickerManagementService
{
    private static readonly IReadOnlyDictionary<string, string> EmotionSemanticTags =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["happy"] = "smile", ["shy"] = "blush", ["surprised"] = "surprised",
            ["embarrassed"] = "sweatdrop", ["angry"] = "angry", ["sad"] = "crying",
            ["comforting"] = "hug", ["serious"] = "serious", ["proud"] = "smug",
            ["neutral"] = "expressionless"
        };

    private readonly IncomingImageStore _incomingImages;
    private readonly AnimeStickerTagger _tagger;
    private readonly StickerTagCatalog _catalog;
    private readonly ImageService _images;
    private readonly OpenCodeStickerCatalogPublisher _publisher;
    private readonly ILogger<StickerManagementService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _idSync = new();
    private readonly string _managementIdPath;
    private Dictionary<string, string> _managementIds = new(StringComparer.OrdinalIgnoreCase);

    public StickerManagementService(
        IncomingImageStore incomingImages,
        AnimeStickerTagger tagger,
        StickerTagCatalog catalog,
        ImageService images,
        OpenCodeStickerCatalogPublisher publisher,
        ILogger<StickerManagementService> logger)
    {
        _incomingImages = incomingImages;
        _tagger = tagger;
        _catalog = catalog;
        _images = images;
        _publisher = publisher;
        _logger = logger;
        var workingDirectory = Directory.GetCurrentDirectory();
        var writableRoot = File.Exists(Path.Combine(workingDirectory, "Hime.csproj"))
            ? workingDirectory
            : AppContext.BaseDirectory;
        _managementIdPath = Path.Combine(writableRoot, "data", "sticker-management-ids.json");
        LoadManagementIds();
    }

    public async Task<IReadOnlyList<StickerImportResult>> AddFromMessageAsync(
        MessageBody? body,
        long userId,
        long? groupId,
        IEnumerable<string>? manualLabels = null,
        CancellationToken cancellationToken = default)
    {
        var archived = await _incomingImages.ArchiveAsync(body, userId, groupId, cancellationToken);
        var imagePaths = archived.Where(IsSupportedSticker).Take(5).ToList();
        if (imagePaths.Count == 0)
            return [];

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var results = new List<StickerImportResult>(imagePaths.Count);
            var requestedLabels = StickerLabelVocabulary.ResolveMany(manualLabels);
            foreach (var source in imagePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = ComputeSha256(source);
                var duplicate = FindApprovedDuplicate(hash);
                var destination = duplicate ?? CopyIntoApprovedLibraries(source, hash, groupId);
                _images.Refresh();
                var existing = _catalog.GetEntry(destination);
                var preserveExistingManual = existing is not null &&
                    existing.LabelSource.Equals("manual", StringComparison.OrdinalIgnoreCase);
                var analysis = preserveExistingManual ? null : AnalyzeAndIndex(destination);
                var manual = requestedLabels.Count > 0
                    ? ApplyManualLabels(destination, requestedLabels)
                    : preserveExistingManual
                        ? BuildManualLabelResult(destination, existing!)
                        : null;
                results.Add(new StickerImportResult(
                    GetManagementId(destination),
                    Path.GetFileName(destination),
                    destination,
                    duplicate is not null,
                    analysis,
                    manual));
            }

            _publisher.Publish();
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StickerRebuildResult> RebuildAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _images.Refresh();
            var paths = _images.AvailableImages.Values
                .Where(IsSupportedSticker)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var recognized = 0;
            var unrecognized = 0;
            var preservedManual = 0;
            var analyzedFrames = 0;

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existing = _catalog.GetEntry(path);
                if (existing?.LabelSource.Equals("manual", StringComparison.OrdinalIgnoreCase) == true &&
                    existing.CatalogVersion >= StickerTagCatalog.CurrentCatalogVersion)
                {
                    preservedManual++;
                    continue;
                }

                var analysis = AnalyzeAndIndex(path);
                if (analysis is null)
                {
                    _catalog.Remove(path, preserveManual: true);
                    unrecognized++;
                }
                else
                {
                    recognized++;
                    analyzedFrames += analysis.AnalyzedFrames;
                }

                await Task.Delay(20, cancellationToken);
            }

            _publisher.Publish();
            return new StickerRebuildResult(paths.Count, recognized, unrecognized, preservedManual, analyzedFrames);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<UnrecognizedSticker> GetUnrecognized()
    {
        _images.Refresh();
        return _images.AvailableImages.Values
            .Where(IsSupportedSticker)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !_catalog.IsIndexed(path))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => new UnrecognizedSticker(GetManagementId(path), Path.GetFileName(path), path))
            .ToList();
    }

    public IReadOnlyList<ManagedSticker> GetManagedStickers(string? filter = null)
    {
        _images.Refresh();
        var normalizedFilter = ResolveFilter(filter);
        return _images.AvailableImages.Values
            .Where(IsSupportedSticker)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var entry = _catalog.GetEntry(path);
                return new ManagedSticker(
                    GetManagementId(path),
                    Path.GetFileName(path),
                    path,
                    entry is not null && entry.CatalogVersion >= StickerTagCatalog.CurrentCatalogVersion,
                    entry?.FallbackEmotion ?? "unrecognized",
                    entry?.Tags ?? [],
                    entry?.IntentTags ?? [],
                    entry?.LabelSource ?? "none");
            })
            .Where(item => string.IsNullOrWhiteSpace(normalizedFilter) ||
                item.ReferenceId.Equals(normalizedFilter, StringComparison.OrdinalIgnoreCase) ||
                item.StickerId.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase) ||
                item.FallbackEmotion.Equals(normalizedFilter, StringComparison.OrdinalIgnoreCase) ||
                item.Tags.Contains(normalizedFilter, StringComparer.OrdinalIgnoreCase) ||
                item.IntentTags.Contains(normalizedFilter, StringComparer.OrdinalIgnoreCase) ||
                (!item.Recognized && normalizedFilter.Equals("unrecognized", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.ReferenceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<StickerManualLabelResult?> SetManualEmotionsAsync(
        string target,
        IEnumerable<string> labels,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = ResolveTarget(target);
            if (path is null)
                return null;

            var definitions = StickerLabelVocabulary.ResolveMany(labels);
            if (definitions.Count == 0)
                return new StickerManualLabelResult(Path.GetFileName(path), path, [], [], [], false);
            var result = ReplaceManualLabels(path, definitions);
            _publisher.Publish();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StickerManualLabelResult?> AppendManualEmotionsAsync(
        string target,
        IEnumerable<string> labels,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = ResolveTarget(target);
            if (path is null)
                return null;

            var definitions = StickerLabelVocabulary.ResolveMany(labels);
            if (definitions.Count == 0)
                return new StickerManualLabelResult(Path.GetFileName(path), path, [], [], [], false);
            var result = ApplyManualLabels(path, definitions);
            _publisher.Publish();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private AnimeTagResult? AnalyzeAndIndex(string path)
    {
        var analysis = _tagger.Analyze(path);
        if (analysis is null)
            return null;

        _catalog.Upsert(
            path,
            analysis.Emotion,
            analysis.EmotionScores,
            analysis.SemanticTags,
            analysis.IntentTags,
            labelSource: "model");
        return analysis;
    }

    private string? ResolveTarget(string target)
    {
        var cleaned = target.Trim().Trim('"').TrimStart('#');
        var fileName = Path.GetFileName(cleaned);
        if (_images.AvailableImages.TryGetValue(fileName, out var path))
            return path;

        var byId = GetManagedStickers().Where(item => item.ReferenceId.Equals(cleaned, StringComparison.OrdinalIgnoreCase)).ToList();
        return byId.Count == 1 ? byId[0].Path : null;
    }

    private StickerManualLabelResult ApplyManualLabels(
        string path,
        IReadOnlyList<StickerLabelDefinition> definitions)
    {
        var existing = _catalog.GetEntry(path);
        var addedEmotions = definitions
            .Select(definition => definition.BaseEmotion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existingEmotions = existing is null
            ? Enumerable.Empty<string>()
            : existing.EmotionScores.Keys;
        var emotions = existingEmotions
            .Concat(addedEmotions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var semantics = (existing?.Tags ?? [])
            .Concat(definitions
            .Where(definition => definition.Kind == StickerLabelKind.Semantic)
            .Select(definition => definition.Canonical))
            .Concat(definitions
                .Where(definition => definition.Kind == StickerLabelKind.Emotion)
                .Select(definition => EmotionSemanticTags[definition.BaseEmotion]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var intents = (existing?.IntentTags ?? [])
            .Concat(GetExplicitIntentTags(definitions))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var scores = existing?.EmotionScores.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var emotion in addedEmotions)
            scores[emotion] = 1d;

        var fallback = !string.IsNullOrWhiteSpace(existing?.FallbackEmotion)
            ? existing.FallbackEmotion
            : addedEmotions[0];
        _catalog.Upsert(path, fallback, scores, semantics, intents, labelSource: "manual");
        return new StickerManualLabelResult(
            Path.GetFileName(path), path, emotions, semantics, intents, true);
    }

    private StickerManualLabelResult ReplaceManualLabels(
        string path,
        IReadOnlyList<StickerLabelDefinition> definitions)
    {
        var emotions = definitions
            .Select(definition => definition.BaseEmotion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var semantics = definitions
            .Where(definition => definition.Kind == StickerLabelKind.Semantic)
            .Select(definition => definition.Canonical)
            .Concat(definitions
                .Where(definition => definition.Kind == StickerLabelKind.Emotion)
                .Select(definition => EmotionSemanticTags[definition.BaseEmotion]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var intents = GetExplicitIntentTags(definitions);
        var scores = emotions.ToDictionary(
            emotion => emotion,
            _ => 1d,
            StringComparer.OrdinalIgnoreCase);

        _catalog.Upsert(path, emotions[0], scores, semantics, intents, labelSource: "manual");
        return new StickerManualLabelResult(
            Path.GetFileName(path), path, emotions, semantics, intents, true);
    }

    private static StickerManualLabelResult BuildManualLabelResult(
        string path,
        StickerTagCatalogEntry entry) =>
        new(
            Path.GetFileName(path),
            path,
            entry.EmotionScores.Keys.ToList(),
            entry.Tags,
            entry.IntentTags,
            true);

    private static string ResolveFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return string.Empty;
        if (StickerLabelVocabulary.TryResolve(filter, out var definition))
            return definition.Canonical;
        return filter.Trim().TrimStart('#').ToLowerInvariant();
    }

    private string GetManagementId(string path)
    {
        var contentHash = ComputeSha256(path).ToLowerInvariant();
        lock (_idSync)
        {
            if (_managementIds.TryGetValue(contentHash, out var existing))
                return existing;

            var occupied = _managementIds.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var attempt = 0; attempt < 10000; attempt++)
            {
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{contentHash}:{attempt}"));
                var candidate = (1000 + BitConverter.ToUInt32(digest, 0) % 9000).ToString();
                if (occupied.Contains(candidate))
                    continue;

                _managementIds[contentHash] = candidate;
                SaveManagementIdsUnderLock();
                return candidate;
            }
        }

        throw new InvalidOperationException("无法为表情包分配不重复的四位管理编号。");
    }

    private void LoadManagementIds()
    {
        try
        {
            if (!File.Exists(_managementIdPath))
                return;
            _managementIds = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(_managementIdPath)) ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _managementIds = new Dictionary<string, string>(_managementIds, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to load sticker management IDs from {Path}", _managementIdPath);
        }
    }

    private void SaveManagementIdsUnderLock()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_managementIdPath)!);
            var temporary = _managementIdPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                _managementIds.OrderBy(pair => pair.Value).ToDictionary(pair => pair.Key, pair => pair.Value),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _managementIdPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to persist sticker management IDs to {Path}", _managementIdPath);
        }
    }

    private string? FindApprovedDuplicate(string hash)
    {
        foreach (var path in _images.AvailableImages.Values.Where(IsSupportedSticker).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (ComputeSha256(path).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not hash existing sticker {Path}", path);
            }
        }
        return null;
    }

    private static string CopyIntoApprovedLibraries(string source, string hash, long? groupId)
    {
        var scope = groupId?.ToString() ?? "private";
        var extension = GetStickerExtension(source);
        var fileName = $"sticker_{hash[..24].ToLowerInvariant()}{extension}";
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(AppContext.BaseDirectory, "resources", "images", "approved", scope)
        };
        var workingDirectory = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(workingDirectory, "Hime.csproj")))
            roots.Add(Path.Combine(workingDirectory, "resources", "images", "approved", scope));

        string? runtimePath = null;
        foreach (var root in roots)
        {
            Directory.CreateDirectory(root);
            var destination = Path.Combine(root, fileName);
            if (!File.Exists(destination))
                File.Copy(source, destination, overwrite: false);
            if (destination.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                runtimePath = destination;
        }
        return runtimePath ?? Path.Combine(roots.First(), fileName);
    }

    /// <summary>
    /// Manual labels are authoritative: an intent is persisted only when the
    /// administrator explicitly supplied an intent label. Emotion labels must
    /// never manufacture a conversational intent (for example serious -> calm).
    /// </summary>
    private static IReadOnlyList<string> GetExplicitIntentTags(
        IEnumerable<StickerLabelDefinition> definitions) =>
        definitions
            .Where(definition => definition.Kind == StickerLabelKind.Intent)
            .Select(definition => definition.Canonical)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsSupportedSticker(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            Span<byte> header = stackalloc byte[12];
            using var stream = File.OpenRead(path);
            var read = stream.Read(header);
            return IsGifHeader(header, read) || IsPngHeader(header, read) ||
                   IsJpegHeader(header, read) || IsWebpHeader(header, read);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string GetStickerExtension(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        if (IsGifHeader(header, read)) return ".gif";
        if (IsPngHeader(header, read)) return ".png";
        if (IsJpegHeader(header, read)) return ".jpg";
        if (IsWebpHeader(header, read)) return ".webp";
        throw new InvalidOperationException("管理员上传的文件不是受支持的图片格式。");
    }

    private static bool IsGifHeader(ReadOnlySpan<byte> header, int length) =>
        length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8));

    private static bool IsPngHeader(ReadOnlySpan<byte> header, int length) =>
        length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4e && header[3] == 0x47 &&
        header[4] == 0x0d && header[5] == 0x0a && header[6] == 0x1a && header[7] == 0x0a;

    private static bool IsJpegHeader(ReadOnlySpan<byte> header, int length) =>
        length >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;

    private static bool IsWebpHeader(ReadOnlySpan<byte> header, int length) =>
        length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public sealed record StickerImportResult(
    string ReferenceId,
    string StickerId,
    string Path,
    bool AlreadyExisted,
    AnimeTagResult? Analysis,
    StickerManualLabelResult? ManualLabel);
public sealed record StickerRebuildResult(int Total, int Recognized, int Unrecognized, int PreservedManual, int AnalyzedFrames);
public sealed record UnrecognizedSticker(string ReferenceId, string StickerId, string Path);
public sealed record ManagedSticker(
    string ReferenceId,
    string StickerId,
    string Path,
    bool Recognized,
    string FallbackEmotion,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> IntentTags,
    string LabelSource);
public sealed record StickerManualLabelResult(
    string StickerId,
    string Path,
    IReadOnlyList<string> Emotions,
    IReadOnlyList<string> SemanticTags,
    IReadOnlyList<string> IntentTags,
    bool Success);
