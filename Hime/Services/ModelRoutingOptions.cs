namespace Hime.Services;

/// <summary>
/// Keeps ordinary chat on the low-latency model and reserves the stronger model
/// for technical or genuinely multi-step requests.
/// </summary>
public sealed class ModelRoutingOptions
{
    public bool Enabled { get; set; } = true;

    public bool UseHighCapabilityForTechnical { get; set; } = true;

    /// <summary>Use the stronger model for nuanced social or emotional context, not for every greeting.</summary>
    public bool UseHighCapabilityForComplexSocial { get; set; } = true;

    public int ComplexSocialPromptMinCharacters { get; set; } = 70;

    public int ComplexPromptMinCharacters { get; set; } = 120;

    public string HighCapabilityProviderId { get; set; } = "hime-glm";

    public string HighCapabilityModelId { get; set; } = "glm-5.2";

    public List<string> TimeMarkers { get; set; } =
    [
        "现在几点", "几点了", "当前时间", "什么时间", "今天几号", "今天星期", "星期几", "日期"
    ];

    public List<string> TechnicalMarkers { get; set; } =
    [
        "c盘", "磁盘", "硬盘", "文件", "路径", "配置", "权限", "工具", "日志", "程序", "机器人",
        "模型", "opencode", "api", "接口", "上下文", "记忆", "表情库", "语音", "点歌", "代码", "报错",
        "错误", "检查", "检测", "状态", "功能"
    ];

    public List<string> ComplexMarkers { get; set; } =
    [
        "分析", "方案", "设计", "架构", "排查", "比较", "优化", "原因", "为什么", "如何", "步骤",
        "上下文", "记忆", "智能体", "agent", "opencode", "代码", "错误", "问题"
    ];

    public List<string> SocialComplexityMarkers { get; set; } =
    [
        "关系", "吵架", "误会", "难过", "焦虑", "害怕", "生气", "委屈", "孤独",
        "喜欢", "讨厌", "道歉", "后悔", "怎么办", "不知道该", "不想说"
    ];
}
