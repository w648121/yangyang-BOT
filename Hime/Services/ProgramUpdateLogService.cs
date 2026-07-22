using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>从追加式 JSONL 发布记录生成确定性的用户可见更新日志。</summary>
public sealed class ProgramUpdateLogService
{
    private readonly IOptionsMonitor<ProgramUpdateOptions> _options;
    private readonly ILogger<ProgramUpdateLogService> _logger;
    private readonly object _sync = new();
    private string _loadedPath = string.Empty;
    private DateTime _loadedWriteUtc;
    private IReadOnlyList<ProgramUpdateEntry> _entries = [];

    public ProgramUpdateLogService(
        IOptionsMonitor<ProgramUpdateOptions> options,
        ILogger<ProgramUpdateLogService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string BuildVersionStatus()
    {
        var current = _options.CurrentValue.CurrentVersion.Trim();
        var latest = OrderedEntries().FirstOrDefault();
        if (latest is null)
            return $"Hime {DisplayVersion(current)}\n更新日志尚未生成。";

        var currentRecorded = string.IsNullOrWhiteSpace(current) ||
                              Load().Any(item => VersionEquals(item.Version, current));
        return $"Hime {DisplayVersion(current)}\n" +
               $"最近发布：{latest.ReleasedAt:yyyy-MM-dd HH:mm}｜{latest.Title}\n" +
               (currentRecorded ? "更新日志：已记录" : "更新日志：当前版本缺少记录，请联系维护者");
    }

    public string BuildLog(string? argument)
    {
        var request = argument?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(request) || request.Equals("最新", StringComparison.OrdinalIgnoreCase))
            return Format(OrderedEntries().Take(Math.Clamp(_options.CurrentValue.DefaultEntries, 1, 3)));

        if (request.Equals("全部", StringComparison.OrdinalIgnoreCase) ||
            request.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Format(OrderedEntries().Take(Math.Clamp(_options.CurrentValue.MaxEntries, 1, 20)));

        var matched = OrderedEntries()
            .Where(item => VersionEquals(item.Version, request))
            .Take(1);
        return Format(matched, $"没有找到程序版本 {request} 的更新日志。");
    }

    private string Format(IEnumerable<ProgramUpdateEntry> source, string emptyMessage = "暂时没有程序更新日志。")
    {
        var entries = source.ToArray();
        if (entries.Length == 0)
            return emptyMessage;

        var builder = new StringBuilder();
        foreach (var item in entries)
        {
            builder.AppendLine($"Hime {item.Version}｜{item.ReleasedAt:yyyy-MM-dd}");
            builder.AppendLine(item.Title);
            if (!string.IsNullOrWhiteSpace(item.Summary))
                builder.AppendLine(item.Summary);
            foreach (var change in item.Changes.Where(value => !string.IsNullOrWhiteSpace(value)).Take(12))
                builder.AppendLine($"• {change}");
            if (item.BreakingChanges.Count > 0)
                builder.AppendLine("注意：" + string.Join("；", item.BreakingChanges.Take(4)));
            if (item.ConfigChanges.Count > 0)
                builder.AppendLine("配置：" + string.Join("；", item.ConfigChanges.Take(4)));
            builder.AppendLine(item.RequiresRestart ? "生效方式：需要重启 Hime" : "生效方式：无需重启");
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

    private IReadOnlyList<ProgramUpdateEntry> OrderedEntries() =>
        Load().OrderByDescending(item => item.ReleasedAt)
            .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IReadOnlyList<ProgramUpdateEntry> Load()
    {
        var configured = _options.CurrentValue.ChangeLogFile;
        if (string.IsNullOrWhiteSpace(configured))
            return [];
        var path = Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
        try
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("Program update log does not exist: {Path}", path);
                return [];
            }

            var writeUtc = File.GetLastWriteTimeUtc(path);
            lock (_sync)
            {
                if (string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase) && _loadedWriteUtc == writeUtc)
                    return _entries;

                var loaded = new List<ProgramUpdateEntry>();
                var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    var item = JsonSerializer.Deserialize<ProgramUpdateEntry>(line);
                    if (item is null || string.IsNullOrWhiteSpace(item.Version) || !versions.Add(item.Version))
                        continue;
                    loaded.Add(item);
                }

                _loadedPath = path;
                _loadedWriteUtc = writeUtc;
                _entries = loaded;
                _logger.LogInformation("Loaded {Count} program update entries from {Path}.", loaded.Count, path);
                return _entries;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not load program update log {Path}.", path);
            return [];
        }
    }

    private static bool VersionEquals(string left, string right) =>
        left.TrimStart('v', 'V').Equals(right.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

    private static string DisplayVersion(string version) =>
        string.IsNullOrWhiteSpace(version) ? "（未配置版本号）" : version;
}

public sealed class ProgramUpdateEntry
{
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset ReleasedAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> Changes { get; set; } = [];
    public List<string> BreakingChanges { get; set; } = [];
    public List<string> ConfigChanges { get; set; } = [];
    public bool RequiresRestart { get; set; } = true;
    public List<string> Verification { get; set; } = [];
}
