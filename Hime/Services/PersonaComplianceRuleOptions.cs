namespace Hime.Services;

/// <summary>
/// Hot-reloadable semantic checks for the active persona. C# owns only the
/// evaluation algorithm; character vocabulary and relationship wording live in
/// configuration so a persona update does not require recompilation.
/// </summary>
public sealed class PersonaComplianceRuleOptions
{
    public List<string> LegacyPersonaMarkers { get; set; } = [];

    public List<string> AssistantBoilerplateMarkers { get; set; } = [];

    public List<string> RelationshipDriftMarkers { get; set; } = [];

    public List<PersonaCompliancePatternRule> RelationshipRules { get; set; } = [];

    public List<PersonaSceneAnchorRule> SceneAnchors { get; set; } = [];

    public List<string> RewriteRules { get; set; } = [];

    public PersonaRelationshipFallbacks RelationshipFallbacks { get; set; } = new();

    public PersonaRelationshipRepetitionRules RelationshipRepetitionRules { get; set; } = new();

    public bool IsValid() =>
        RelationshipRules.All(rule =>
            !string.IsNullOrWhiteSpace(rule.Key) &&
            !string.IsNullOrWhiteSpace(rule.Pattern) &&
            !string.IsNullOrWhiteSpace(rule.Reason) &&
            rule.Penalty is >= 1 and <= 100) &&
        SceneAnchors.All(anchor =>
            anchor.ReplyTerms.Any(term => !string.IsNullOrWhiteSpace(term)) &&
            anchor.PromptTerms.Any(term => !string.IsNullOrWhiteSpace(term))) &&
        RewriteRules.Any(rule => !string.IsNullOrWhiteSpace(rule)) &&
        RelationshipFallbacks.IsValid() &&
        RelationshipRepetitionRules.IsValid();
}

public sealed class PersonaCompliancePatternRule
{
    public string Key { get; set; } = string.Empty;

    public string Pattern { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public int Penalty { get; set; } = 30;
}

public sealed class PersonaSceneAnchorRule
{
    public List<string> ReplyTerms { get; set; } = [];

    public List<string> PromptTerms { get; set; } = [];
}

public sealed class PersonaRelationshipFallbacks
{
    public string FallbackEmotion { get; set; } = string.Empty;

    public string FirstRequest { get; set; } = string.Empty;

    public string SecondRequest { get; set; } = string.Empty;

    public string LaterRequest { get; set; } = string.Empty;

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(FallbackEmotion) &&
        !string.IsNullOrWhiteSpace(FirstRequest) &&
        !string.IsNullOrWhiteSpace(SecondRequest) &&
        !string.IsNullOrWhiteSpace(LaterRequest);
}

public sealed class PersonaRelationshipRepetitionRules
{
    public string FirstRequest { get; set; } = string.Empty;

    public string SecondRequest { get; set; } = string.Empty;

    public string LaterRequest { get; set; } = string.Empty;

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(FirstRequest) &&
        !string.IsNullOrWhiteSpace(SecondRequest) &&
        !string.IsNullOrWhiteSpace(LaterRequest);
}
