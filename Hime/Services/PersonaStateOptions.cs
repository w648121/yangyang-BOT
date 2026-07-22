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
}
