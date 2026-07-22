namespace Hime.Services;

/// <summary>
/// Bounds raw model context while preserving a compact, non-authoritative memory of
/// older conversations.
/// </summary>
public sealed class ChatHistoryOptions
{
    /// <summary>
    /// Prefix for session keys. A new namespace starts with empty sessions and
    /// cannot accidentally read the previous persona's conversation records.
    /// </summary>
    public string Namespace { get; set; } = "default";

    public bool ImportLegacySessions { get; set; } = true;

    public DateTimeOffset? AcceptMessagesAfterUtc { get; set; }

    public int RawContextDays { get; set; } = 3;

    /// <summary>
    /// Maximum recent user/assistant messages injected into one model request.
    /// Older raw messages remain persisted and are summarized instead of being
    /// allowed to crowd out the current conversational thread.
    /// </summary>
    public int MaxRecentMessagesInPrompt { get; set; } = 24;

    public int MaxSummaryItems { get; set; } = 12;

    public int MaxSummaryCharacters { get; set; } = 1800;

    public int MaxSourceMessageCharacters { get; set; } = 180;
}
