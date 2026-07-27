using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// 图片资源服务：启动时扫描图片目录，按文件名查找。
/// AI 回复中的 [img:文件名] 标记由此服务解析为实际文件路径。
/// </summary>
public sealed class ImageService
{
    private readonly Dictionary<string, string> _images;
    private readonly Dictionary<string, List<string>> _emotionImages;
    private readonly string _imageDir;
    private readonly List<string> _scanDirectories;
    private readonly HashSet<string> _allowedExtensions;
    private readonly bool _onlyUseApprovedStickers;
    private readonly HashSet<string> _approvedStickerFileNames;
    private readonly IReadOnlyList<string> _approvedStickerDirectories;
    private readonly long _maxSendableStickerBytes;
    private readonly ILogger<ImageService>? _logger;
    private readonly StickerTagCatalog _stickerTags;
    private readonly StickerLabelVocabulary _labels;
    private readonly StickerTagOptions _stickerTagOptions;
    private readonly object _sync = new();

    /// <summary>图片资源目录绝对路径</summary>
    public string ImageDirectory => _imageDir;

    public ImageService(
        IOptions<ImageOptions> options,
        StickerTagCatalog stickerTags,
        StickerLabelVocabulary labels,
        IOptions<StickerTagOptions> stickerTagOptions,
        ILogger<ImageService>? logger = null)
    {
        var opts = options.Value;
        _stickerTags = stickerTags;
        _labels = labels;
        _stickerTagOptions = stickerTagOptions.Value;
        _logger = logger;
        _maxSendableStickerBytes = Math.Max(64 * 1024, opts.MaxSendableStickerBytes);

        _imageDir = Path.IsPathRooted(opts.Directory)
            ? opts.Directory
            : Path.Combine(AppContext.BaseDirectory, opts.Directory);

        _images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _emotionImages = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _allowedExtensions = new HashSet<string>(opts.AllowedExtensions, StringComparer.OrdinalIgnoreCase);
        _onlyUseApprovedStickers = opts.OnlyUseApprovedStickers;
        _approvedStickerFileNames = new HashSet<string>(
            opts.ApprovedStickerFileNames
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => Path.GetFileName(name)!),
            StringComparer.OrdinalIgnoreCase);
        _approvedStickerDirectories = opts.ApprovedStickerDirectories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.IsPathRooted(directory) ? directory : Path.Combine(AppContext.BaseDirectory, directory))
            .Select(Path.GetFullPath)
            .Select(directory => directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _scanDirectories = new List<string> { _imageDir };
        _scanDirectories.AddRange(opts.AdditionalDirectories.Select(directory =>
            Path.IsPathRooted(directory) ? directory : Path.Combine(AppContext.BaseDirectory, directory)));
        ScanDirectories();
    }

    /// <summary>Refreshes the allow-listed library after a human approves new stickers.</summary>
    public void Refresh() => ScanDirectories();

    private void ScanDirectories()
    {
        lock (_sync)
        {
            var skippedOversized = 0;
            _images.Clear();
            _emotionImages.Clear();
            foreach (var directory in _scanDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory))
                    continue;

                foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
                {
                    if (!_allowedExtensions.Contains(Path.GetExtension(file)))
                        continue;
                    if (new FileInfo(file).Length > _maxSendableStickerBytes)
                    {
                        skippedOversized++;
                        continue;
                    }
                    RegisterImageInternal(null, file);
                }
            }
            RebuildEmotionPoolsFromCatalogUnderLock();
            if (skippedOversized > 0)
            {
                _logger?.LogWarning(
                    "Excluded {Count} oversized sticker(s) from the sendable catalog (limit={LimitBytes} bytes).",
                    skippedOversized,
                    _maxSendableStickerBytes);
            }
        }
    }

    /// <summary>
    /// 按文件名查找图片完整路径。
    /// </summary>
    /// <param name="fileName">文件名（含扩展名，大小写不敏感）</param>
    /// <returns>图片的完整物理路径，未找到时返回 null</returns>
    public string? Resolve(string fileName)
    {
        lock (_sync)
            return _images.TryGetValue(fileName, out var path) && File.Exists(path) ? path : null;
    }

    /// <summary>Returns whether a sticker is in the local cute/anime curation allow-list.</summary>
    public bool IsApprovedStickerPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var fullPath = Path.GetFullPath(path);
        if (!_allowedExtensions.Contains(Path.GetExtension(fullPath)))
            return false;
        if (new FileInfo(fullPath).Length > _maxSendableStickerBytes)
            return false;

        return IsApprovedStickerPathCore(fullPath);
    }

    public IReadOnlyList<string> CanonicalEmotions => _labels.BaseEmotions;

    /// <summary>把运行时词表中的中英文情绪标签统一为基础情绪。</summary>
    public string NormalizeEmotion(string emotion)
        => _labels.NormalizeBaseEmotion(emotion);

    public bool TryNormalizeEmotion(string? emotion, out string canonical)
    {
        return _labels.TryNormalizeBaseEmotion(emotion, out canonical);
    }

    /// <summary>Normalizes model-supplied semantic tags before they can select a sticker.</summary>
    public IReadOnlyList<string> NormalizeStickerTags(IEnumerable<string>? tags) =>
        StickerTagCatalog.NormalizeTags(tags)
            .Select(tag => _labels.TryResolve(tag, out var definition) ? definition.Canonical : tag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

    /// <summary>按规范情绪标签解析对应的表情包路径。</summary>
    public string? ResolveEmotion(string emotion)
    {
        var canonical = NormalizeEmotion(emotion);
        lock (_sync)
        {
            var candidates = new List<string>();
            if (_emotionImages.TryGetValue(canonical, out var emotionPaths))
            {
                candidates.AddRange(emotionPaths.Where(File.Exists));
            }

            var distinct = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return distinct.Count == 0 ? null : distinct[Random.Shared.Next(distinct.Count)];
        }
    }

    /// <summary>
    /// Selects an approved sticker by one or more WDv3 semantic tags. All requested
    /// tags are scored; if no exact semantic match exists, the nearest base emotion
    /// is used as a safe compatibility fallback.
    /// </summary>
    public string? ResolveSticker(IEnumerable<string>? tags)
    {
        var requested = NormalizeStickerTags(tags);
        if (requested.Count == 0)
            return null;

        var emotions = requested
            .Where(tag => CanonicalEmotions.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(tag => tag, _ => 1.0, StringComparer.OrdinalIgnoreCase);
        var semantic = requested
            .Where(tag => !CanonicalEmotions.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return SearchStickers(new StickerSearchRequest
        {
            Emotions = emotions,
            SemanticTags = semantic,
            Count = 1
        }).FirstOrDefault()?.Path;
    }

    /// <summary>
    /// Scores approved stickers against independent emotion weights, visual semantics,
    /// and conversation intent. Low-confidence queries fall back to a calm sticker.
    /// </summary>
    public IReadOnlyList<StickerSearchCandidate> SearchStickers(StickerSearchRequest? query)
    {
        query ??= new StickerSearchRequest();
        var requestedEmotions = NormalizeEmotionWeights(query.Emotions);
        var semanticTags = NormalizeStickerTags(query.SemanticTags);
        var intentTags = StickerTagCatalog.NormalizeTags(query.IntentTags).Take(8).ToList();
        var count = Math.Clamp(query.Count, 1, 3);
        var strong = Math.Clamp(_stickerTagOptions.StrongMatchThreshold, 0.5, 1);
        var partial = Math.Clamp(_stickerTagOptions.PartialMatchThreshold, 0.25, strong);

        List<string> paths;
        lock (_sync)
            paths = _images.Values.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();

        var candidates = paths
            .Select(path => ScoreCandidate(path, requestedEmotions, semanticTags, intentTags))
            .Where(candidate => candidate is not null)
            .Cast<StickerSearchCandidate>()
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.StickerId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var primaryEmotion = requestedEmotions.OrderByDescending(pair => pair.Value).FirstOrDefault().Key;
        var primaryTag = semanticTags.FirstOrDefault();
        var accepted = candidates
            .Where(candidate => candidate.Score >= strong ||
                (candidate.Score >= partial &&
                 (string.IsNullOrWhiteSpace(primaryEmotion)
                     ? primaryTag is not null && candidate.MatchedTags.Contains(primaryTag, StringComparer.OrdinalIgnoreCase)
                     : candidate.MatchedEmotions.Contains(primaryEmotion, StringComparer.OrdinalIgnoreCase))))
            .Take(count)
            .Select(candidate => candidate with
            {
                MatchLevel = candidate.Score >= strong ? "strong" : "partial"
            })
            .ToList();

        if (accepted.Count >= count)
            return accepted;

        var excluded = accepted.Select(item => item.StickerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var neutralTags = StickerTagCatalog.NormalizeTags(_stickerTagOptions.NeutralFallbackTags)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var neutral = candidates
            .Where(candidate => !excluded.Contains(candidate.StickerId))
            .Where(candidate =>
            {
                var entry = _stickerTags.GetEntry(candidate.StickerId);
                return entry is not null &&
                       (entry.EmotionScores.GetValueOrDefault(_labels.FallbackEmotion) >= 0.35 ||
                        entry.Tags.Concat(entry.IntentTags).Any(neutralTags.Contains));
            })
            .OrderBy(_ => Random.Shared.Next())
            .Take(count - accepted.Count)
            .Select(candidate => candidate with { MatchLevel = "neutral-fallback", Score = 0 })
            .ToList();
        accepted.AddRange(neutral);
        return accepted;
    }

    public string? ResolveStickerId(string? stickerId)
    {
        if (string.IsNullOrWhiteSpace(stickerId) || stickerId != Path.GetFileName(stickerId))
            return null;
        return Resolve(stickerId);
    }

    /// <summary>运行时把新收录表情包加入情绪池，无需重启。</summary>
    public void RegisterEmotionImage(
        string emotion,
        string path,
        IEnumerable<string>? semanticTags = null,
        IReadOnlyDictionary<string, double>? emotionScores = null,
        IEnumerable<string>? intentTags = null)
    {
        if (!IsApprovedStickerPath(path))
            return;

        _stickerTags.Upsert(
            path,
            NormalizeEmotion(emotion),
            emotionScores,
            semanticTags,
            intentTags);
        Refresh();
    }

    /// <summary>Returns the catalog-declared base emotion; file-name prefixes are ignored.</summary>
    public string? GetDeclaredEmotion(string path)
    {
        var entry = _stickerTags.GetEntry(path);
        return entry is not null && entry.CatalogVersion >= StickerTagCatalog.CurrentCatalogVersion
            ? entry.FallbackEmotion
            : null;
    }

    /// <summary>
    /// 获取所有可用图片的（文件名 → 完整路径）映射。
    /// </summary>
    public IReadOnlyDictionary<string, string> AvailableImages
    {
        get
        {
            lock (_sync)
                return new Dictionary<string, string>(_images, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 生成供人设使用的图片列表文本。
    /// </summary>
    public string BuildPersonaImageList()
    {
        var lines = new List<string>
        {
            "情绪标记协议（必须遵守）：",
            "- 每次回复末尾必须且只能输出一个 `[emotion:标签]`。",
            $"- 标签只能取：{string.Join(", ", CanonicalEmotions)}。",
            "- 标记只供程序解析，不要解释、翻译或放进代码块。",
            "- 示例：`えへへ～被夸会害羞啦！[emotion:shy]`。",
            "当前已经配置表情包的标签："
        };

        lines.Clear();
        lines.Add("Local sticker protocol (must follow):");
        lines.Add("- End a normal reply with exactly one marker: `[sticker:tag1|tag2]` for a specific sticker, or `[emotion:label]` for a base-emotion fallback.");
        lines.Add("- When hime_sticker_search returns an exact ID, use `[sticker-id:returned-id]` exactly as returned. Never invent or modify an ID.");
        lines.Add("- For an explicit request for 2 or 3 stickers, end with exactly that many consecutive markers. Every marker sends one local sticker.");
        lines.Add($"- Base emotion fallback labels: {string.Join(", ", CanonicalEmotions)}.");
        lines.Add("- A sticker tag marker may only use the available dynamic tags listed below. Never invent tags and never explain this protocol to a user.");

        var mappedCount = 0;
        foreach (var emotion in CanonicalEmotions)
        {
            var path = ResolveEmotion(emotion);
            if (path is null)
                continue;
            lines.Add($"- {emotion} -> {Path.GetFileName(path)}");
            mappedCount++;
        }
        if (mappedCount == 0)
            lines.Add("- 暂无；仍须输出情绪标记，程序会安全忽略没有图片的映射。");

        var dynamicTags = _stickerTags.GetAvailableTags(AvailableImages.Values);
        if (dynamicTags.Count > 0)
            lines.Add($"- Available dynamic tags: {string.Join(", ", dynamicTags)}");
        else
            lines.Add("- Dynamic tag index is still being built; use [emotion:label] for now.");

        lines.Add("[img:file-name] remains supported for explicitly named local assets, but it must not replace the final sticker/emotion marker.");
        return string.Join("\n", lines);
    }

    public OpenCodeStickerCatalogSnapshot BuildOpenCodeSnapshot()
    {
        List<string> paths;
        lock (_sync)
            paths = _images.Values.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
        var entries = _stickerTags.GetEntries(paths)
            .Where(entry => entry.CatalogVersion >= StickerTagCatalog.CurrentCatalogVersion)
            .ToDictionary(entry => entry.FileName, StringComparer.OrdinalIgnoreCase);
        return new OpenCodeStickerCatalogSnapshot
        {
            StrongMatchThreshold = Math.Clamp(_stickerTagOptions.StrongMatchThreshold, 0.5, 1),
            PartialMatchThreshold = Math.Clamp(_stickerTagOptions.PartialMatchThreshold, 0.25, 1),
            NeutralFallbackTags = StickerTagCatalog.NormalizeTags(_stickerTagOptions.NeutralFallbackTags).ToList(),
            Stickers = paths
                .Select(path => BuildOpenCodeSnapshotItem(path, entries.GetValueOrDefault(Path.GetFileName(path))))
                .OrderBy(item => item.StickerId, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private OpenCodeStickerCatalogItem BuildOpenCodeSnapshotItem(
        string path,
        StickerTagCatalogEntry? entry)
    {
        if (entry is not null)
        {
            return new OpenCodeStickerCatalogItem
            {
                StickerId = entry.FileName,
                Emotions = new Dictionary<string, double>(entry.EmotionScores, StringComparer.OrdinalIgnoreCase),
                SemanticTags = NormalizeStickerTags(entry.Tags).ToList(),
                IntentTags = StickerTagCatalog.NormalizeTags(entry.IntentTags).Take(8).ToList()
            };
        }

        // A clean Release directory can start before the asynchronous WDv3 backfill has
        // rebuilt its local catalog. Keep the restricted tool useful during that window,
        // but expose only approved IDs and a conservative filename/neutral fallback.
        var fallbackEmotion = TryGetEmotionFromFileName(path) ?? _labels.FallbackEmotion;
        var fallbackSemantic = fallbackEmotion switch
        {
            "happy" => "smile",
            "sad" => "crying",
            "shy" => "blush",
            "surprised" => "surprised",
            "embarrassed" => "embarrassed",
            "angry" => "angry",
            "comforting" => "hug",
            "serious" => "serious",
            "proud" => "smug",
            _ => "expressionless"
        };
        return new OpenCodeStickerCatalogItem
        {
            StickerId = Path.GetFileName(path),
            Emotions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [fallbackEmotion] = 1
            },
            SemanticTags = [fallbackSemantic],
            IntentTags = []
        };
    }

    private StickerSearchCandidate? ScoreCandidate(
        string path,
        IReadOnlyDictionary<string, double> requestedEmotions,
        IReadOnlyList<string> requestedTags,
        IReadOnlyList<string> requestedIntents)
    {
        var entry = _stickerTags.GetEntry(path);
        if (entry is null || entry.CatalogVersion < StickerTagCatalog.CurrentCatalogVersion)
            return null;

        var tags = NormalizeStickerTags(entry.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var intents = StickerTagCatalog.NormalizeTags(entry.IntentTags).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedEmotions = requestedEmotions.Keys
            .Where(emotion => entry.EmotionScores.GetValueOrDefault(emotion) > 0.05)
            .ToList();
        var matchedTags = requestedTags.Where(tags.Contains).ToList();
        var matchedIntents = requestedIntents.Where(intents.Contains).ToList();

        var components = new List<(double Weight, double Score)>();
        if (requestedEmotions.Count > 0)
        {
            var total = requestedEmotions.Values.Sum();
            var score = total <= 0 ? 0 : requestedEmotions.Sum(pair =>
                pair.Value * entry.EmotionScores.GetValueOrDefault(pair.Key)) / total;
            components.Add((0.60, score));
        }
        if (requestedTags.Count > 0)
            components.Add((0.25, matchedTags.Count / (double)requestedTags.Count));
        if (requestedIntents.Count > 0)
            components.Add((0.15, matchedIntents.Count / (double)requestedIntents.Count));
        if (components.Count == 0)
            return null;

        var activeWeight = components.Sum(component => component.Weight);
        var scoreValue = activeWeight <= 0 ? 0 : components.Sum(component => component.Weight * component.Score) / activeWeight;
        return new StickerSearchCandidate
        {
            StickerId = Path.GetFileName(path),
            Path = path,
            Score = Math.Round(Math.Clamp(scoreValue, 0, 1), 4),
            MatchedEmotions = matchedEmotions,
            MatchedTags = matchedTags,
            MatchedIntents = matchedIntents
        };
    }

    private IReadOnlyDictionary<string, double> NormalizeEmotionWeights(IReadOnlyDictionary<string, double>? raw)
    {
        var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in raw ?? new Dictionary<string, double>())
        {
            if (!TryNormalizeEmotion(pair.Key, out var emotion))
                continue;
            if (!double.IsNaN(pair.Value) && !double.IsInfinity(pair.Value) && pair.Value > 0)
                normalized[emotion] = Math.Max(normalized.GetValueOrDefault(emotion), Math.Clamp(pair.Value, 0, 1));
        }
        return normalized;
    }

    private string? InferFallbackEmotion(IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
        {
            if (_labels.TryResolve(tag, out var definition))
                return definition.BaseEmotion;
        }
        return null;
    }

    private void RegisterImageInternal(string? emotion, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsApprovedStickerPathCore(fullPath))
            return;

        _images[Path.GetFileName(fullPath)] = fullPath;
        if (emotion is null)
            return;

        if (!_emotionImages.TryGetValue(emotion, out var paths))
        {
            paths = new List<string>();
            _emotionImages[emotion] = paths;
        }
        if (!paths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            paths.Add(fullPath);
    }

    private void RebuildEmotionPoolsFromCatalogUnderLock()
    {
        foreach (var path in _images.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = _stickerTags.GetEntry(path);
            if (entry is null || entry.CatalogVersion < StickerTagCatalog.CurrentCatalogVersion)
                continue;

            var emotions = entry.EmotionScores
                .Where(pair => pair.Value >= 0.35 && CanonicalEmotions.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .Select(pair => pair.Key.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (emotions.Count == 0 && CanonicalEmotions.Contains(entry.FallbackEmotion, StringComparer.OrdinalIgnoreCase))
                emotions.Add(entry.FallbackEmotion.ToLowerInvariant());

            foreach (var emotion in emotions)
            {
                if (!_emotionImages.TryGetValue(emotion, out var paths))
                {
                    paths = [];
                    _emotionImages[emotion] = paths;
                }
                if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    paths.Add(path);
            }
        }
    }

    private bool IsApprovedStickerPathCore(string fullPath)
    {
        if (!_onlyUseApprovedStickers)
            return true;

        if (_approvedStickerFileNames.Contains(Path.GetFileName(fullPath)))
            return true;

        return _approvedStickerDirectories.Any(directory =>
            fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase));
    }

    private string? TryGetEmotionFromFileName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var prefix = stem.Split(['_', '-'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return prefix is not null && CanonicalEmotions.Contains(prefix, StringComparer.OrdinalIgnoreCase)
            ? prefix.ToLowerInvariant()
            : null;
    }
}
