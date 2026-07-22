namespace Hime.Services;

/// <summary>
/// Controls the group-scoped, opt-in banter responder. The allow lists are deliberately
/// explicit so a profile made for one group cannot affect conversations elsewhere.
/// </summary>
public sealed class TargetedInteractionOptions
{
    public bool Enabled { get; set; }

    public List<long> AllowedGroupIds { get; set; } = [];

    public List<long> TargetUserIds { get; set; } = [];

    /// <summary>
    /// Per-group targets. When a group has an entry here, only the listed QQ accounts
    /// are eligible in that group; this prevents independent groups from being
    /// accidentally cross-matched through the legacy global allow lists above.
    /// </summary>
    public Dictionary<long, List<long>> GroupTargetUserIds { get; set; } = [];

    /// <summary>
    /// Targets in these groups use the normal Hime persona instead of the legacy
    /// short-banter style profile.
    /// </summary>
    public List<long> DefaultPersonaGroupIds { get; set; } = [];

    /// <summary>Probability of replying to an eligible message, from 0 to 1.</summary>
    public double ReplyProbability { get; set; } = 0.35;

    public int MinReplyIntervalSeconds { get; set; } = 180;

    /// <summary>
    /// Maximum replies in a rolling hour. Set to 0 or a negative value for no hourly limit.
    /// </summary>
    public int MaxRepliesPerHour { get; set; }

    public int MaxIncomingCharacters { get; set; } = 360;

    public int MaxReplyCharacters { get; set; } = 170;

    public bool UseEmotionSticker { get; set; } = true;

    /// <summary>
    /// When true, image or sticker-only messages from configured members receive a
    /// short generic reaction. The bot does not claim to have seen image details.
    /// </summary>
    public bool ReplyToVisualMessages { get; set; } = true;

    /// <summary>
    /// Markdown file containing aggregated style guidance only. It must not contain raw
    /// chat transcripts or private facts.
    /// </summary>
    public string StyleProfileFile { get; set; } = "personas/interaction-profiles/group-1095402532-3144819436.md";
}
