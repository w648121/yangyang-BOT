namespace Hime.Services;

/// <summary>
/// 人设配置（绑定 appsettings.json 的 "Personas" 节点）
/// </summary>
public sealed class PersonaOptions
{
    /// <summary>Stable runtime identity used by every reply route.</summary>
    public string ProfileId { get; set; } = "hime";

    /// <summary>
    /// Bump this value whenever the active character or its delivery contract changes.
    /// Stored sessions use it to discard old assistant wording without losing user facts.
    /// </summary>
    public string Version { get; set; } = "hime-v1";

    /// <summary>Visible reply language contract, for example zh-CN or ja-zh.</summary>
    public string Language { get; set; } = "ja-zh";

    /// <summary>Default configured voice name for diagnostics and route consistency.</summary>
    public string Voice { get; set; } = "nina";

    /// <summary>Optional JSONL file containing verified character utterances.</summary>
    public string CorpusFile { get; set; } = string.Empty;

    /// <summary>Structured, source-graded plot events used for factual lore retrieval.</summary>
    public string PlotKnowledgeFile { get; set; } = string.Empty;

    public int MaxPlotEvents { get; set; } = 3;

    public int MaxCorpusExamples { get; set; } = 4;

    public bool ComplianceRewriteEnabled { get; set; }

    public int ComplianceMinimumScore { get; set; } = 78;

    public int ComplianceMaxCasualCharacters { get; set; } = 170;

    /// <summary>Old bot activity before this instant is hidden from the new persona.</summary>
    public DateTimeOffset? ActivatedAtUtc { get; set; }

    /// <summary>人设文件所在目录（相对程序运行目录）</summary>
    public string Directory { get; set; } = "personas";

    /// <summary>兜底人设文件名（不含路径）</summary>
    public string DefaultPersonaFile { get; set; } = "hime.md";

    /// <summary>
    /// QQ 号（字符串，long 不能直接做 JSON key）→ 人设文件名（同目录下）
    /// </summary>
    public Dictionary<string, string> Bindings { get; set; } = new();
}
