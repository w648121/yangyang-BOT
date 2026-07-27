namespace Hime.Services;

/// <summary>
/// Configuration-driven catalog of model-facing tools. The catalog controls which
/// already-approved OpenCode tools are published for each request; user text cannot
/// enable a tool or change its permission.
/// </summary>
public sealed class AgentToolsOptions
{
    public bool Enabled { get; set; } = true;

    public string CapabilityConfigPath { get; set; } = "config/tools.json";

    public string GeneralPolicy { get; set; } = string.Empty;

    public List<AgentToolDefinition> Tools { get; set; } = [];

    public AgentKnowledgeOptions Knowledge { get; set; } = new();

    public IReadOnlyList<AgentToolDefinition> EnabledDefinitions() =>
        Enabled
            ? Tools
                .Where(item => item.Enabled && IsSafeToolName(item.Name))
                .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList()
            : [];

    private static bool IsSafeToolName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

public sealed class AgentToolDefinition
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string Instruction { get; set; } = string.Empty;

    /// <summary>
    /// Whether successful calls may be persisted as factual evidence. This does not
    /// grant the tool permission; it only controls the evidence ledger.
    /// </summary>
    public bool CacheEvidence { get; set; }

    /// <summary>
    /// Base confidence assigned to a successful tool result. The value is configuration,
    /// not a model-provided assertion.
    /// </summary>
    public double EvidenceConfidence { get; set; }
}

public sealed class AgentKnowledgeOptions
{
    public bool Enabled { get; set; } = true;

    public bool InjectExactCache { get; set; } = true;

    public bool InjectRecentLedger { get; set; } = true;

    public int CacheTtlHours { get; set; } = 168;

    public int LedgerTtlHours { get; set; } = 72;

    public int RecentLedgerEntries { get; set; } = 1;

    public int MaximumCacheEntries { get; set; } = 2000;

    public int MaximumLedgerEntries { get; set; } = 5000;

    public int MaximumEvidencePerEntry { get; set; } = 4;

    public int MaximumEvidenceCharacters { get; set; } = 3000;

    public int MaximumInjectedCharacters { get; set; } = 6000;

    public double MinimumCacheConfidence { get; set; } = 0.65;

    public double IndependentSourceBonus { get; set; } = 0.04;
}
