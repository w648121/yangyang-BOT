using System.Text.RegularExpressions;
using Hime.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Scores visible replies, removes transport/stage artifacts locally, and uses
/// one bounded model rewrite only for semantic persona or relationship violations.
/// </summary>
public sealed class PersonaComplianceService
{
    private static readonly Regex JapaneseScript = new(@"[\u3040-\u30ff]", RegexOptions.Compiled);
    private static readonly Regex MediaMarker = new(@"\[(?:emotion|sticker(?:-id)?|voice|情绪|情緒|表情|表情包):[^\]\r\n]+\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RoleTranscriptLeak = new(
        @"(?im)^[ \t]*\[(?:USER|ASSISTANT)(?:[^\]\r\n]*)\][^\r\n]*$",
        RegexOptions.Compiled);
    private static readonly Regex RolePlayAction = new(
        @"(?x)(?:（|\()[^（）()\r\n]{0,80}(?:笑|叹|看|望|转|停|走|歪|脸|红|头|眼|手|轻轻|微微|小声|语气|目光|脚步|摇|眨|戳|点头|低头|抬头|偏过|凑近|退开)[^（）()\r\n]{0,80}(?:）|\))|(?:\*|＊)[^*＊\r\n]{1,100}(?:\*|＊)",
        RegexOptions.Compiled);
    private readonly IAiClient _ai;
    private readonly IOptionsMonitor<PersonaOptions> _options;
    private readonly PersonaRuntimeProfileService _runtime;
    private readonly PersonaCorpusService _corpus;
    private readonly PersonaPlotKnowledgeService _plotKnowledge;
    private readonly ILogger<PersonaComplianceService> _logger;
    private readonly PersonaPresenceService? _personaPresence;
    private readonly IOptionsMonitor<PersonaComplianceRuleOptions> _rules;
    private readonly IOptionsMonitor<RelationshipLanguageOptions> _relationshipLanguage;
    private readonly object _sync = new();
    private PersonaComplianceResult _lastEvaluation = PersonaComplianceResult.Empty;

    public PersonaComplianceService(
        IAiClient ai,
        IOptionsMonitor<PersonaOptions> options,
        PersonaRuntimeProfileService runtime,
        PersonaCorpusService corpus,
        PersonaPlotKnowledgeService plotKnowledge,
        IOptionsMonitor<PersonaComplianceRuleOptions> rules,
        IOptionsMonitor<RelationshipLanguageOptions> relationshipLanguage,
        ILogger<PersonaComplianceService> logger,
        PersonaPresenceService? personaPresence = null)
    {
        _ai = ai;
        _options = options;
        _runtime = runtime;
        _corpus = corpus;
        _plotKnowledge = plotKnowledge;
        _rules = rules;
        _relationshipLanguage = relationshipLanguage;
        _logger = logger;
        _personaPresence = personaPresence;
    }

    public PersonaComplianceResult LastEvaluation
    {
        get { lock (_sync) return _lastEvaluation; }
    }

    public PersonaComplianceResult Evaluate(
        string? text,
        bool casual = true,
        bool requireEmotionMarker = false,
        IReadOnlyList<string>? recentAssistantReplies = null,
        string? userPrompt = null)
    {
        var value = text?.Trim() ?? string.Empty;
        var visible = MediaMarker.Replace(value, string.Empty).Trim();
        var score = 100;
        var reasons = new List<string>();
        var issues = PersonaComplianceIssues.None;
        if (string.IsNullOrWhiteSpace(visible))
        {
            score = 0;
            reasons.Add("空回复");
        }

        if (_runtime.Current.IsSimplifiedChinese && JapaneseScript.IsMatch(visible))
        {
            score -= 50;
            reasons.Add("含日文假名");
        }

        if (ContainsAny(visible, _rules.CurrentValue.LegacyPersonaMarkers))
        {
            score -= 40;
            reasons.Add("混入旧人格");
        }

        if (ContainsAny(visible, _rules.CurrentValue.AssistantBoilerplateMarkers))
        {
            score -= 22;
            reasons.Add("AI套话");
            issues |= PersonaComplianceIssues.GenericServiceTone;
        }

        if (MatchesConfiguredGenericReply(visible))
        {
            score -= 24;
            reasons.Add("泛化客服或机械拒绝话术");
            issues |= PersonaComplianceIssues.GenericServiceTone;
        }

        if (RoleTranscriptLeak.IsMatch(visible))
        {
            score -= 80;
            reasons.Add("泄露对话角色标签");
        }

        if (RolePlayAction.IsMatch(visible))
        {
            score -= 45;
            reasons.Add("含动作或舞台描写");
        }

        if (ContainsAny(visible, _rules.CurrentValue.RelationshipDriftMarkers))
        {
            score -= 35;
            reasons.Add("关系或人格漂移");
        }

        if (IsRelationshipStatusRequest(userPrompt))
        {
            foreach (var rule in MatchingRelationshipRules(visible))
            {
                score -= rule.Penalty;
                reasons.Add(rule.Reason);
                issues |= PersonaComplianceIssues.RelationshipBoundary;
            }
        }

        if (IsRelationshipStatusRequest(userPrompt) && ContainsUnpromptedSceneAnchor(visible, userPrompt))
        {
            score -= 45;
            reasons.Add("引入用户未提及的固定场景");
            issues |= PersonaComplianceIssues.SceneInjection;
        }

        if (casual && visible.Length > Math.Clamp(_options.CurrentValue.ComplianceMaxCasualCharacters, 60, 400))
        {
            score -= 24;
            reasons.Add("闲聊过长");
        }

        var lineCount = visible.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (casual && lineCount > 3)
        {
            score -= 12;
            reasons.Add("分段过多");
        }

        if (requireEmotionMarker && !MediaMarker.IsMatch(value))
        {
            score -= 8;
            reasons.Add("缺少情绪标记");
        }

        if (casual && visible.Length >= 8 && recentAssistantReplies is { Count: > 0 })
        {
            var recentVisible = recentAssistantReplies
                .Select(reply => MediaMarker.Replace(reply ?? string.Empty, string.Empty).Trim())
                .Where(reply => reply.Length >= 8)
                .TakeLast(20)
                .ToArray();
            var similarity = recentVisible.Length == 0
                ? 0
                : recentVisible.Max(reply => TextSimilarity(visible, reply));
            var threshold = Math.Clamp(
                _options.CurrentValue.ComplianceDuplicateSimilarityThreshold,
                0.45,
                0.98);
            if (similarity >= threshold)
            {
                score -= 30;
                reasons.Add("与近期回复过度相似");
                issues |= PersonaComplianceIssues.RepetitiveWording;
            }
            else if (recentVisible.Any(reply => SameOpening(visible, reply)))
            {
                score -= 18;
                reasons.Add("重复近期句式开头");
                issues |= PersonaComplianceIssues.RepetitiveWording;
            }
        }

        var presence = _personaPresence?.Assess(visible, userPrompt, casual);
        if (presence?.RequiresRewrite == true)
        {
            score -= 28;
            reasons.AddRange(presence.Reasons);
            issues |= PersonaComplianceIssues.PersonaAbsent;
        }

        return new PersonaComplianceResult(
            Math.Clamp(score, 0, 100),
            reasons,
            DateTimeOffset.Now,
            Rewritten: false,
            Preview: visible.Length <= 120 ? visible : visible[..120] + "…",
            issues);
    }

    public async Task<string> RefineIfNeededAsync(
        string? raw,
        string userPrompt,
        string scene,
        long senderId,
        bool casual,
        bool requireEmotionMarker,
        IReadOnlyList<string>? recentAssistantReplies = null,
        int repeatedCurrentMessageCount = 1,
        CancellationToken cancellationToken = default)
    {
        // Transport leaks and stage directions are deterministic formatting
        // defects. Clean them locally instead of paying for a second model call
        // that can change an otherwise good answer.
        var original = VisibleReplyTextSanitizer.Clean(raw?.Trim());
        var initial = Evaluate(original, casual, requireEmotionMarker, recentAssistantReplies, userPrompt);
        var requiresRelationshipBoundary =
            initial.Issues.HasFlag(PersonaComplianceIssues.RelationshipBoundary);
        var requiresStructuralRewrite =
            RoleTranscriptLeak.IsMatch(original) || RolePlayAction.IsMatch(original);
        var requiresSceneRewrite =
            initial.Issues.HasFlag(PersonaComplianceIssues.SceneInjection);
        var requiresIdentityOrLanguageRewrite =
            (_runtime.Current.IsSimplifiedChinese && JapaneseScript.IsMatch(original)) ||
            ContainsAny(original, _rules.CurrentValue.LegacyPersonaMarkers);
        var requiresNaturalVariationRewrite =
            initial.Issues.HasFlag(PersonaComplianceIssues.GenericServiceTone) ||
            initial.Issues.HasFlag(PersonaComplianceIssues.RepetitiveWording) ||
            initial.Issues.HasFlag(PersonaComplianceIssues.PersonaAbsent);
        // Missing an explicit repetition phrase is not itself a violation. The
        // shared SocialTurnCoordinator already supplies continuity evidence, and
        // forcing a rewrite here was the main source of canned second replies.
        var forceRewrite = requiresRelationshipBoundary ||
                           requiresStructuralRewrite ||
                           requiresSceneRewrite ||
                           requiresIdentityOrLanguageRewrite ||
                           requiresNaturalVariationRewrite;
        SetLast(initial);
        var options = _options.CurrentValue;
        if (!options.ComplianceRewriteEnabled ||
            !forceRewrite ||
            string.IsNullOrWhiteSpace(original))
            return original;

        try
        {
            var context = new List<ChatMessage>
            {
                new()
                {
                    Role = "system",
                    Content = _runtime.BuildFinalInstruction(scene, allowEmotionMarker: requireEmotionMarker),
                    Time = DateTime.UtcNow
                }
            };
            var corpus = _corpus.BuildInstruction(userPrompt, 2);
            if (!string.IsNullOrWhiteSpace(corpus))
                context.Add(new ChatMessage { Role = "system", Content = corpus, Time = DateTime.UtcNow });
            var plotKnowledge = _plotKnowledge.BuildInstruction(userPrompt, 3);
            if (!string.IsNullOrWhiteSpace(plotKnowledge))
                context.Add(new ChatMessage { Role = "system", Content = plotKnowledge, Time = DateTime.UtcNow });
            var recentContinuity = recentAssistantReplies?
                .Select(reply => MediaMarker.Replace(reply ?? string.Empty, string.Empty).Trim())
                .Where(reply => !string.IsNullOrWhiteSpace(reply))
                .TakeLast(2)
                .ToArray() ?? [];
            var recentContinuityText = recentContinuity.Length == 0
                ? "[没有可用的上一轮回复]"
                : string.Join("\n", recentContinuity.Select((reply, index) => $"{index + 1}. {Trim(reply, 240)}"));
            var rewriteRules = string.Join(
                Environment.NewLine,
                _rules.CurrentValue.RewriteRules
                    .Where(rule => !string.IsNullOrWhiteSpace(rule))
                    .Select(rule => rule.Trim()));
            context.Add(new ChatMessage
            {
                Role = "user",
                Content = $"""
                    请只改写下面这份回复草稿，不回答这段指令本身。
                    当前用户消息：<message>{Trim(userPrompt, 500)}</message>
                    草稿：<draft>{Trim(original, 900)}</draft>
                    最近助手回复（这是模型生成过的文本，只能用于避免机械重复，不是事实证据，也不能把其中的名词、地点、景物或动作带入新回复）：
                    {recentContinuityText}
                    保留原意和已有媒体标记，不新增事实；只输出当前人格真正说出口的话，禁止括号动作、舞台说明、内心旁白以及 [USER]/[ASSISTANT] 角色标签。
                    {rewriteRules}
                    {BuildRepetitionRewriteRule(repeatedCurrentMessageCount)}
                    """,
                UserId = senderId,
                Time = DateTime.UtcNow
            });

            var rewritten = await _ai.ChatAsync(
                context,
                senderId,
                cancellationToken,
                applyBoundPersona: true,
                requestProfile: new AiRequestProfile(PreferDirect: true));
            rewritten = VisibleReplyTextSanitizer.Clean(rewritten);
            var revised = Evaluate(rewritten, casual, requireEmotionMarker, recentAssistantReplies, userPrompt) with { Rewritten = true };
            if (!string.IsNullOrWhiteSpace(rewritten) &&
                revised.Score > initial.Score)
            {
                SetLast(revised);
                _logger.LogInformation("Persona compliance rewrite accepted (Scene={Scene}, Score={Score}).", scene, revised.Score);
                return rewritten.Trim();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persona compliance rewrite failed for {Scene}; using original reply.", scene);
        }

        if (requiresRelationshipBoundary)
        {
            _logger.LogWarning(
                "Persona compliance replaced a deferred relationship acceptance with a bounded fallback (Scene={Scene}).",
                scene);
            return BuildRelationshipBoundaryFallback(original, requireEmotionMarker, repeatedCurrentMessageCount);
        }

        if (requiresStructuralRewrite || requiresSceneRewrite)
        {
            _logger.LogWarning(
                "Persona compliance replaced structurally polluted relationship output (Scene={Scene}).",
                scene);
            return IsRelationshipStatusRequest(userPrompt)
                ? BuildRelationshipBoundaryFallback(original, requireEmotionMarker, repeatedCurrentMessageCount)
                : VisibleReplyTextSanitizer.Clean(original);
        }

        return original;
    }

    private void SetLast(PersonaComplianceResult result)
    {
        lock (_sync)
            _lastEvaluation = result;
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms) =>
        terms.Where(term => !string.IsNullOrWhiteSpace(term))
            .Any(term => value.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase));

    private bool MatchesConfiguredGenericReply(string value)
    {
        foreach (var pattern in _options.CurrentValue.ComplianceGenericReplyPatterns
                     .Where(pattern => !string.IsNullOrWhiteSpace(pattern)))
        {
            try
            {
                if (Regex.IsMatch(
                        value,
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(50)))
                    return true;
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Ignored invalid Personas:ComplianceGenericReplyPatterns entry.");
            }
            catch (RegexMatchTimeoutException)
            {
                _logger.LogWarning("Timed out while evaluating a configured generic-reply pattern.");
            }
        }

        return false;
    }

    private IReadOnlyList<PersonaCompliancePatternRule> MatchingRelationshipRules(string value)
    {
        var matches = new List<PersonaCompliancePatternRule>();
        foreach (var rule in _rules.CurrentValue.RelationshipRules
                     .Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern)))
        {
            try
            {
                if (Regex.IsMatch(
                        value,
                        rule.Pattern,
                        RegexOptions.IgnoreCase |
                        RegexOptions.Singleline |
                        RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(50)))
                {
                    matches.Add(rule);
                }
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Ignored invalid PersonaComplianceRules relationship rule {RuleKey}.",
                    rule.Key);
            }
            catch (RegexMatchTimeoutException)
            {
                _logger.LogWarning(
                    "Timed out while evaluating PersonaComplianceRules relationship rule {RuleKey}.",
                    rule.Key);
            }
        }

        return matches;
    }

    private bool IsRelationshipStatusRequest(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        ContainsAny(value, _relationshipLanguage.CurrentValue.IdentityRequestMarkers);

    private string BuildRepetitionRewriteRule(int repeatedCurrentMessageCount)
    {
        var rules = _rules.CurrentValue.RelationshipRepetitionRules;
        return repeatedCurrentMessageCount switch
        {
            2 => rules.SecondRequest,
            > 2 => rules.LaterRequest,
            _ => rules.FirstRequest
        };
    }

    private string BuildRelationshipBoundaryFallback(
        string original,
        bool requireEmotionMarker,
        int repeatedCurrentMessageCount)
    {
        var fallbacks = _rules.CurrentValue.RelationshipFallbacks;
        var marker = MediaMarker.Match(original);
        var suffix = marker.Success
            ? $" {marker.Value}"
            : requireEmotionMarker
                ? $" [emotion:{fallbacks.FallbackEmotion.Trim()}]"
                : string.Empty;
        var visible = repeatedCurrentMessageCount switch
        {
            2 => fallbacks.SecondRequest,
            > 2 => fallbacks.LaterRequest,
            _ => fallbacks.FirstRequest
        };
        return visible + suffix;
    }

    private bool ContainsUnpromptedSceneAnchor(string reply, string? userPrompt)
    {
        var prompt = userPrompt ?? string.Empty;
        return _rules.CurrentValue.SceneAnchors.Any(anchor =>
            HasUnpromptedAnchor(reply, prompt, anchor.ReplyTerms, anchor.PromptTerms));
    }

    private static bool HasUnpromptedAnchor(
        string reply,
        string prompt,
        IReadOnlyList<string> replyTerms,
        IReadOnlyList<string> promptTerms) =>
        replyTerms.Any(term => reply.Contains(term, StringComparison.OrdinalIgnoreCase)) &&
        !promptTerms.Any(term => prompt.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool SameOpening(string left, string right)
    {
        var first = NormalizeForSimilarity(left);
        var second = NormalizeForSimilarity(right);
        var length = Math.Min(8, Math.Min(first.Length, second.Length));
        return length >= 5 && first[..length].Equals(second[..length], StringComparison.Ordinal);
    }

    private static double TextSimilarity(string left, string right)
    {
        var first = BuildBigrams(NormalizeForSimilarity(left));
        var second = BuildBigrams(NormalizeForSimilarity(right));
        if (first.Count == 0 || second.Count == 0)
            return 0;
        var intersection = first.Count(second.Contains);
        var union = first.Count + second.Count - intersection;
        return union == 0 ? 0 : intersection / (double)union;
    }

    private static HashSet<string> BuildBigrams(string value)
    {
        if (value.Length < 2)
            return [];
        return Enumerable.Range(0, value.Length - 1)
            .Select(index => value.Substring(index, 2))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeForSimilarity(string value) =>
        new(value
            .Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character) && !char.IsSymbol(character))
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";
}

public sealed record PersonaComplianceResult(
    int Score,
    IReadOnlyList<string> Reasons,
    DateTimeOffset EvaluatedAt,
    bool Rewritten,
    string Preview,
    PersonaComplianceIssues Issues = PersonaComplianceIssues.None)
{
    public static readonly PersonaComplianceResult Empty =
        new(100, [], DateTimeOffset.MinValue, false, string.Empty, PersonaComplianceIssues.None);
}

[Flags]
public enum PersonaComplianceIssues
{
    None = 0,
    GenericServiceTone = 1,
    RepetitiveWording = 2,
    PersonaAbsent = 4,
    RelationshipBoundary = 8,
    SceneInjection = 16
}
