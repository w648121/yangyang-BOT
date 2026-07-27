namespace Hime.Services;

/// <summary>
/// 人设配置（绑定 appsettings.json 的 "Personas" 节点）
/// </summary>
public sealed class PersonaOptions
{
    /// <summary>Stable runtime identity used by every reply route.</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>Human-readable active character name used in diagnostics and reports.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Bump this value whenever the active character or its delivery contract changes.
    /// Stored sessions use it to discard old assistant wording without losing user facts.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Visible reply language contract, for example zh-CN.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Default configured voice name for diagnostics and route consistency.</summary>
    public string Voice { get; set; } = string.Empty;

    /// <summary>Optional JSONL file containing verified character utterances.</summary>
    public string CorpusFile { get; set; } = string.Empty;

    /// <summary>Structured, source-graded plot events used for factual lore retrieval.</summary>
    public string PlotKnowledgeFile { get; set; } = string.Empty;

    public int MaxPlotEvents { get; set; } = 3;

    /// <summary>
    /// High-confidence typo/alias corrections used before plot retrieval.
    /// Keeping this in configuration allows new official names to be added without rebuilding Hime.
    /// </summary>
    public Dictionary<string, string> PlotEntityAliases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> PlotQuestionSignals { get; set; } = [];

    /// <summary>
    /// At least one configured identity must appear together with a plot signal
    /// before the anti-hallucination plot guard is enabled.
    /// </summary>
    public List<string> PlotIdentityMarkers { get; set; } = [];

    public List<string> PlotWeakTerms { get; set; } = [];

    /// <summary>
    /// Active-character facts and delivery rules injected at the final prompt
    /// boundary. They live in configuration so changing persona never requires
    /// recompiling the application.
    /// </summary>
    public List<string> RuntimeIdentityRules { get; set; } = [];

    /// <summary>Cross-cutting output guards for the active persona.</summary>
    public List<string> RuntimeGuardRules { get; set; } = [];

    public int MaxCorpusExamples { get; set; } = 4;

    public bool ComplianceRewriteEnabled { get; set; }

    public int ComplianceMaxCasualCharacters { get; set; } = 170;

    /// <summary>
    /// Regex patterns for generic assistant/customer-service wording that should
    /// be rewritten into the active persona's own conversational voice.
    /// </summary>
    public List<string> ComplianceGenericReplyPatterns { get; set; } = [];

    public double ComplianceDuplicateSimilarityThreshold { get; set; } = 0.72;

    /// <summary>Old bot activity before this instant is hidden from the new persona.</summary>
    public DateTimeOffset? ActivatedAtUtc { get; set; }

    /// <summary>人设文件所在目录（相对程序运行目录）</summary>
    public string Directory { get; set; } = "personas";

    /// <summary>兜底人设文件名（不含路径）</summary>
    public string DefaultPersonaFile { get; set; } = string.Empty;

    /// <summary>
    /// QQ 号（字符串，long 不能直接做 JSON key）→ 人设文件名（同目录下）
    /// </summary>
    public Dictionary<string, string> Bindings { get; set; } = new();

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(ProfileId) &&
        !string.IsNullOrWhiteSpace(DisplayName) &&
        !string.IsNullOrWhiteSpace(Version) &&
        !string.IsNullOrWhiteSpace(Language) &&
        !string.IsNullOrWhiteSpace(Voice) &&
        !string.IsNullOrWhiteSpace(DefaultPersonaFile) &&
        PlotQuestionSignals.Any(item => !string.IsNullOrWhiteSpace(item)) &&
        PlotIdentityMarkers.Any(item => !string.IsNullOrWhiteSpace(item)) &&
        RuntimeIdentityRules.Any(item => !string.IsNullOrWhiteSpace(item)) &&
        RuntimeGuardRules.Any(item => !string.IsNullOrWhiteSpace(item));
}
