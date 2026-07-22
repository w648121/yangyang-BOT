using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>Loads verified character lines and retrieves a few cadence examples per request.</summary>
public sealed class PersonaCorpusService
{
    private readonly IOptionsMonitor<PersonaOptions> _options;
    private readonly ILogger<PersonaCorpusService> _logger;
    private readonly object _sync = new();
    private string _loadedPath = string.Empty;
    private DateTime _loadedWriteUtc;
    private IReadOnlyList<PersonaCorpusEntry> _entries = [];
    private IReadOnlyList<PersonaCorpusEntry> _lastSelection = [];
    private readonly Queue<string> _recentSelectionIds = new();

    public PersonaCorpusService(
        IOptionsMonitor<PersonaOptions> options,
        ILogger<PersonaCorpusService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public int Count => Load().Count;

    public IReadOnlyList<PersonaCorpusEntry> LastSelection
    {
        get
        {
            lock (_sync)
                return _lastSelection.ToArray();
        }
    }

    public string BuildInstruction(string? focus, int? maximum = null)
    {
        var selected = Select(focus, maximum);
        if (selected.Count == 0)
            return string.Empty;

        var lines = selected.Select(item => $"- [{item.Scene}/{item.Emotion}] {item.Text}");
        return $"""
            <verified_character_cadence_examples>
            以下是真实角色台词，只用于学习句子节奏、措辞密度和判断方式：
            {string.Join('\n', lines)}
            严禁照搬其中的剧情、身份、关系、地点和事件；回答内容仍只能依据当前对话。
            </verified_character_cadence_examples>
            """;
    }

    public IReadOnlyList<PersonaCorpusEntry> Select(string? focus, int? maximum = null)
    {
        var entries = Load();
        if (entries.Count == 0)
            return [];

        var options = _options.CurrentValue;
        var count = Math.Clamp(maximum ?? options.MaxCorpusExamples, 1, 6);
        var normalizedFocus = focus?.Trim() ?? string.Empty;
        var scene = ClassifyScene(normalizedFocus);
        var emotion = ClassifyEmotion(normalizedFocus);
        var terms = ExtractTerms(normalizedFocus);
        var loreFocus = LooksLikeLoreFocus(normalizedFocus);
        var relationshipStatusFocus = LooksLikeRelationshipStatusFocus(normalizedFocus);
        HashSet<string> recentIds;
        lock (_sync)
            recentIds = _recentSelectionIds.ToHashSet(StringComparer.Ordinal);

        var ranked = entries
            .Where(entry => IsCadenceCandidate(entry, scene, loreFocus, relationshipStatusFocus))
            .Select(entry => new ScoredCorpusEntry(
                entry,
                Score(entry, scene, emotion, terms, recentIds)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => StableTieBreak(item.Entry.Id, normalizedFocus))
            .Take(Math.Max(12, count * 5))
            .ToList();

        var selected = new List<PersonaCorpusEntry>(count);
        while (selected.Count < count && ranked.Count > 0)
        {
            var ordered = ranked
                .Select(item => new
                {
                    Item = item,
                    Adjusted = item.Score - selected.Sum(chosen => CadenceSimilarity(chosen.Text, item.Entry.Text) * 10)
                })
                .OrderByDescending(item => item.Adjusted)
                .ThenBy(_ => Random.Shared.Next())
                .ToList();
            var choicePool = ordered.Take(Math.Min(3, ordered.Count)).ToArray();
            var chosen = choicePool[Random.Shared.Next(choicePool.Length)].Item;
            selected.Add(chosen.Entry);
            ranked.Remove(chosen);
        }

        lock (_sync)
        {
            _lastSelection = selected.ToArray();
            foreach (var entry in selected)
                _recentSelectionIds.Enqueue(entry.Id);
            while (_recentSelectionIds.Count > 36)
                _recentSelectionIds.Dequeue();
            return _lastSelection;
        }
    }

    private static int Score(
        PersonaCorpusEntry entry,
        string scene,
        string emotion,
        IReadOnlyList<string> terms,
        IReadOnlySet<string> recentIds)
    {
        var score = 0;
        if (string.Equals(entry.Scene, scene, StringComparison.OrdinalIgnoreCase))
            score += 10;
        if (string.Equals(entry.Emotion, emotion, StringComparison.OrdinalIgnoreCase))
            score += emotion == "neutral" ? 2 : 7;
        score += terms.Count(term => entry.Text.Contains(term, StringComparison.OrdinalIgnoreCase)) * 3;
        score += entry.Text.Length switch
        {
            >= 6 and <= 38 => 5,
            <= 55 => 2,
            > 70 => -10,
            _ => 0
        };
        if (emotion != "neutral" && entry.Emotion == "neutral")
            score -= 2;
        if (recentIds.Contains(entry.Id))
            score -= 9;
        return score;
    }

    private static bool IsCadenceCandidate(
        PersonaCorpusEntry entry,
        string scene,
        bool loreFocus,
        bool relationshipStatusFocus)
    {
        var text = entry.Text.Trim();
        if (text.Length is < 2 or > 82)
            return false;

        // The complete corpus remains available as canonical source material, but
        // plot exposition is a poor cadence example for ordinary QQ conversation.
        if (!loreFocus && (scene is "casual" or "care" or "relationship") &&
            ContainsAny(text,
                "频谱", "频率能量", "数据坞", "无冠者", "残象", "声骸",
                "令尹", "岁主", "今汐", "瑝览类书", "无音区", "黑海岸"))
        {
            return false;
        }

        // Official letters remain valid plot evidence, but a concrete scene from a
        // letter must not become a generic reply template for "老婆/恋人" small talk.
        if (relationshipStatusFocus &&
            ContainsAny(text,
                "赏花", "花田", "花海", "散步", "走走", "走一走", "吹风",
                "赶路", "跟上", "这一路", "天气", "景色", "云雀"))
        {
            return false;
        }

        return true;
    }

    private static string ClassifyEmotion(string text)
    {
        if (ContainsAny(text, "开心", "高兴", "谢谢", "哈哈", "笑", "完成", "成功", "喜欢")) return "happy";
        if (ContainsAny(text, "难过", "伤心", "失去", "告别", "哭")) return "sad";
        if (ContainsAny(text, "累", "害怕", "担心", "没事", "安慰", "失败", "输了")) return "comforting";
        if (ContainsAny(text, "危险", "错误", "报错", "警告", "认真", "检查", "原因")) return "serious";
        return "neutral";
    }

    private static bool LooksLikeLoreFocus(string text) =>
        ContainsAny(text,
            "鸣潮", "今州", "夜归", "漂泊者", "炽霞", "白芷", "残象",
            "声骸", "无音区", "黑海岸", "流息", "秧秧剧情");

    private static bool LooksLikeRelationshipStatusFocus(string text) =>
        ContainsAny(text,
            "老婆", "老公", "恋人", "对象", "女朋友", "男朋友", "伴侣",
            "结婚", "嫁给", "娶你");

    private static double CadenceSimilarity(string left, string right)
    {
        var leftTerms = ExtractTerms(left).ToHashSet(StringComparer.Ordinal);
        var rightTerms = ExtractTerms(right).ToHashSet(StringComparer.Ordinal);
        if (leftTerms.Count == 0 || rightTerms.Count == 0)
            return 0;
        var intersection = leftTerms.Count(rightTerms.Contains);
        var union = leftTerms.Count + rightTerms.Count - intersection;
        return union == 0 ? 0 : intersection / (double)union;
    }

    private IReadOnlyList<PersonaCorpusEntry> Load()
    {
        var configured = _options.CurrentValue.CorpusFile;
        if (string.IsNullOrWhiteSpace(configured))
            return [];

        var path = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
        try
        {
            if (!File.Exists(path))
                return [];

            var writeUtc = File.GetLastWriteTimeUtc(path);
            lock (_sync)
            {
                if (string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase) &&
                    _loadedWriteUtc == writeUtc)
                    return _entries;

                var loaded = new List<PersonaCorpusEntry>();
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    var entry = JsonSerializer.Deserialize<PersonaCorpusEntry>(line);
                    if (entry is not null && !string.IsNullOrWhiteSpace(entry.Text))
                        loaded.Add(entry);
                }

                _loadedPath = path;
                _loadedWriteUtc = writeUtc;
                _entries = loaded;
                _logger.LogInformation("Loaded {Count} verified persona corpus lines from {Path}.", loaded.Count, path);
                return _entries;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load persona corpus {Path}.", path);
            return [];
        }
    }

    private static string ClassifyScene(string text)
    {
        if (ContainsAny(text, "担心", "放心", "没事", "小心", "谢谢", "抱歉", "安全", "难过")) return "care";
        if (ContainsAny(text, "危险", "敌人", "战斗", "救人", "残象", "受伤")) return "danger";
        if (ContainsAny(text, "发现", "痕迹", "数据", "判断", "推测", "调查", "线索", "为什么", "怎么")) return "investigation";
        if (ContainsAny(text, "小时候", "母亲", "家里", "记得", "愿望", "生日", "回忆")) return "memory";
        if (ContainsAny(text,
                "朋友", "我们", "一起", "喜欢", "认识", "关系",
                "老婆", "老公", "恋人", "对象", "女朋友", "男朋友", "伴侣",
                "结婚", "嫁给", "娶你")) return "relationship";
        return "casual";
    }

    private static IReadOnlyList<string> ExtractTerms(string text)
    {
        var chars = text.Where(ch => ch >= 0x4e00 && ch <= 0x9fff).ToArray();
        if (chars.Length < 2)
            return [];
        return Enumerable.Range(0, chars.Length - 1)
            .Select(index => new string([chars[index], chars[index + 1]]))
            .Distinct(StringComparer.Ordinal)
            .Take(24)
            .ToArray();
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static int StableTieBreak(string id, string focus) =>
        StringComparer.Ordinal.GetHashCode(id + "|" + focus) & int.MaxValue;

    private sealed record ScoredCorpusEntry(PersonaCorpusEntry Entry, int Score);
}

public sealed class PersonaCorpusEntry
{
    public string Id { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Scene { get; set; } = "casual";
    public string Emotion { get; set; } = "neutral";
}
