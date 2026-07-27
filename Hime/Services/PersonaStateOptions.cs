namespace Hime.Services;

/// <summary>
/// Controls the local, structured state that makes a persona continuous across turns.
/// </summary>
public sealed class PersonaStateOptions
{
    public bool Enabled { get; set; } = true;
    public int MoodHalfLifeMinutes { get; set; } = 120;
    public int ConfirmationsRequired { get; set; } = 2;
    public int MaxFactsInPrompt { get; set; } = 6;
    public int TopicMemoryHours { get; set; } = 24;
    public int MaxFactLength { get; set; } = 120;
    public int MaxGroupMemberCards { get; set; } = 5;
    public Dictionary<string, List<string>> FactAliases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> AllowedUserFactKinds { get; set; } =
        ["preference", "relationship", "address", "style", "boundary", "shared", "promise"];

    public List<string> AllowedGroupFactKinds { get; set; } = ["style", "topic"];

    public List<PersonaFactCaptureRule> FactCaptureRules { get; set; } = [];
}

public sealed class PersonaFactCaptureRule
{
    public bool Enabled { get; set; } = true;

    public string Kind { get; set; } = "shared";

    public string Key { get; set; } = string.Empty;

    public string Source { get; set; } = "explicit-fact";

    public string Pattern { get; set; } = string.Empty;

    public string ValueKind { get; set; } = "text";

    public string ValueTemplate { get; set; } = "{value}";

    public string PeriodGroup { get; set; } = "period";

    public string HourGroup { get; set; } = "hour";

    public string MinuteGroup { get; set; } = "minute";

    public List<string> AfternoonPeriods { get; set; } =
    [
        "下午", "晚上"
    ];

    public List<string> MorningPeriods { get; set; } =
    [
        "早上", "上午", "凌晨"
    ];
}
