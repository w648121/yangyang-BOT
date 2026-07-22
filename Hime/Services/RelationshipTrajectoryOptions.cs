namespace Hime.Services;

/// <summary>
/// Controls the evidence-backed relationship trajectory introduced for the
/// Yangyang persona. Each schema store is intentionally isolated from legacy chat
/// summaries and persona-state collections.
/// </summary>
public sealed class RelationshipTrajectoryOptions
{
    public bool Enabled { get; set; } = true;

    public string SchemaVersion { get; set; } = "v3";

    public string DatabaseFileName { get; set; } = "relationship-v3.db";

    public DateTimeOffset? AcceptEventsAfterUtc { get; set; }

    public bool ImportLegacyData { get; set; }

    public string ActivePersonaStage { get; set; } = "xuanling";

    /// <summary>
    /// Every user shares the canonical Rover relationship baseline. Individual
    /// QQ accounts still receive isolated, evidence-only interaction tracks.
    /// </summary>
    public string DefaultCounterpartRole { get; set; } = "rover";

    public int RawEventDays { get; set; } = 3;

    public int MaxRecentEventsInPrompt { get; set; } = 14;

    public int MaxEvidenceCharacters { get; set; } = 2600;

    public int MaxInferenceItemsInPrompt { get; set; } = 5;

    public int InferenceRetentionDays { get; set; } = 30;
}
