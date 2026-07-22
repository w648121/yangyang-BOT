using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Retrieves source-graded story facts separately from cadence examples.
/// It also resolves a small, high-confidence alias table so a typo never becomes a new place or event.
/// </summary>
public sealed class PersonaPlotKnowledgeService
{
    private static readonly IReadOnlyDictionary<string, string> EntityAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["云灵谷"] = "云陵谷",
            ["云岭谷"] = "云陵谷",
            ["云陵古"] = "云陵谷",
            ["漂泊着"] = "漂泊者",
            ["央央"] = "秧秧",
            ["玄凌"] = "玄翎",
            ["玄玲"] = "玄翎",
            ["炽夏"] = "炽霞",
            ["白枝"] = "白芷",
            ["今洲"] = "今州",
            ["黑海安"] = "黑海岸"
        };

    private static readonly string[] PlotQuestionSignals =
    [
        "剧情", "任务", "版本", "初见", "第一次", "相遇", "发生", "当时", "以前",
        "过去", "经历", "故事", "还记得", "记不记得", "是哪", "哪里", "什么时候", "为什么",
        "来信", "写信", "邮件", "祝福", "前瞻", "追月节", "玄方", "玄翎"
    ];

    private static readonly HashSet<string> WeakTerms =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "秧秧", "漂泊者", "事情", "故事", "剧情", "任务", "版本", "发生", "记得", "当时"
        };

    private readonly IOptionsMonitor<PersonaOptions> _options;
    private readonly ILogger<PersonaPlotKnowledgeService> _logger;
    private readonly object _sync = new();
    private string _loadedPath = string.Empty;
    private DateTime _loadedWriteUtc;
    private IReadOnlyList<PersonaPlotEvent> _events = [];

    public PersonaPlotKnowledgeService(
        IOptionsMonitor<PersonaOptions> options,
        ILogger<PersonaPlotKnowledgeService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public int Count => Load().Count;

    public string BuildVersionIndex()
    {
        var groups = Load()
            .GroupBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => VersionSortKey(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}：{group.Count()} 个事件")
            .ToArray();
        return groups.Length == 0
            ? "剧情资料库尚未加载。"
            : "秧秧剧情资料库版本\n" + string.Join('\n', groups) + $"\n合计：{Count} 个结构化事件";
    }

    public string BuildSearchReport(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "用法：/剧情 查 云灵谷";
        var result = Retrieve(query, 5);
        var builder = new StringBuilder();
        if (result.Corrections.Count > 0)
            builder.AppendLine("名称纠正：" + string.Join("；", result.Corrections.Select(item => $"{item.Original} → {item.Canonical}")));
        if (result.Events.Count == 0)
            return builder.Append("未在已核验剧情库中找到匹配事件；机器人应当明确说未查到，不会补造剧情。").ToString();
        builder.AppendLine($"命中 {result.Events.Count} 个事件：");
        foreach (var item in result.Events)
            builder.AppendLine($"- [{item.Version}] {item.Title}｜{item.SourceKind}/{item.Confidence}");
        return builder.ToString().TrimEnd();
    }

    public PersonaPlotRetrievalResult Retrieve(string? focus, int? maximum = null)
    {
        var original = focus?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(original))
            return PersonaPlotRetrievalResult.Empty;

        var normalized = NormalizeAliases(original, out var corrections);
        var compact = Compact(normalized);
        var looksLikePlotQuestion = LooksLikePlotQuestion(compact);
        var terms = ExtractTerms(compact);

        var ranked = Load()
            .Select(item => new { Event = item, Score = Score(item, compact, terms) })
            .Where(item => item.Score >= 18)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Event.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(maximum ?? _options.CurrentValue.MaxPlotEvents, 1, 5))
            .Select(item => item.Event)
            .ToArray();

        return new PersonaPlotRetrievalResult(original, normalized, corrections, ranked, looksLikePlotQuestion);
    }

    public string BuildInstruction(string? focus, int? maximum = null)
    {
        var result = Retrieve(focus, maximum);
        if (result.Events.Count == 0)
        {
            if (!result.LooksLikePlotQuestion)
                return string.Empty;

            return """
                <plot_knowledge_guard>
                当前经过核验的秧秧剧情库没有找到与用户问题对应的事件。
                必须明确说“我目前没有查到对应的剧情记录”或请用户补充线索；不得把陌生地名自动当成真实地点，不得虚构流息、战斗、相遇、承诺或人物关系来填空。
                如果只是名称疑似写错，但没有高置信度别名命中，也不要擅自替用户决定正确名称。
                </plot_knowledge_guard>
                """;
        }

        var builder = new StringBuilder();
        builder.AppendLine("<verified_plot_knowledge>");
        if (result.Corrections.Count > 0)
        {
            builder.Append("高置信度名称纠正：");
            builder.AppendLine(string.Join("；", result.Corrections.Select(item => $"“{item.Original}”应为“{item.Canonical}”")));
            builder.AppendLine("回复时可自然地轻轻纠正一次，然后直接回答正确地点或人物对应的剧情；绝不能为错字另造一个地点或事件。");
        }

        builder.AppendLine("以下是与本轮最相关、经过来源分级的剧情事实。只允许依据这些事实和当前对话回答；不得把缺失部分补成似乎真实的剧情。");
        foreach (var item in result.Events)
        {
            builder.AppendLine($"- [{item.Version}｜{item.Chapter}｜{item.Quest}] {item.Title}");
            builder.AppendLine($"  摘要：{item.Summary}");
            if (item.Facts.Count > 0)
                builder.AppendLine($"  已核验事实：{string.Join("；", item.Facts)}");
            builder.AppendLine($"  来源等级：{item.SourceKind}，置信度：{item.Confidence}；来源：{item.SourceTitle}");
        }

        builder.AppendLine("若用户问到上面没有覆盖的细节，坦白说当前记录未覆盖，不用角色能力、流息感知或合理推测补全。区分‘云陵谷初见’与‘历史上的云陵谷战役/今州建州线索’，不可混写。");
        builder.AppendLine("</verified_plot_knowledge>");
        return builder.ToString();
    }

    private static int Score(PersonaPlotEvent item, string compact, IReadOnlySet<string> terms)
    {
        var score = 0;
        score += MatchScore(compact, item.Title, 50);
        score += MatchScore(compact, item.Quest, 44);
        score += MatchScore(compact, item.Chapter, 34);
        score += MatchScore(compact, item.Version, 34);
        score += MatchScore(compact, item.SourceTitle, 18);
        score += item.Aliases.Sum(alias => MatchScore(compact, alias, 42));
        score += item.Locations.Sum(location => MatchScore(compact, location, 38));
        score += item.Participants
            .Where(participant => !WeakTerms.Contains(participant))
            .Sum(participant => MatchScore(compact, participant, 20));

        var searchable = Compact(string.Join('|',
            item.Title,
            item.Version,
            item.Quest,
            item.Chapter,
            item.Summary,
            item.SourceTitle,
            string.Join('|', item.Aliases),
            string.Join('|', item.Facts),
            string.Join('|', item.Locations),
            string.Join('|', item.Participants)));
        score += terms.Where(term => !WeakTerms.Contains(term) && searchable.Contains(term, StringComparison.OrdinalIgnoreCase)).Count() * 5;
        return score;
    }

    private static int MatchScore(string focus, string? value, int score)
    {
        var candidate = Compact(value ?? string.Empty);
        return candidate.Length >= 2 && focus.Contains(candidate, StringComparison.OrdinalIgnoreCase) ? score : 0;
    }

    private static string NormalizeAliases(string value, out IReadOnlyList<PersonaEntityCorrection> corrections)
    {
        var normalized = value;
        var found = new List<PersonaEntityCorrection>();
        foreach (var pair in EntityAliases)
        {
            if (!normalized.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                continue;
            normalized = normalized.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
            found.Add(new PersonaEntityCorrection(pair.Key, pair.Value));
        }
        corrections = found;
        return normalized;
    }

    private static bool LooksLikePlotQuestion(string value) =>
        PlotQuestionSignals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase)) &&
        (value.Contains("秧秧", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("玄翎", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("漂泊者", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("鸣潮", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlySet<string> ExtractTerms(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in value.Split([' ', '，', '。', '？', '?', '！', '!', '、', '/', '：', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 2)
                result.Add(token);
        }

        var chars = value.Where(character => character is >= '\u4e00' and <= '\u9fff').ToArray();
        for (var index = 0; index + 1 < chars.Length; index++)
            result.Add(new string([chars[index], chars[index + 1]]));
        return result;
    }

    private IReadOnlyList<PersonaPlotEvent> Load()
    {
        var configured = _options.CurrentValue.PlotKnowledgeFile;
        if (string.IsNullOrWhiteSpace(configured))
            return [];

        var path = Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
        try
        {
            if (!File.Exists(path))
                return [];
            var writeUtc = File.GetLastWriteTimeUtc(path);
            lock (_sync)
            {
                if (string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase) && _loadedWriteUtc == writeUtc)
                    return _events;

                var loaded = new List<PersonaPlotEvent>();
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    var item = JsonSerializer.Deserialize<PersonaPlotEvent>(line);
                    if (item is not null && !string.IsNullOrWhiteSpace(item.Title))
                        loaded.Add(item);
                }

                _loadedPath = path;
                _loadedWriteUtc = writeUtc;
                _events = loaded;
                _logger.LogInformation("Loaded {Count} verified persona plot events from {Path}.", loaded.Count, path);
                return _events;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not load persona plot knowledge {Path}.", path);
            return [];
        }
    }

    private static long VersionSortKey(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"(?<major>\d+)\.(?<minor>\d+)");
        return match.Success &&
               long.TryParse(match.Groups["major"].Value, out var major) &&
               long.TryParse(match.Groups["minor"].Value, out var minor)
            ? major * 10_000 + minor
            : -1;
    }

    private static string Compact(string value) =>
        new(value.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)).ToArray());
}

public sealed class PersonaPlotEvent
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Chapter { get; set; } = string.Empty;
    public string Quest { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = [];
    public string Summary { get; set; } = string.Empty;
    public List<string> Participants { get; set; } = [];
    public List<string> Locations { get; set; } = [];
    public List<string> Facts { get; set; } = [];
    public List<string> EvidenceLineIds { get; set; } = [];
    public string SourceTitle { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public string Confidence { get; set; } = "medium";
    public string CanonStatus { get; set; } = "canon";
}

public sealed record PersonaEntityCorrection(string Original, string Canonical);

public sealed record PersonaPlotRetrievalResult(
    string Original,
    string Normalized,
    IReadOnlyList<PersonaEntityCorrection> Corrections,
    IReadOnlyList<PersonaPlotEvent> Events,
    bool LooksLikePlotQuestion)
{
    public static readonly PersonaPlotRetrievalResult Empty = new(string.Empty, string.Empty, [], [], false);
}
