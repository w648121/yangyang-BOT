using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Configurable signals for conversational subtext. These lists describe when a
/// message may be an emotional bid; they never contain a canned reply.
/// </summary>
public sealed class EmotionalPragmaticsOptions
{
    public bool Enabled { get; set; } = true;

    public int ShortCueMaximumCharacters { get; set; } = 12;

    public List<string> SighMarkers { get; set; } =
    [
        "唉", "哎", "唉……", "哎……"
    ];

    public List<string> WithdrawalMarkers { get; set; } =
    [
        "算了", "没什么", "没事", "不想说", "别问", "不用了"
    ];

    public List<string> AdviceOptOutMarkers { get; set; } =
    [
        "别分析", "不要分析", "别劝", "不用劝", "别给建议", "不要建议",
        "不用建议", "别讲道理", "陪我说两句", "只想安静"
    ];

    public List<string> AdviceRequestMarkers { get; set; } =
    [
        "怎么办", "怎么做", "该怎么", "如何", "给点建议", "帮我想想", "你觉得该"
    ];

    public List<string> ExclusionMarkers { get; set; } =
    [
        "就我没有", "只有我没有", "就剩我", "只有我一个", "没人管我",
        "没人理我", "把我落下", "被落下"
    ];

    public List<string> GroupComparisonMarkers { get; set; } =
    [
        "所有人", "大家都", "别人都", "他们都", "每个人"
    ];

    public List<string> VulnerabilityMarkers { get; set; } =
    [
        "心情不好", "难过", "伤心", "委屈", "孤独", "害怕", "焦虑",
        "撑不住", "很累", "失去", "不在了", "淋雨", "失败了"
    ];

    public List<string> IronyMarkers { get; set; } =
    [
        "对对对", "行行行", "呵呵", "你可真聪明", "都是我的错",
        "你一点问题都没有", "可真厉害"
    ];

    public List<string> ContrastMarkers { get; set; } =
    [
        "但是", "可是", "不过", "却", "反而"
    ];

    public List<string> PositiveEventMarkers { get; set; } =
    [
        "考第一", "成功了", "赢了", "通过了", "录取了", "升职了", "中奖了"
    ];

    public List<string> NegativeEventMarkers { get; set; } =
    [
        "不在了", "离开了", "失去", "没人", "没法告诉", "不能分享",
        "难过", "遗憾", "可惜"
    ];

    public List<string> EmptyMentionReplies { get; set; } =
    [
        "嗯？我在呢。",
        "怎么啦？",
        "我在，慢慢说。"
    ];

    public string SupportPreferredEmotion { get; set; } = "comforting";

    public string DefaultPreferredEmotion { get; set; } = "neutral";

    public List<string> SupportAllowedEmotions { get; set; } =
    [
        "comforting", "neutral", "sad"
    ];

    public List<string> DefaultAllowedEmotions { get; set; } =
    [
        "neutral", "comforting"
    ];

    public List<string> SupportMediaIntentTags { get; set; } =
    [
        "gentle", "caring", "calm"
    ];

    public List<string> DefaultMediaIntentTags { get; set; } =
    [
        "calm"
    ];

    public EmotionalReplyQualityOptions ReplyQuality { get; set; } = new();
}

/// <summary>
/// Configurable, high-confidence reply defects. These are deliberately kept in
/// configuration rather than buried in the response pipeline so they can evolve
/// without recompiling the bot.
/// </summary>
public sealed class EmotionalReplyQualityOptions
{
    public bool Enabled { get; set; } = true;

    public List<string> DismissiveMarkers { get; set; } =
    [
        "别叹气", "别难过", "想开点", "雨总会停", "都会过去",
        "没什么大不了", "不要想太多"
    ];

    public List<string> InventedSharedActionMarkers { get; set; } =
    [
        "往我这边", "伞够两个人", "我陪你去", "陪我去", "我们去",
        "一起去", "跟我来", "靠过来", "来我这里", "抱抱你"
    ];

    public List<string> UnsupportedThirdPartyCertaintyMarkers { get; set; } =
    [
        "一定都看在眼里", "一定看得到", "一定会知道", "肯定看得到",
        "肯定会知道", "一定会为你", "肯定会为你"
    ];

    public List<string> UnsolicitedAdviceMarkers { get; set; } =
    [
        "先好好休息", "先歇会", "先休息", "记得带伞", "下次记得",
        "你最好", "不妨试试", "试着放松", "想开一些"
    ];
}

/// <summary>
/// A small, deterministic plan that tells the language model how to respond to
/// the conversational function of a message rather than only its literal event.
/// </summary>
public sealed record EmotionalPragmaticsPlan(
    bool IsActive,
    bool NeedsSupport,
    bool AvoidAdvice,
    bool AvoidQuestions,
    bool UseRecentContext,
    bool RestrictMediaIntensity,
    string PreferredEmotion,
    IReadOnlyList<string> AllowedMediaEmotions,
    IReadOnlyList<string> MediaIntentTags,
    IReadOnlyList<string> Cues,
    string Instruction)
{
    public static EmotionalPragmaticsPlan None { get; } = new(
        false,
        false,
        false,
        false,
        false,
        false,
        "neutral",
        ["neutral"],
        ["calm"],
        [],
        string.Empty);
}

/// <summary>
/// Detects high-confidence conversational cues locally. Ambiguous interpretation
/// remains the model's responsibility, but the model receives an explicit,
/// non-prescriptive plan in the same generation call.
/// </summary>
public sealed class EmotionalPragmaticsPlanner
{
    private readonly IOptionsMonitor<EmotionalPragmaticsOptions> _options;
    private readonly object _replySync = new();
    private string? _lastEmptyMentionReply;

    public EmotionalPragmaticsPlanner(IOptionsMonitor<EmotionalPragmaticsOptions> options)
    {
        _options = options;
    }

    public EmotionalPragmaticsPlan Plan(string? prompt, HimeStyleScene scene)
    {
        var options = _options.CurrentValue;
        var text = prompt?.Trim() ?? string.Empty;
        if (!options.Enabled || string.IsNullOrWhiteSpace(text))
            return EmotionalPragmaticsPlan.None;

        var cues = new List<string>();
        var maximumShortLength = Math.Clamp(options.ShortCueMaximumCharacters, 2, 40);
        var shortCue = text.Length <= maximumShortLength;
        var sigh = shortCue && ContainsAny(text, options.SighMarkers);
        var withdrawal = ContainsAny(text, options.WithdrawalMarkers);
        var adviceOptOut = ContainsAny(text, options.AdviceOptOutMarkers);
        var adviceRequested = ContainsAny(text, options.AdviceRequestMarkers);
        var exclusion = ContainsAny(text, options.ExclusionMarkers) ||
                        (ContainsAny(text, options.GroupComparisonMarkers) &&
                         text.Contains('我') &&
                         ContainsAny(text, ["没有", "没带", "没轮到", "被落下"]));
        var vulnerability = ContainsAny(text, options.VulnerabilityMarkers);
        var irony = ContainsAny(text, options.IronyMarkers);
        var mixedAffect =
            ContainsAny(text, options.ContrastMarkers) &&
            ContainsAny(text, options.PositiveEventMarkers) &&
            ContainsAny(text, options.NegativeEventMarkers);

        AddCue(cues, sigh, "low-information-affect");
        AddCue(cues, withdrawal, "withdrawal");
        AddCue(cues, adviceOptOut, "advice-opt-out");
        AddCue(cues, exclusion, "social-exclusion");
        AddCue(cues, vulnerability, "vulnerability");
        AddCue(cues, irony, "irony");
        AddCue(cues, mixedAffect, "mixed-affect");

        if (cues.Count == 0)
            return EmotionalPragmaticsPlan.None;

        var needsSupport = sigh || withdrawal || adviceOptOut || exclusion || vulnerability || mixedAffect;
        var avoidAdvice = adviceOptOut || (needsSupport && !adviceRequested);
        var avoidQuestions = adviceOptOut || (shortCue && (sigh || withdrawal));
        var useRecentContext = shortCue && (sigh || withdrawal);
        var preferredEmotion = needsSupport
            ? options.SupportPreferredEmotion
            : options.DefaultPreferredEmotion;
        var allowedEmotions = CleanList(needsSupport
            ? options.SupportAllowedEmotions
            : options.DefaultAllowedEmotions);
        var mediaIntentTags = CleanList(needsSupport
            ? options.SupportMediaIntentTags
            : options.DefaultMediaIntentTags);

        var instructions = new List<string>
        {
            "Treat the labels below as tentative conversational cues, not as facts about the user's inner state.",
            "Respond to the likely interpersonal meaning before logistics. Reflect only what the user's words support, and hedge when uncertain.",
            "Do not tell the user to stop feeling something, minimize it with generic optimism, or turn the reply into customer service.",
            "Do not invent weather, scenery, locations, physical actions, shared activities, or past events to make the reply feel caring.",
            "Use ordinary chat language rather than a polished quote, metaphor, slogan, or therapeutic summary.",
            "Do not expose cue labels, analysis, policy, or a psychological diagnosis."
        };
        if (exclusion)
            instructions.Add("The contrast with other people may be the point. Notice possible loneliness, embarrassment, or feeling left out before discussing the practical event.");
        if (sigh)
            instructions.Add("This is a low-information emotional cue. Use the immediately preceding user context, allow silence, and do not demand an explanation.");
        if (withdrawal)
            instructions.Add("Respect the user's retreat. Stay present without chasing, interrogating, or announcing that the conversation is over.");
        if (adviceOptOut)
            instructions.Add("The user explicitly declined analysis or advice. Offer neither, even in disguised form.");
        else if (avoidAdvice)
            instructions.Add("Do not give unsolicited instructions or future-prevention advice. Acknowledge first; offer a practical option only if the user asks.");
        if (irony)
            instructions.Add("Do not read the words literally. Recognize the tension or criticism and repair briefly if the bot may be at fault.");
        if (mixedAffect)
            instructions.Add("Hold the positive event and painful meaning together; do not overwrite either side with congratulations or consolation.");
        if (avoidQuestions)
            instructions.Add("Do not end with a question. One calm statement or a small amount of conversational space is enough.");
        else
            instructions.Add("Ask at most one small, relevant question, and only if it shows care or is needed for immediate safety.");
        if (scene == HimeStyleScene.GroupReply)
            instructions.Add("Keep the warmth public-safe and restrained; do not create private intimacy in front of the group.");
        instructions.Add("If a sticker or voice is used, keep it gentle and low-intensity; avoid cheerful, excited, or celebratory media.");

        return new EmotionalPragmaticsPlan(
            true,
            needsSupport,
            avoidAdvice,
            avoidQuestions,
            useRecentContext,
            needsSupport,
            preferredEmotion,
            allowedEmotions,
            mediaIntentTags,
            cues,
            $"""
             <emotional_pragmatics cues="{string.Join(',', cues)}">
             {string.Join('\n', instructions)}
             </emotional_pragmatics>
             """);
    }

    public string GetEmptyMentionReply()
    {
        var replies = _options.CurrentValue.EmptyMentionReplies
            .Where(reply => !string.IsNullOrWhiteSpace(reply))
            .Select(reply => reply.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (replies.Count == 0)
            return "嗯？我在呢。";

        lock (_replySync)
        {
            var candidates = replies
                .Where(reply => !string.Equals(reply, _lastEmptyMentionReply, StringComparison.Ordinal))
                .ToList();
            if (candidates.Count == 0)
                candidates = replies;
            var selected = candidates[Random.Shared.Next(candidates.Count)];
            _lastEmptyMentionReply = selected;
            return selected;
        }
    }

    private static bool ContainsAny(string value, IEnumerable<string> markers) =>
        markers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Any(marker => value.Contains(marker.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> CleanList(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void AddCue(ICollection<string> cues, bool condition, string cue)
    {
        if (condition)
            cues.Add(cue);
    }
}
