namespace Hime.Services;

/// <summary>
/// Short-lived local context for an ordinary image that was sent separately
/// from the follow-up question. This is intentionally not a group-wide image
/// search facility: only the same sender can recover their own recent image.
/// </summary>
public sealed class RecentVisualContextOptions
{
    /// <summary>Local index retaining the sender-to-image association across a bot restart.</summary>
    public string IndexPath { get; set; } = "data/recent-visual-context.json";

    /// <summary>How long an image can be used by an explicit follow-up question.</summary>
    public int RetentionMinutes { get; set; } = 15;

    /// <summary>Bound the number of image paths retained for one message.</summary>
    public int MaxImagesPerMessage { get; set; } = 1;
}
