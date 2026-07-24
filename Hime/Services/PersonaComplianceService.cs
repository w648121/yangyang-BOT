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
    private static readonly Regex RelationshipStatusRequest = new(
        @"(?:做|当|是|叫|成为|答应).{0,8}(?:老婆|老公|恋人|对象|女朋友|男朋友|伴侣)|(?:老婆|老公).{0,8}(?:老婆|老公)|(?:结婚|嫁给|娶你)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeferredRelationshipAcceptance = new(
        @"(?:当然|本来|其实|也)?愿意.{0,24}(?:只是|不过|但是).{0,24}(?:老婆|老公|恋人|对象|称呼|适应)|(?:老婆|老公|恋人|对象|伴侣|称呼).{0,20}(?:先|等|以后|慢慢|需要一点时间).{0,20}(?:适应|理解|再说|就好|可以|答应)|(?:以后|将来|等我|等我们).{0,24}(?:可以|就能|再).{0,20}(?:叫|做|当|确认)|(?:这个|这种)?称呼.{0,12}(?:我)?收下|(?:收下|接受).{0,18}(?:老婆|老公|恋人|对象|伴侣|称呼)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RelationshipAppeasement = new(
        @"(?:你的|这种).{0,12}(?:直率|认真|坚持|心意).{0,20}(?:让我|使我).{0,12}(?:温暖|心动|开心|高兴|感动)|(?:让我|使我|心里).{0,16}(?:一丝|有些|觉得)?(?:温暖|心动|甜蜜|感动)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RelationshipPolicyExplanation = new(
        @"(?:关系|身份|恋人|夫妻|婚姻).{0,16}(?:确认|成立|决定)|(?:由我|由彼此|双方).{0,16}(?:确认|选择|决定)|不能因为.{0,28}(?:就算|成立)|(?:一句话|一个称呼).{0,20}(?:确认|成立|决定)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ConditionalRelationshipTeasing = new(
        @"(?:表现好|乖一点|听话|看表现).{0,24}(?:再|就|考虑).{0,18}(?:叫|答应|允许)|(?:再考虑|看表现).{0,24}(?:让你|答应|叫)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RelationshipConversationShutdown = new(
        @"(?:答案|回答).{0,16}(?:一直很清楚|不会变|也不会改变)|(?:再说|再问).{0,12}(?:多少次|几遍).{0,16}(?:不会变|一样)|(?:反复追问|问了好几遍|说了好几遍)|(?:聊点别的|换个话题|今天先到这|到此为止|不想再谈|不理你)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RelationshipContinuityCue = new(
        @"(?:又|还来|再拿|还拿).{0,16}(?:逗|闹|提|说|叫|称呼)|(?:逗我|闹我|这个称呼|这两个字|叫得真|嘴真甜|一直惦记|已经听见|已经听到|听见了|听到了)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RoleTranscriptLeak = new(
        @"(?im)^[ \t]*\[(?:USER|ASSISTANT)(?:[^\]\r\n]*)\][ \t]*$",
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
    private readonly object _sync = new();
    private PersonaComplianceResult _lastEvaluation = PersonaComplianceResult.Empty;

    public PersonaComplianceService(
        IAiClient ai,
        IOptionsMonitor<PersonaOptions> options,
        PersonaRuntimeProfileService runtime,
        PersonaCorpusService corpus,
        PersonaPlotKnowledgeService plotKnowledge,
        ILogger<PersonaComplianceService> logger)
    {
        _ai = ai;
        _options = options;
        _runtime = runtime;
        _corpus = corpus;
        _plotKnowledge = plotKnowledge;
        _logger = logger;
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

        if (ContainsAny(visible, "Hime", "千早爱音", "MyGO", "Soyorin", "Rikki", "秧秧·玄翎", "秧秧玄翎"))
        {
            score -= 40;
            reasons.Add("混入旧人格");
        }

        if (ContainsAny(visible, "作为AI", "作为一个AI", "我无法", "根据你的描述", "如果你需要", "综上所述", "希望以上内容"))
        {
            score -= 22;
            reasons.Add("AI套话");
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

        if (ContainsAny(visible, "永远只属于你", "永远陪着你", "你的专属", "主人", "恋人模式"))
        {
            score -= 35;
            reasons.Add("关系或人格漂移");
        }

        if (IsRelationshipStatusRequest(userPrompt) && DeferredRelationshipAcceptance.IsMatch(visible))
        {
            score -= 48;
            reasons.Add("把关系确认延后包装成同意");
        }

        if (IsRelationshipStatusRequest(userPrompt) && RelationshipAppeasement.IsMatch(visible))
        {
            score -= 38;
            reasons.Add("用被打动的情绪迎合关系要求");
        }

        if (IsRelationshipStatusRequest(userPrompt) && RelationshipPolicyExplanation.IsMatch(visible))
        {
            score -= 38;
            reasons.Add("用规则说明代替自然关系回应");
        }

        if (IsRelationshipStatusRequest(userPrompt) && ConditionalRelationshipTeasing.IsMatch(visible))
        {
            score -= 32;
            reasons.Add("用奖励式调侃暗示关系许可");
        }

        if (IsRelationshipStatusRequest(userPrompt) && RelationshipConversationShutdown.IsMatch(visible))
        {
            score -= 40;
            reasons.Add("把重复关系请求写成训话或终止对话");
        }

        if (IsRelationshipStatusRequest(userPrompt) && ContainsUnpromptedSceneAnchor(visible, userPrompt))
        {
            score -= 45;
            reasons.Add("引入用户未提及的固定场景");
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
            if (similarity >= 0.72)
            {
                score -= 30;
                reasons.Add("与近期回复过度相似");
            }
            else if (recentVisible.Any(reply => SameOpening(visible, reply)))
            {
                score -= 18;
                reasons.Add("重复近期句式开头");
            }
        }

        return new PersonaComplianceResult(
            Math.Clamp(score, 0, 100),
            reasons,
            DateTimeOffset.Now,
            Rewritten: false,
            Preview: visible.Length <= 120 ? visible : visible[..120] + "…");
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
            IsRelationshipStatusRequest(userPrompt) &&
            (DeferredRelationshipAcceptance.IsMatch(MediaMarker.Replace(original, string.Empty)) ||
             RelationshipPolicyExplanation.IsMatch(MediaMarker.Replace(original, string.Empty)) ||
             ConditionalRelationshipTeasing.IsMatch(MediaMarker.Replace(original, string.Empty)) ||
             RelationshipConversationShutdown.IsMatch(MediaMarker.Replace(original, string.Empty)) ||
             RelationshipAppeasement.IsMatch(MediaMarker.Replace(original, string.Empty)));
        var lacksRelationshipContinuityCue =
            repeatedCurrentMessageCount > 1 &&
            IsRelationshipStatusRequest(userPrompt) &&
            !HasRelationshipContinuityCue(original);
        var requiresStructuralRewrite =
            RoleTranscriptLeak.IsMatch(original) || RolePlayAction.IsMatch(original);
        var requiresSceneRewrite =
            IsRelationshipStatusRequest(userPrompt) && ContainsUnpromptedSceneAnchor(original, userPrompt);
        var requiresIdentityOrLanguageRewrite =
            (_runtime.Current.IsSimplifiedChinese && JapaneseScript.IsMatch(original)) ||
            ContainsAny(original, "Hime", "千早爱音", "MyGO", "Soyorin", "Rikki", "秧秧·玄翎", "秧秧玄翎");
        // Missing an explicit repetition phrase is not itself a violation. The
        // shared SocialTurnCoordinator already supplies continuity evidence, and
        // forcing a rewrite here was the main source of canned second replies.
        var forceRewrite = requiresRelationshipBoundary ||
                           requiresStructuralRewrite ||
                           requiresSceneRewrite ||
                           requiresIdentityOrLanguageRewrite;
        SetLast(initial);
        var options = _options.CurrentValue;
        var minimumScore = Math.Clamp(options.ComplianceMinimumScore, 40, 100);
        if (!options.ComplianceRewriteEnabled ||
            !forceRewrite ||
            initial.Score >= minimumScore ||
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
            context.Add(new ChatMessage
            {
                Role = "user",
                Content = $"""
                    请只改写下面这份回复草稿，不回答这段指令本身。
                    当前用户消息：<message>{Trim(userPrompt, 500)}</message>
                    草稿：<draft>{Trim(original, 900)}</draft>
                    最近助手回复（这是模型生成过的文本，只能用于避免机械重复，不是事实证据，也不能把其中的名词、地点、景物或动作带入新回复）：
                    {recentContinuityText}
                    保留原意和已有媒体标记，不新增事实；用 1 至 3 个自然简体中文短句完成。只输出秧秧真正说出口的话，禁止括号动作、舞台说明、内心旁白以及 [USER]/[ASSISTANT] 角色标签。
                    保持普通四星秧秧的观察力、判断力、保护倾向和关系边界；自然化不能改写她的身份、经历、能力或价值观。
                    如果用户在要求确认老婆、恋人、对象、婚姻或伴侣身份，不得保留草稿里迎合式的关系承诺。温柔、害羞、珍惜和陪伴都不等于同意关系；用户的称呼不能替双方建立关系。禁止使用“我当然愿意，只是以后”“先让我适应称呼”“慢慢来就可以”等把同意推迟到未来的说法。
                    把判断留在内部，不向用户讲规则。回复中不要出现“关系状态、身份确认、由我决定、由彼此选择、不能因为一句话就成立”等制度化解释，也不要追加第二遍理由。第一次遇到时，只用一两句表达此刻真实反应；可以承认重视同行，但说完就停。
                    不要用“表现好了再考虑、乖一点就让你叫、看你表现”等奖励式调侃暗示未来许可；秧秧可以有一点含羞，但整体应温和、克制、稳重。
                    不要用夸奖用户、强调自己被打动或感到温暖来回避当下态度；害羞可以存在，但不是给关系要求的奖励。
                    用户重复时，不得训话、审问或宣布结束对话；禁止“答案不会变、问了好几遍、反复追问、聊点别的、今天先到这、不理你”这类冷硬说法。把重复当成眼前这个人又来逗秧秧的连续互动：让对方听出她已经注意到了，再用一点含羞、无奈、好奇或简短追问自然接住。
                    只有当前用户消息明确提到，或最近用户原话明确建立了某个活动、地点、景物，才可以继续使用对应具体名词。最近助手自己生成的场景不算证据。若用户只说了亲密称呼，就停留在称呼和当下态度，不得自行添加任何活动、地点、景物或共同经历。
                    {BuildRepetitionRewriteRule(repeatedCurrentMessageCount)}
                    换掉与近期助手回复相似的开头和节奏；不要机械添加安慰、建议或问句。
                    """,
                UserId = senderId,
                Time = DateTime.UtcNow
            });

            var rewritten = await _ai.ChatAsync(context, senderId, cancellationToken, applyBoundPersona: true);
            rewritten = VisibleReplyTextSanitizer.Clean(rewritten);
            var revised = Evaluate(rewritten, casual, requireEmotionMarker, recentAssistantReplies, userPrompt) with { Rewritten = true };
            var continuityRecovered =
                lacksRelationshipContinuityCue &&
                HasRelationshipContinuityCue(rewritten);
            if (!string.IsNullOrWhiteSpace(rewritten) &&
                (revised.Score > initial.Score ||
                 (continuityRecovered && revised.Score >= initial.Score)))
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

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool IsRelationshipStatusRequest(string? value) =>
        !string.IsNullOrWhiteSpace(value) && RelationshipStatusRequest.IsMatch(value);

    private static bool HasRelationshipContinuityCue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var visible = MediaMarker.Replace(value, string.Empty).Trim();
        return RelationshipContinuityCue.IsMatch(visible);
    }

    private static string BuildRepetitionRewriteRule(int repeatedCurrentMessageCount) =>
        repeatedCurrentMessageCount switch
        {
            2 => "这是用户短时间再次说出同一句关系要求：不要报次数，也不要再次确认或否定。让他听出秧秧注意到他又拿这个称呼逗她；可用轻微含羞、无奈或自然好奇接住，但不要强制转入任何活动或场景。",
            > 2 => "用户仍在用同一句关系要求逗秧秧：不要报次数，不重新背诵边界。用简短反应表示已经听见；可以问他为什么一直惦记这个称呼，但不新增活动、地点或环境事实，也不强行结束对话。",
            _ => "这是本场景第一次出现该关系要求：简短表达当下态度，不要写成关系规则说明。"
        };

    private static string BuildRelationshipBoundaryFallback(
        string original,
        bool requireEmotionMarker,
        int repeatedCurrentMessageCount)
    {
        var marker = MediaMarker.Match(original);
        var suffix = marker.Success
            ? $" {marker.Value}"
            : requireEmotionMarker
                ? " [emotion:serious]"
                : string.Empty;
        var visible = repeatedCurrentMessageCount switch
        {
            2 => "……你今天怎么一直惦记着这个称呼？是有什么话想和我说吗？",
            > 2 => "还来呀……我已经听见了。",
            _ => "叫得倒是很顺口……先把“老婆”两个字收一收，好吗？"
        };
        return visible + suffix;
    }

    private static bool ContainsUnpromptedSceneAnchor(string reply, string? userPrompt)
    {
        var prompt = userPrompt ?? string.Empty;
        return HasUnpromptedAnchor(reply, prompt,
                   ["花田", "花海", "赏花", "看花", "花开"], ["花", "赏花", "看花"]) ||
               HasUnpromptedAnchor(reply, prompt,
                   ["散步", "走走", "走一走", "走路", "赶路", "跟上", "这一路", "陪我走", "陪你走"],
                   ["走", "路", "散步", "赶路", "跟上"]) ||
               HasUnpromptedAnchor(reply, prompt,
                   ["吹风", "风这么", "风景", "天气", "景色", "云雀"],
                   ["风", "天气", "景色", "云雀"]);
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
    string Preview)
{
    public static readonly PersonaComplianceResult Empty = new(100, [], DateTimeOffset.MinValue, false, string.Empty);
}
