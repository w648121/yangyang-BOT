using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

public sealed class PersonaCorpusRoutingOptions
{
    public Dictionary<string, List<string>> EmotionMarkers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> SceneMarkers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> LoreMarkers { get; set; } = [];

    public List<string> RelationshipStatusMarkers { get; set; } = [];

    public List<string> PlotExpositionMarkers { get; set; } = [];

    public List<string> RelationshipSceneMarkers { get; set; } = [];

    public bool IsValid() =>
        EmotionMarkers.Count > 0 &&
        SceneMarkers.Count > 0 &&
        EmotionMarkers.Values.All(HasMarkers) &&
        SceneMarkers.Values.All(HasMarkers);

    private static bool HasMarkers(IEnumerable<string> markers) =>
        markers.Any(marker => !string.IsNullOrWhiteSpace(marker));
}

/// <summary>Loads verified character lines and retrieves a few cadence examples per request.</summary>
public sealed class PersonaCorpusService
{
    private readonly IOptionsMonitor<PersonaOptions> _options;
    private readonly IOptionsMonitor<PersonaCorpusRoutingOptions> _routing;
    private readonly ILogger<PersonaCorpusService> _logger;
    private readonly object _sync = new();
    private string _loadedPath = string.Empty;
    private DateTime _loadedWriteUtc;
    private IReadOnlyList<PersonaCorpusEntry> _entries = [];
    private IReadOnlyList<PersonaCorpusEntry> _lastSelection = [];
    private readonly Queue<string> _recentSelectionIds = new();

    public PersonaCorpusService(
        IOptionsMonitor<PersonaOptions> options,
        IOptionsMonitor<PersonaCorpusRoutingOptions> routing,
        ILogger<PersonaCorpusService> logger)
    {
        _options = options;
        _routing = routing;
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

    private bool IsCadenceCandidate(
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
            ContainsAny(text, _routing.CurrentValue.PlotExpositionMarkers))
        {
            return false;
        }

        // Official letters remain valid plot evidence, but a concrete scene from a
        // letter must not become a generic reply template for "老婆/恋人" small talk.
        if (relationshipStatusFocus &&
            ContainsAny(text, _routing.CurrentValue.RelationshipSceneMarkers))
        {
            return false;
        }

        return true;
    }

    private string ClassifyEmotion(string text)
    {
        foreach (var (emotion, markers) in _routing.CurrentValue.EmotionMarkers)
        {
            if (ContainsAny(text, markers))
                return emotion;
        }
        return "neutral";
    }

    private bool LooksLikeLoreFocus(string text) =>
        ContainsAny(text, _routing.CurrentValue.LoreMarkers);

    private bool LooksLikeRelationshipStatusFocus(string text) =>
        ContainsAny(text, _routing.CurrentValue.RelationshipStatusMarkers);

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

    private string ClassifyScene(string text)
    {
        foreach (var (scene, markers) in _routing.CurrentValue.SceneMarkers)
        {
            if (ContainsAny(text, markers))
                return scene;
        }
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

    private static bool ContainsAny(string text, IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => text.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase));

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
