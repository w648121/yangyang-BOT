namespace Hime.Services;

/// <summary>
/// Controls the small, scene-aware style card appended to Hime replies. The
/// persona remains the source of character identity; this card only keeps its
/// delivery consistent across private chat, group chat, and proactive posts.
/// </summary>
public sealed class ConversationStyleOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Relative paths are resolved from the application directory.</summary>
    public string ProfileFile { get; set; } = string.Empty;

    /// <summary>Only a small, relevant sample is added to a single model request.</summary>
    public int MaxExamplesPerPrompt { get; set; } = 4;

    /// <summary>Ordered, hot-reloadable conversational cue rules.</summary>
    public List<DialogueMoveRuleOptions> DialogueMoveRules { get; set; } = [];

    public List<string> DefaultReplyMoves { get; set; } = [];

    public List<string> DefaultProactiveMoves { get; set; } = [];

    public Dictionary<string, string> SceneRules { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> LengthRules { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> TechnicalRules { get; set; } = [];

    public List<string> CommonReplyRules { get; set; } = [];

    public List<string> ProactivePlannerRules { get; set; } = [];

    public bool HasDialogueMoves() =>
        !string.IsNullOrWhiteSpace(ProfileFile) &&
        DialogueMoveRules.Any(rule =>
            rule.Markers.Any(marker => !string.IsNullOrWhiteSpace(marker)) &&
            !string.IsNullOrWhiteSpace(rule.Instruction)) &&
        DefaultReplyMoves.Any(move => !string.IsNullOrWhiteSpace(move)) &&
        DefaultProactiveMoves.Any(move => !string.IsNullOrWhiteSpace(move)) &&
        Enum.GetNames<HimeStyleScene>().All(name =>
            SceneRules.TryGetValue(name, out var sceneRule) &&
            !string.IsNullOrWhiteSpace(sceneRule) &&
            LengthRules.TryGetValue(name, out var lengthRules) &&
            lengthRules.Any(rule => !string.IsNullOrWhiteSpace(rule))) &&
        TechnicalRules.Any(rule => !string.IsNullOrWhiteSpace(rule)) &&
        CommonReplyRules.Any(rule => !string.IsNullOrWhiteSpace(rule)) &&
        ProactivePlannerRules.Any(rule => !string.IsNullOrWhiteSpace(rule));
}

public sealed class DialogueMoveRuleOptions
{
    public List<string> Markers { get; set; } = [];
    public string Instruction { get; set; } = string.Empty;
}

public enum HimeStyleScene
{
    PrivateReply,
    GroupReply,
    ProactiveGroupPost
}
