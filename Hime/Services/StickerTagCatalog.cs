using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Local-only semantic metadata for curated stickers. The key is the local file name,
/// so the catalog survives debug-output copies of the same approved asset.
/// </summary>
public sealed class StickerTagCatalog
{
    public const int CurrentCatalogVersion = 4;
    private static readonly Regex SafeTag = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled);
    private readonly string _catalogPath;
    private readonly int _maxTagsPerSticker;
    private readonly ILogger<StickerTagCatalog> _logger;
    private readonly object _sync = new();
    private Dictionary<string, StickerTagCatalogEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public StickerTagCatalog(IOptions<StickerTagOptions> options, ILogger<StickerTagCatalog> logger)
    {
        var configured = options.Value;
        _catalogPath = ResolvePath(configured.CatalogPath);
        _maxTagsPerSticker = Math.Clamp(configured.MaxTagsPerSticker, 1, 24);
        _logger = logger;
        Load();
        LoadBundledSeeds();
    }

    public bool IsIndexed(string imagePath)
    {
        var key = GetKey(imagePath);
        lock (_sync)
            return _entries.TryGetValue(key, out var entry) && entry.CatalogVersion >= CurrentCatalogVersion;
    }

    public IReadOnlyList<string> GetTags(string imagePath)
    {
        var key = GetKey(imagePath);
        lock (_sync)
        {
            return _entries.TryGetValue(key, out var entry)
                ? entry.Tags.ToArray()
                : [];
        }
    }

    public StickerTagCatalogEntry? GetEntry(string imagePath)
    {
        var key = GetKey(imagePath);
        lock (_sync)
            return _entries.TryGetValue(key, out var entry) ? CloneEntry(entry) : null;
    }

    public IReadOnlyList<StickerTagCatalogEntry> GetEntries(IEnumerable<string> imagePaths)
    {
        var keys = imagePaths.Select(GetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
            return _entries
                .Where(pair => keys.Contains(pair.Key) && pair.Value.CatalogVersion >= CurrentCatalogVersion)
                .Select(pair => CloneEntry(pair.Value))
                .ToList();
    }

    public IReadOnlyList<string> GetAvailableTags(IEnumerable<string> imagePaths, int maximum = 72)
    {
        var keys = imagePaths.Select(GetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            return _entries
                .Where(pair => keys.Contains(pair.Key))
                .SelectMany(pair => pair.Value.Tags
                    .Concat(pair.Value.IntentTags)
                    .Concat(pair.Value.EmotionScores.Keys))
                .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(maximum, 1, 160))
                .Select(group => group.Key)
                .ToList();
        }
    }

    public void Upsert(string imagePath, string fallbackEmotion, IEnumerable<string>? semanticTags)
        => Upsert(
            imagePath,
            fallbackEmotion,
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [fallbackEmotion] = 1.0 },
            semanticTags,
            intentTags: null);

    public void Upsert(
        string imagePath,
        string fallbackEmotion,
        IReadOnlyDictionary<string, double>? emotionScores,
        IEnumerable<string>? semanticTags,
        IEnumerable<string>? intentTags,
        string labelSource = "model")
    {
        var key = GetKey(imagePath);
        if (string.IsNullOrWhiteSpace(key))
            return;

        var tags = NormalizeTags(semanticTags)
            .Append(NormalizeSingleTag(fallbackEmotion))
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(_maxTagsPerSticker)
            .ToList();
        if (tags.Count == 0)
            tags = ["neutral"];

        var normalizedEmotions = NormalizeEmotionScores(emotionScores, fallbackEmotion);
        var normalizedIntents = NormalizeTags(intentTags).Take(_maxTagsPerSticker).ToList();
        var normalizedSource = string.Equals(labelSource, "manual", StringComparison.OrdinalIgnoreCase)
            ? "manual"
            : "model";

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing) &&
                existing.CatalogVersion >= CurrentCatalogVersion &&
                existing.FallbackEmotion.Equals(fallbackEmotion, StringComparison.OrdinalIgnoreCase) &&
                existing.Tags.SequenceEqual(tags, StringComparer.OrdinalIgnoreCase) &&
                existing.IntentTags.SequenceEqual(normalizedIntents, StringComparer.OrdinalIgnoreCase) &&
                existing.LabelSource.Equals(normalizedSource, StringComparison.OrdinalIgnoreCase) &&
                EmotionScoresEqual(existing.EmotionScores, normalizedEmotions))
            {
                return;
            }

            _entries[key] = new StickerTagCatalogEntry
            {
                FileName = key,
                FallbackEmotion = fallbackEmotion.Trim().ToLowerInvariant(),
                EmotionScores = normalizedEmotions,
                Tags = tags,
                IntentTags = normalizedIntents,
                LabelSource = normalizedSource,
                CatalogVersion = CurrentCatalogVersion,
                UpdatedAt = DateTime.UtcNow
            };
            SaveUnderLock();
        }
    }

    public bool Remove(string imagePath, bool preserveManual = true)
    {
        var key = GetKey(imagePath);
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var existing) ||
                (preserveManual && existing.LabelSource.Equals("manual", StringComparison.OrdinalIgnoreCase)))
                return false;

            _entries.Remove(key);
            SaveUnderLock();
            return true;
        }
    }

    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? rawTags) =>
        (rawTags ?? [])
            .SelectMany(tag => (tag ?? string.Empty).Split(
                ['|', '｜', ',', '，', '、', ';', '；', '/', '\\', '+', '＋', '&', '＆', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(NormalizeSingleTag)
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string NormalizeSingleTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var compact = value.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        return SafeTag.IsMatch(compact) ? compact : string.Empty;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_catalogPath))
                return;

            var serialized = File.ReadAllText(_catalogPath);
            var entries = JsonSerializer.Deserialize<List<StickerTagCatalogEntry>>(serialized) ?? [];
            _entries = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.FileName))
                .ToDictionary(
                    entry => Path.GetFileName(entry.FileName),
                    entry => entry with
                    {
                        FileName = Path.GetFileName(entry.FileName),
                        FallbackEmotion = NormalizeSingleTag(entry.FallbackEmotion),
                        EmotionScores = NormalizeEmotionScores(entry.EmotionScores, entry.FallbackEmotion),
                        Tags = NormalizeTags(entry.Tags).ToList(),
                        IntentTags = NormalizeTags(entry.IntentTags).ToList(),
                        LabelSource = string.Equals(entry.LabelSource, "manual", StringComparison.OrdinalIgnoreCase)
                            ? "manual"
                            : "model"
                    },
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to read local sticker semantic catalog at {CatalogPath}", _catalogPath);
        }
    }

    /// <summary>
    /// Merge curated metadata shipped with the application into the local runtime catalog.
    /// Runtime entries always win, so an administrator's manual relabel is never overwritten
    /// by a later build or restart.
    /// </summary>
    private void LoadBundledSeeds()
    {
        var roots = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "resources", "images", "approved"),
                Path.Combine(AppContext.BaseDirectory, "resources", "images", "approved"),
                Path.Combine(Path.GetDirectoryName(_catalogPath) ?? string.Empty, "..", "resources", "images", "approved")
            }
            .Select(path => Path.GetFullPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToList();

        var added = 0;
        foreach (var root in roots)
        {
            foreach (var seedPath in Directory.EnumerateFiles(root, "tags.json", SearchOption.AllDirectories))
            {
                try
                {
                    var seeds = JsonSerializer.Deserialize<List<StickerTagCatalogEntry>>(
                        File.ReadAllText(seedPath)) ?? [];
                    lock (_sync)
                    {
                        foreach (var seed in seeds)
                        {
                            var key = GetKey(seed.FileName);
                            if (string.IsNullOrWhiteSpace(key) || _entries.ContainsKey(key))
                                continue;

                            var fallback = NormalizeSingleTag(seed.FallbackEmotion);
                            _entries[key] = seed with
                            {
                                FileName = key,
                                FallbackEmotion = string.IsNullOrWhiteSpace(fallback) ? "neutral" : fallback,
                                EmotionScores = NormalizeEmotionScores(seed.EmotionScores, fallback),
                                Tags = NormalizeTags(seed.Tags).Take(_maxTagsPerSticker).ToList(),
                                IntentTags = NormalizeTags(seed.IntentTags).Take(_maxTagsPerSticker).ToList(),
                                LabelSource = "manual",
                                CatalogVersion = CurrentCatalogVersion,
                                UpdatedAt = seed.UpdatedAt == default ? DateTime.UtcNow : seed.UpdatedAt
                            };
                            added++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to read bundled sticker tag seed {SeedPath}", seedPath);
                }
            }
        }

        if (added <= 0)
            return;

        lock (_sync)
            SaveUnderLock();
        _logger.LogInformation("Merged {Count} bundled sticker tag entries into the runtime catalog.", added);
    }

    private void SaveUnderLock()
    {
        try
        {
            var directory = Path.GetDirectoryName(_catalogPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var temporary = _catalogPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                _entries.Values.OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _catalogPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to persist local sticker semantic catalog at {CatalogPath}", _catalogPath);
        }
    }

    private static string GetKey(string imagePath) => Path.GetFileName(imagePath ?? string.Empty);

    private static Dictionary<string, double> NormalizeEmotionScores(
        IReadOnlyDictionary<string, double>? raw,
        string fallbackEmotion)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in raw ?? new Dictionary<string, double>())
        {
            var tag = NormalizeSingleTag(pair.Key);
            if (string.IsNullOrWhiteSpace(tag) || double.IsNaN(pair.Value) || double.IsInfinity(pair.Value))
                continue;
            scores[tag] = Math.Round(Math.Clamp(pair.Value, 0, 1), 4);
        }

        if (scores.Count == 0)
        {
            var fallback = NormalizeSingleTag(fallbackEmotion);
            scores[string.IsNullOrWhiteSpace(fallback) ? "neutral" : fallback] = 1.0;
        }
        return scores;
    }

    private static bool EmotionScoresEqual(
        IReadOnlyDictionary<string, double> left,
        IReadOnlyDictionary<string, double> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var value) && Math.Abs(pair.Value - value) < 0.0001);

    private static StickerTagCatalogEntry CloneEntry(StickerTagCatalogEntry entry) => entry with
    {
        EmotionScores = new Dictionary<string, double>(entry.EmotionScores, StringComparer.OrdinalIgnoreCase),
        Tags = entry.Tags.ToList(),
        IntentTags = entry.IntentTags.ToList()
    };

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        var workingDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), path);
        return Directory.Exists(Directory.GetCurrentDirectory()) ? workingDirectoryPath : Path.Combine(AppContext.BaseDirectory, path);
    }
}

public sealed record StickerTagCatalogEntry
{
    public string FileName { get; init; } = string.Empty;
    public string FallbackEmotion { get; init; } = "neutral";
    public Dictionary<string, double> EmotionScores { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Tags { get; init; } = [];
    public List<string> IntentTags { get; init; } = [];
    public string LabelSource { get; init; } = "model";
    public int CatalogVersion { get; init; }
    public DateTime UpdatedAt { get; init; }
}
