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

    /// <summary>
    /// High-confidence typo/alias corrections used before plot retrieval.
    /// Keeping this in configuration allows new official names to be added without rebuilding Hime.
    /// </summary>
    public Dictionary<string, string> PlotEntityAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["云灵谷"] = "云陵谷",
        ["云岭谷"] = "云陵谷",
        ["云陵古"] = "云陵谷",
        ["漂泊着"] = "漂泊者",
        ["央央"] = "秧秧",
        ["玄凌"] = "玄翎",
        ["玄玲"] = "玄翎",
        ["炽夏"] = "炽霞",
        ["白枝"] = "白芷",
        ["今洲"] = "今州",
        ["黑海安"] = "黑海岸"
    };

    public List<string> PlotQuestionSignals { get; set; } =
    [
        "剧情", "任务", "版本", "初见", "第一次", "相遇", "发生", "当时", "以前",
        "过去", "经历", "故事", "还记得", "记不记得", "是哪", "哪里", "什么时候", "为什么",
        "来信", "写信", "邮件", "祝福", "前瞻", "追月节", "玄方", "玄翎"
    ];

    public List<string> PlotWeakTerms { get; set; } =
    [
        "秧秧", "漂泊者", "事情", "故事", "剧情", "任务", "版本", "发生", "记得", "当时"
    ];

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
