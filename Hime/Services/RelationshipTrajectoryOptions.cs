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

    public List<string> AllowedInferenceKinds { get; set; } =
    [
        "preference",
        "relationship",
        "address",
        "style",
        "boundary",
        "shared",
        "promise",
        "topic"
    ];

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(SchemaVersion) &&
        !string.IsNullOrWhiteSpace(DatabaseFileName) &&
        RawEventDays > 0 &&
        MaxRecentEventsInPrompt > 0 &&
        MaxEvidenceCharacters > 0 &&
        MaxInferenceItemsInPrompt > 0 &&
        InferenceRetentionDays > 0 &&
        AllowedInferenceKinds.Any(kind => !string.IsNullOrWhiteSpace(kind));
}

/// <summary>
/// Hot-reloadable language signals for relationship event classification.
/// They affect intent selection, never stored identity or database schema.
/// </summary>
public sealed class RelationshipLanguageOptions
{
    public string AssistantDisplayName { get; set; } = string.Empty;

    public List<string> IdentityRequestMarkers { get; set; } = [];

    public List<string> AffectionMarkers { get; set; } = [];

    public List<string> BoundaryMarkers { get; set; } = [];

    public List<string> RepairMarkers { get; set; } = [];

    public List<string> ContinuityMarkers { get; set; } = [];

    public List<string> GroupContextRules { get; set; } = [];

    public Dictionary<string, List<string>> RoleEvidenceRules { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> DefaultResponseImpulses { get; set; } = [];

    public List<RelationshipFamiliarityRule> FamiliarityRules { get; set; } = [];

    public List<string> IdentityRequestRules { get; set; } = [];

    public List<string> FirstIdentityRequestRules { get; set; } = [];

    public List<string> SecondIdentityRequestRules { get; set; } = [];

    public List<string> LaterIdentityRequestRules { get; set; } = [];

    public string AffectionRule { get; set; } = string.Empty;

    public string BoundaryRule { get; set; } = string.Empty;

    public string RepairRule { get; set; } = string.Empty;

    public string ContinuityRule { get; set; } = string.Empty;

    public List<string> FinalEvidenceRules { get; set; } = [];

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(AssistantDisplayName) &&
        IdentityRequestMarkers.Count > 0 &&
        AffectionMarkers.Count > 0 &&
        BoundaryMarkers.Count > 0 &&
        RepairMarkers.Count > 0 &&
        ContinuityMarkers.Count > 0 &&
        GroupContextRules.Any(IsPresent) &&
        RoleEvidenceRules.Count > 0 &&
        RoleEvidenceRules.All(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) && pair.Value.Any(IsPresent)) &&
        DefaultResponseImpulses.Any(IsPresent) &&
        FamiliarityRules.Any(rule =>
            rule.MaximumInteractionCount > 0 && IsPresent(rule.Instruction)) &&
        IdentityRequestRules.Any(IsPresent) &&
        FirstIdentityRequestRules.Any(IsPresent) &&
        SecondIdentityRequestRules.Any(IsPresent) &&
        LaterIdentityRequestRules.Any(IsPresent) &&
        IsPresent(AffectionRule) &&
        IsPresent(BoundaryRule) &&
        IsPresent(RepairRule) &&
        IsPresent(ContinuityRule) &&
        FinalEvidenceRules.Any(IsPresent);

    private static bool IsPresent(string? value) => !string.IsNullOrWhiteSpace(value);
}

public sealed class RelationshipFamiliarityRule
{
    public int MaximumInteractionCount { get; set; }

    public string Instruction { get; set; } = string.Empty;
}
