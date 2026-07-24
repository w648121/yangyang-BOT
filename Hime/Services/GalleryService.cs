using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Loads a source-audited local artwork catalog and selects a non-repeating item.
/// Social artwork must meet the configured engagement floor; official game assets
/// may be used only as the complete-character fallback.
/// </summary>
public sealed class GalleryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly GalleryOptions _options;
    private readonly ILogger<GalleryService> _logger;
    private readonly object _sync = new();
    private readonly Queue<string> _recent = new();
    private DateTime _lastWriteUtc;
    private IReadOnlyList<GalleryItem> _items = [];

    public GalleryService(IOptions<GalleryOptions> options, ILogger<GalleryService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    public GallerySelection Select(string? character)
    {
        if (!_options.Enabled)
            return GallerySelection.Failure("美图功能当前未开启。");

        var items = LoadCatalog();
        if (items.Count == 0)
            return GallerySelection.Failure("美图库当前没有可用图片。");

        var query = Normalize(character);
        var matching = string.IsNullOrEmpty(query)
            ? items
            : items.Where(item => Matches(item, query)).ToList();

        if (matching.Count == 0)
            return GallerySelection.Failure($"暂时没有找到“{character?.Trim()}”的合规美图。可发送 /美图 角色 查看角色列表。");

        var highEngagement = matching.Where(IsHighEngagement).ToList();
        var pool = _options.PreferHighEngagement && highEngagement.Count > 0
            ? highEngagement
            : matching.Where(item => IsHighEngagement(item) || (_options.AllowOfficialFallback && item.IsOfficial)).ToList();

        if (pool.Count == 0)
            return GallerySelection.Failure("匹配到的图片没有通过来源和热度校验。");

        var selected = ChooseWithoutRecentRepeat(pool);
        var fullPath = ResolvePath(selected.LocalPath);
        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Gallery image is missing (Id={Id}, Path={Path})", selected.Id, fullPath);
            return GallerySelection.Failure("这张美图的本地文件丢失了，请稍后再试。");
        }

        var length = new FileInfo(fullPath).Length;
        if (length <= 0 || length > _options.MaxImageBytes)
        {
            _logger.LogWarning("Gallery image failed size validation (Id={Id}, Bytes={Bytes})", selected.Id, length);
            return GallerySelection.Failure("这张美图尺寸不适合通过 QQ 发送，已阻止发送。");
        }

        return GallerySelection.Success(selected, fullPath, IsHighEngagement(selected));
    }

    public IReadOnlyList<string> GetCharacters() => LoadCatalog()
        .Select(item => item.Character)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private IReadOnlyList<GalleryItem> LoadCatalog()
    {
        var path = ResolvePath(_options.CatalogPath);
        if (!File.Exists(path))
            return [];

        var writeUtc = File.GetLastWriteTimeUtc(path);
        lock (_sync)
        {
            if (_items.Count > 0 && writeUtc == _lastWriteUtc)
                return _items;

            try
            {
                var catalog = JsonSerializer.Deserialize<GalleryCatalog>(File.ReadAllText(path), JsonOptions);
                _items = (catalog?.Items ?? [])
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                    .Where(item => !string.IsNullOrWhiteSpace(item.Character))
                    .Where(item => !string.IsNullOrWhiteSpace(item.LocalPath))
                    .Where(item => item.IsOfficial || IsHighEngagement(item))
                    .ToList();
                _lastWriteUtc = writeUtc;
                _logger.LogInformation("Gallery catalog loaded ({Count} items, {Characters} characters).",
                    _items.Count,
                    _items.Select(item => item.Character).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load gallery catalog from {Path}", path);
                _items = [];
            }

            return _items;
        }
    }

    private GalleryItem ChooseWithoutRecentRepeat(IReadOnlyList<GalleryItem> pool)
    {
        lock (_sync)
        {
            var recent = _recent.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fresh = pool.Where(item => !recent.Contains(Fingerprint(item))).ToList();
            var candidates = fresh.Count > 0 ? fresh : pool;
            var selected = candidates[Random.Shared.Next(candidates.Count)];

            _recent.Enqueue(Fingerprint(selected));
            var limit = Math.Clamp(_options.RecentHistorySize, 0, 100);
            while (_recent.Count > limit)
                _recent.Dequeue();
            return selected;
        }
    }

    private static string Fingerprint(GalleryItem item) =>
        string.IsNullOrWhiteSpace(item.Sha256) ? item.Id : item.Sha256;

    private bool IsHighEngagement(GalleryItem item) =>
        Math.Max(item.Likes, item.Favorites) >= Math.Max(0, _options.MinimumEngagement);

    private static bool Matches(GalleryItem item, string normalizedQuery)
    {
        if (Normalize(item.Character).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            normalizedQuery.Contains(Normalize(item.Character), StringComparison.OrdinalIgnoreCase))
            return true;

        return item.Aliases.Any(alias =>
        {
            var normalizedAlias = Normalize(alias);
            return normalizedAlias.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                   normalizedQuery.Contains(normalizedAlias, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string Normalize(string? value) => string.Concat((value ?? string.Empty)
        .Trim()
        .ToLowerInvariant()
        .Where(character => !char.IsWhiteSpace(character) && character is not '·' and not '-' and not '_' and not '。'));

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        var working = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
        if (File.Exists(working) || Directory.Exists(Path.GetDirectoryName(working)))
            return working;

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }
}

public sealed class GalleryCatalog
{
    public int Version { get; set; } = 1;
    public List<GalleryItem> Items { get; set; } = [];
}

public sealed class GalleryItem
{
    public string Id { get; set; } = string.Empty;
    public string Character { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = [];
    public string Title { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public string NonAiBasis { get; set; } = string.Empty;
    public bool IsOfficial { get; set; }
    public long Likes { get; set; }
    public long Favorites { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed record GallerySelection(bool Found, GalleryItem? Item, string? FullPath, bool HighEngagement, string? Error)
{
    public static GallerySelection Success(GalleryItem item, string fullPath, bool highEngagement) =>
        new(true, item, fullPath, highEngagement, null);

    public static GallerySelection Failure(string error) => new(false, null, null, false, error);
}
