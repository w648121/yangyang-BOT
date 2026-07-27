namespace Hime.Services;

/// <summary>
/// Runtime-configurable social reasoning before the language model writes a reply.
/// The code owns only scoring and retrieval; intent vocabulary, persona posture,
/// bad habits and examples stay hot-reloadable or database-backed.
/// </summary>
public sealed class SocialIntelligenceOptions
{
    public bool Enabled { get; set; } = true;

    public int MaxGoodExamplesPerPrompt { get; set; } = 3;

    public int MaxBadExamplesPerPrompt { get; set; } = 3;

    public int MaxLearningExamplesToScan { get; set; } = 240;

    public double MinimumExampleSimilarity { get; set; } = 0.16d;

    public string DefaultIntentId { get; set; } = "ordinary_chat";

    public string DefaultPosture { get; set; } = "温柔、克制、先理解再回应";

    public string DefaultReplyGoal { get; set; } = "自然回应当前这句话，不解释内部规则，不强行反问";

    public List<string> CommonStrategyRules { get; set; } = [];

    public List<string> NaturalnessAvoidRules { get; set; } = [];

    public List<string> PersonaBoundaryRules { get; set; } = [];

    public List<string> ReplyMoveChoices { get; set; } =
        ["answer", "tease back softly", "clarify", "repair", "comfort", "refuse gently", "stay brief"];

    public List<SocialIntentRuleOptions> IntentRules { get; set; } = [];

    public ReplyCandidateJudgeOptions CandidateJudge { get; set; } = new();

    public bool IsValid() =>
        MaxGoodExamplesPerPrompt is >= 0 and <= 8 &&
        MaxBadExamplesPerPrompt is >= 0 and <= 8 &&
        MaxLearningExamplesToScan is >= 20 and <= 2000 &&
        MinimumExampleSimilarity is >= 0 and <= 1 &&
        !string.IsNullOrWhiteSpace(DefaultIntentId) &&
        !string.IsNullOrWhiteSpace(DefaultPosture) &&
        !string.IsNullOrWhiteSpace(DefaultReplyGoal) &&
        CommonStrategyRules.Any(IsPresent) &&
        NaturalnessAvoidRules.Any(IsPresent) &&
        PersonaBoundaryRules.Any(IsPresent) &&
        ReplyMoveChoices.Any(IsPresent) &&
        IntentRules.Any(rule => rule.IsValid()) &&
        CandidateJudge.IsValid();

    private static bool IsPresent(string? value) => !string.IsNullOrWhiteSpace(value);
}

public sealed class SocialIntentRuleOptions
{
    public string Id { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Posture { get; set; } = string.Empty;

    public string ReplyGoal { get; set; } = string.Empty;

    public string SocialAction { get; set; } = string.Empty;

    public string StickerIntent { get; set; } = string.Empty;

    public List<string> Markers { get; set; } = [];

    public List<string> Avoid { get; set; } = [];

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Id) &&
        !string.IsNullOrWhiteSpace(Description) &&
        !string.IsNullOrWhiteSpace(Posture) &&
        !string.IsNullOrWhiteSpace(ReplyGoal) &&
        Markers.Any(marker => !string.IsNullOrWhiteSpace(marker));
}

public sealed record SocialIntentResult(
    string IntentId,
    string Description,
    string Posture,
    string ReplyGoal,
    string SocialAction,
    string StickerIntent,
    IReadOnlyList<string> MatchedMarkers,
    IReadOnlyList<string> Avoid,
    double Confidence);

public sealed class ReplyCandidateJudgeOptions
{
    public bool Enabled { get; set; } = true;

    public bool GenerateAlternatives { get; set; } = true;

    public bool UseForExplicitAi { get; set; } = true;

    public bool UseForReactiveConversation { get; set; } = true;

    public int AlternativeCount { get; set; } = 1;

    public int GenerateAlternativesBelowScore { get; set; } = 88;

    public int AcceptFirstAtOrAboveScore { get; set; } = 94;

    public int MinimumImprovementToReplace { get; set; } = 4;

    public int GoodExampleBonus { get; set; } = 8;

    public int BadExamplePenalty { get; set; } = 18;

    public double ExampleSimilarityThreshold { get; set; } = 0.28d;

    public int MaxCandidateCharacters { get; set; } = 320;

    public List<string> AlwaysGenerateForDialogueActs { get; set; } =
        ["Relationship", "Repair", "Support"];

    public List<ReplyCandidatePenaltyRuleOptions> PenaltyRules { get; set; } = [];

    public string AlternativePrompt { get; set; } =
        "Generate one fresh alternative reply to the same latest user message. Output only the final visible reply.";

    public bool IsValid() =>
        AlternativeCount is >= 0 and <= 3 &&
        GenerateAlternativesBelowScore is >= 0 and <= 100 &&
        AcceptFirstAtOrAboveScore is >= 0 and <= 100 &&
        MinimumImprovementToReplace is >= 0 and <= 40 &&
        GoodExampleBonus is >= 0 and <= 40 &&
        BadExamplePenalty is >= 0 and <= 80 &&
        ExampleSimilarityThreshold is >= 0 and <= 1 &&
        MaxCandidateCharacters is >= 40 and <= 2000 &&
        PenaltyRules.All(rule => rule.IsValid()) &&
        !string.IsNullOrWhiteSpace(AlternativePrompt);
}

public sealed class ReplyCandidatePenaltyRuleOptions
{
    public string Id { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public int Penalty { get; set; } = 8;

    public List<string> ReplyMarkers { get; set; } = [];

    public List<string> ExceptWhenUserMentions { get; set; } = [];

    public List<string> IntentIds { get; set; } = [];

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Id) &&
        !string.IsNullOrWhiteSpace(Reason) &&
        Penalty is >= 0 and <= 80 &&
        ReplyMarkers.Any(marker => !string.IsNullOrWhiteSpace(marker));
}
