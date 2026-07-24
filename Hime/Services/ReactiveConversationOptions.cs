namespace Hime.Services;

/// <summary>
/// Boundaries for Hime occasionally joining an ongoing group conversation without being
/// explicitly mentioned. All lists are opt-in and the per-group/user limits are hard gates.
/// </summary>
public sealed class ReactiveConversationOptions
{
    public bool Enabled { get; set; } = true;

    public double BaseReplyProbability { get; set; } = 0.08;

    public double QuestionReplyProbability { get; set; } = 0.28;

    public double BotNameReplyProbability { get; set; } = 0.35;

    public int MinGroupReplyIntervalSeconds { get; set; } = 150;

    public int MinUserReplyIntervalSeconds { get; set; } = 420;

    /// <summary>Set to 0 or lower to remove the rolling hourly group limit.</summary>
    public int MaxRepliesPerGroupPerHour { get; set; } = 6;

    public int ContextMessageLimit { get; set; } = 12;

    public int MinMessageCharacters { get; set; } = 2;

    public int MinNaturalDelaySeconds { get; set; } = 2;

    public int MaxNaturalDelaySeconds { get; set; } = 9;

    public int MaxReplyCharacters { get; set; } = 150;
}
