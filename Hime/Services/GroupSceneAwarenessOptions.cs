namespace Hime.Services;

public sealed class GroupSceneAwarenessOptions
{
    public bool Enabled { get; set; } = true;

    public int RecentEventLimit { get; set; } = 30;

    public int MaxEventsInPrompt { get; set; } = 6;

    public int EventTtlMinutes { get; set; } = 120;

    public int MaxSummaryCharacters { get; set; } = 180;

    public List<string> CommonPromptRules { get; set; } = [];

    public List<GroupSceneEventRuleOptions> EventRules { get; set; } = [];

    public bool IsValid()
    {
        if (!Enabled)
            return true;

        return RecentEventLimit is >= 1 and <= 200 &&
               MaxEventsInPrompt is >= 1 and <= 20 &&
               EventTtlMinutes is >= 1 and <= 1440 &&
               MaxSummaryCharacters is >= 40 and <= 1000 &&
               CommonPromptRules.Any(rule => !string.IsNullOrWhiteSpace(rule)) &&
               EventRules.Count > 0 &&
               EventRules.All(rule => rule.IsValid());
    }
}

public sealed class GroupSceneEventRuleOptions
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> Markers { get; set; } = [];

    public List<string> NegativeMarkers { get; set; } = [];

    public string SocialHint { get; set; } = string.Empty;

    public string ReplyHint { get; set; } = string.Empty;

    public double Weight { get; set; } = 1.0;

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Id) &&
        !string.IsNullOrWhiteSpace(Kind) &&
        !string.IsNullOrWhiteSpace(Description) &&
        Markers.Any(marker => !string.IsNullOrWhiteSpace(marker)) &&
        Weight > 0;
}
