namespace Hime.Services;

/// <summary>
/// Controls explicit friend-message AI replies. Ordinary private messages are
/// ignored; only messages beginning with the configured trigger enter AI flow.
/// </summary>
public sealed class PrivateConversationOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Required prefix for private AI conversations.</summary>
    public string TriggerPrefix { get; set; } = "~ai";

    /// <summary>
    /// Empty means every friend may use the explicit trigger. Fill this list to
    /// restrict the command to specific QQ accounts.
    /// </summary>
    public List<long> AllowedUserIds { get; set; } = [];

    /// <summary>Wait for this quiet period before replying once to a burst.</summary>
    public int MergeWindowSeconds { get; set; } = 2;

    /// <summary>Bounds memory and prompt growth when a sender floods messages.</summary>
    public int MaxMergedMessages { get; set; } = 8;

    /// <summary>How long an empty explicit trigger waits for the next message.</summary>
    public int WaitTimeoutSeconds { get; set; } = 120;

    /// <summary>Runtime-configurable conversation reset words.</summary>
    public List<string> ResetPrompts { get; set; } = ["clear", "reset", "重置"];

    public bool Allows(long userId) =>
        userId > 0 && (AllowedUserIds.Count == 0 || AllowedUserIds.Contains(userId));
}
