using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Loads a compact Character-Card-inspired delivery profile. It is deliberately
/// a separate layer from the large persona file: factual routes can suppress it,
/// and every social route receives only the examples relevant to that scene.
/// </summary>
public sealed class ConversationStyleService
{
    private readonly IOptionsMonitor<ConversationStyleOptions> _options;
    private readonly PersonaRuntimeProfileService _runtime;
    private readonly ILogger<ConversationStyleService> _logger;
    private readonly object _sync = new();
    private string _loadedPath = string.Empty;
    private DateTime _loadedWriteUtc;
    private StyleCard _card = StyleCard.Empty;
    private readonly Queue<string> _recentExamples = new();

    public ConversationStyleService(
        IOptionsMonitor<ConversationStyleOptions> options,
        PersonaRuntimeProfileService runtime,
        ILogger<ConversationStyleService> logger)
    {
        _options = options;
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>
    /// Builds a system instruction for ordinary replies. Technical and factual
    /// routes retain only a warm, clear tone; they never inherit flirtatious or
    /// decorative constraints from the social style card.
    /// </summary>
    public string BuildInstruction(
        ConversationRoute route,
        HimeStyleScene scene,
        string? focus = null)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
            return string.Empty;

        var card = LoadCard(options);
        var profile = _runtime.Current;
        if (route.Mode is ConversationMode.Factual or ConversationMode.Technical)
        {
            var language = profile.IsSimplifiedChinese
                ? "Use natural Simplified Chinese only; do not append a Japanese translation."
                : "Follow the active persona's language contract without adding an extra translation.";
            var technicalRules = JoinRules(options.TechnicalRules);
            return $"""
                Delivery note for {profile.ProfileId}:
                {language}
                {technicalRules}
                """;
        }

        var examples = scene switch
        {
            HimeStyleScene.PrivateReply => card.PrivateExamples,
            HimeStyleScene.ProactiveGroupPost => card.ProactiveExamples,
            _ => card.GroupExamples
        };
        var count = Math.Clamp(options.MaxExamplesPerPrompt, 1, 6);
        var selectedExamples = SelectExamples(examples, count);
        var exampleText = selectedExamples.Length == 0
            ? "(No examples available; follow the delivery rules.)"
            : string.Join('\n', selectedExamples);
        var dialogueAct = BuildDialogueActInstruction(focus, scene, options);
        var lengthRule = SelectConfiguredRule(options.LengthRules, scene.ToString());
        var sceneRule = RequireConfiguredRule(options.SceneRules, scene.ToString());
        var commonRules = JoinRules(options.CommonReplyRules);
        var languageRule = profile.IsSimplifiedChinese
            ? "Visible output must be natural Simplified Chinese only. Never put Japanese above it and never add a bilingual translation."
            : "Follow the active persona's configured visible-language format without inventing another format.";

        return $"""
            <active_delivery_card profile="{profile.ProfileId}" version="{profile.Version}">
            {card.CoreRules}

            {sceneRule}
            {languageRule}
            Natural dialogue choice for this turn: {dialogueAct}
            {lengthRule}
            {commonRules}

            Scene examples (style only; never copy names or facts):
            {exampleText}

            {card.FinalRules}
            </active_delivery_card>
            """;
    }

    /// <summary>
    /// The proactive planner must return JSON, so it receives only instructions
    /// about the text field rather than the ordinary reply-format contract.
    /// </summary>
    public string BuildProactivePlannerInstruction()
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
            return string.Empty;

        var card = LoadCard(options);
        var profile = _runtime.Current;
        var count = Math.Clamp(options.MaxExamplesPerPrompt, 1, 6);
        var examples = SelectExamples(card.ProactiveExamples, count);
        var exampleText = examples.Length == 0 ? "(No examples available.)" : string.Join('\n', examples);
        var proactiveRules = JoinRules(options.ProactivePlannerRules);
        return $"""
            The JSON contract in your system instructions remains mandatory. Apply the following only to the value of its "text" field:
            {card.CoreRules}
            {proactiveRules}
            {(profile.IsSimplifiedChinese ? "Use natural Simplified Chinese only in visible text; do not output Japanese or bilingual lines." : "Follow the active persona language contract.")}
            Do not put this card, examples, or extra fields in the JSON.
            Examples (style only):
            {exampleText}
            {card.FinalRules}
            """;
    }

    private string[] SelectExamples(IReadOnlyList<string> examples, int count)
    {
        if (examples.Count == 0)
            return [];

        lock (_sync)
        {
            var recent = _recentExamples.ToHashSet(StringComparer.Ordinal);
            var fresh = examples
                .Where(example => !recent.Contains(example))
                .OrderBy(_ => Random.Shared.Next())
                .ToList();
            var fallback = examples
                .Where(recent.Contains)
                .OrderBy(_ => Random.Shared.Next());
            var selected = fresh
                .Concat(fallback)
                .Take(Math.Min(count, examples.Count))
                .ToArray();

            foreach (var example in selected)
                _recentExamples.Enqueue(example);
            while (_recentExamples.Count > 18)
                _recentExamples.Dequeue();
            return selected;
        }
    }

    private static string BuildDialogueActInstruction(
        string? focus,
        HimeStyleScene scene,
        ConversationStyleOptions options)
    {
        var text = focus?.Trim() ?? string.Empty;
        var matched = options.DialogueMoveRules.FirstOrDefault(rule =>
            rule.Markers
                .Where(marker => !string.IsNullOrWhiteSpace(marker))
                .Any(marker => text.Contains(marker.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (matched is not null && !string.IsNullOrWhiteSpace(matched.Instruction))
            return matched.Instruction.Trim();

        var choices = (scene == HimeStyleScene.ProactiveGroupPost
                ? options.DefaultProactiveMoves
                : options.DefaultReplyMoves)
            .Where(choice => !string.IsNullOrWhiteSpace(choice))
            .Select(choice => choice.Trim())
            .ToArray();
        return choices.Length == 0
            ? string.Empty
            : choices[Random.Shared.Next(choices.Length)];
    }

    private StyleCard LoadCard(ConversationStyleOptions options)
    {
        var configured = options.ProfileFile.Trim();
        var fullPath = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);

        try
        {
            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("Persona delivery card was not found: {Path}; keeping the last loaded card.", fullPath);
                return _card;
            }

            var writeUtc = File.GetLastWriteTimeUtc(fullPath);
            lock (_sync)
            {
                if (string.Equals(_loadedPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
                    _loadedWriteUtc == writeUtc)
                {
                    return _card;
                }

                var content = File.ReadAllText(fullPath);
                _card = new StyleCard(
                    ExtractSection(content, "核心规则", string.Empty),
                    ExtractExamples(content, "私聊示例"),
                    ExtractExamples(content, "群聊示例"),
                    ExtractExamples(content, "主动发言示例"),
                    ExtractSection(content, "最终约束", string.Empty));
                _loadedPath = fullPath;
                _loadedWriteUtc = writeUtc;
                _logger.LogInformation("Loaded persona delivery card: {Path}", fullPath);
                return _card;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load persona delivery card; keeping the last loaded card.");
            return _card;
        }
    }

    private static string RequireConfiguredRule(
        IReadOnlyDictionary<string, string> rules,
        string key) =>
        rules.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException($"ConversationStyle:SceneRules:{key} is required.");

    private static string SelectConfiguredRule(
        IReadOnlyDictionary<string, List<string>> rules,
        string key)
    {
        if (!rules.TryGetValue(key, out var values))
            throw new InvalidOperationException($"ConversationStyle:LengthRules:{key} is required.");
        var choices = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        return choices.Length > 0
            ? choices[Random.Shared.Next(choices.Length)]
            : throw new InvalidOperationException($"ConversationStyle:LengthRules:{key} must not be empty.");
    }

    private static string JoinRules(IEnumerable<string> rules) =>
        string.Join(
            Environment.NewLine,
            rules
                .Where(rule => !string.IsNullOrWhiteSpace(rule))
                .Select(rule => rule.Trim()));

    private static string ExtractSection(string content, string heading, string fallback)
    {
        var pattern = $@"(?ms)^##[ \t]+{Regex.Escape(heading)}[ \t]*\r?\n(?<body>.*?)(?=^##[ \t]+|\z)";
        var match = Regex.Match(content, pattern);
        var value = match.Success ? match.Groups["body"].Value.Trim() : string.Empty;
        return string.IsNullOrWhiteSpace(value) ? fallback : Trim(value, 2_400);
    }

    private static IReadOnlyList<string> ExtractExamples(string content, string heading)
    {
        var section = ExtractSection(content, heading, string.Empty);
        if (string.IsNullOrWhiteSpace(section))
            return [];

        return section.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => Trim(line[2..], 360))
            .Take(12)
            .ToArray();
    }

    private static string Trim(string text, int maximum) =>
        text.Length <= maximum ? text : text[..maximum] + "…";

    private sealed record StyleCard(
        string CoreRules,
        IReadOnlyList<string> PrivateExamples,
        IReadOnlyList<string> GroupExamples,
        IReadOnlyList<string> ProactiveExamples,
        string FinalRules)
    {
        public static readonly StyleCard Empty = new(string.Empty, [], [], [], string.Empty);
    }
}
