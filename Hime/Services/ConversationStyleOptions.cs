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
    public string ProfileFile { get; set; } = "personas/hime-style-card.md";

    /// <summary>Only a small, relevant sample is added to a single model request.</summary>
    public int MaxExamplesPerPrompt { get; set; } = 4;
}

public enum HimeStyleScene
{
    PrivateReply,
    GroupReply,
    TargetedGroupReply,
    ProactiveGroupPost
}
