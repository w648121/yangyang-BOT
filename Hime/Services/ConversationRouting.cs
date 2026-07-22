using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Selects a response policy before a message reaches the persona model. This is
/// deliberately deterministic: a user cannot make a casual message become a
/// privileged or technical mode through prompt wording.
/// </summary>
public sealed class ConversationRouter
{
    private readonly ModelRoutingOptions _modelRouting;

    public ConversationRouter(IOptions<ModelRoutingOptions> modelRouting)
    {
        _modelRouting = modelRouting.Value;
    }

    private static readonly string[] TimeMarkers =
    [
        "现在几点", "几点了", "当前时间", "什么时间", "今天几号", "今天星期", "星期几", "日期"
    ];

    private static readonly string[] TechnicalMarkers =
    [
        "c盘", "磁盘", "硬盘", "文件", "路径", "配置", "权限", "工具", "日志", "程序", "机器人",
        "模型", "opencode", "api", "接口", "上下文", "记忆", "表情库", "语音", "点歌", "代码", "报错",
        "错误", "检查", "检测", "状态", "功能"
    ];

    private static readonly string[] ComplexMarkers =
    [
        "分析", "方案", "设计", "架构", "排查", "比较", "优化", "原因", "为什么", "如何", "步骤",
        "上下文", "记忆", "智能体", "agent", "opencode", "代码", "错误", "问题"
    ];

    private static readonly string[] SocialComplexityMarkers =
    [
        "关系", "吵架", "误会", "难过", "焦虑", "害怕", "生气", "委屈", "孤独",
        "喜欢", "讨厌", "道歉", "后悔", "怎么办", "不知道该", "不想说"
    ];

    public ConversationRoute Route(string prompt)
    {
        var text = (prompt ?? string.Empty).Trim();
        var normalized = text.ToLowerInvariant();

        if (TimeMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal)))
            return new ConversationRoute(ConversationMode.Factual, "时间或日期查询", AllowDecorativeMedia: false);

        if (TechnicalMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal)))
            return new ConversationRoute(ConversationMode.Technical, "程序、能力或技术问题", AllowDecorativeMedia: false);

        return new ConversationRoute(ConversationMode.Casual, "普通聊天", AllowDecorativeMedia: true);
    }

    /// <summary>
    /// Returns only a configured allow-list model. Ordinary conversation continues
    /// to use the OpenCode agent default (currently V4 Pro).
    /// </summary>
    public AiRequestProfile SelectModel(ConversationRoute route, string prompt)
    {
        if (!_modelRouting.Enabled ||
            string.IsNullOrWhiteSpace(_modelRouting.HighCapabilityProviderId) ||
            string.IsNullOrWhiteSpace(_modelRouting.HighCapabilityModelId))
        {
            return AiRequestProfile.Default;
        }

        var normalized = (prompt ?? string.Empty).Trim().ToLowerInvariant();
        var technical = _modelRouting.UseHighCapabilityForTechnical && route.Mode == ConversationMode.Technical;
        var complex = normalized.Length >= Math.Clamp(_modelRouting.ComplexPromptMinCharacters, 40, 2000) &&
                      ComplexMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
        var socialMarkerCount = SocialComplexityMarkers.Count(marker => normalized.Contains(marker, StringComparison.Ordinal));
        var complexSocial = _modelRouting.UseHighCapabilityForComplexSocial &&
                            route.Mode == ConversationMode.Casual &&
                            socialMarkerCount > 0 &&
                            (normalized.Length >= Math.Clamp(_modelRouting.ComplexSocialPromptMinCharacters, 20, 500) ||
                             socialMarkerCount >= 2);
        return technical || complex || complexSocial
            ? new AiRequestProfile(_modelRouting.HighCapabilityProviderId, _modelRouting.HighCapabilityModelId)
            : AiRequestProfile.Default;
    }

    public static string BuildSystemPolicy(ConversationRoute route) => route.Mode switch
    {
        ConversationMode.Factual =>
            """
            Response mode: verified factual answer.
            Answer in concise Simplified Chinese. Follow trusted runtime facts supplied by the application.
            Do not role-play, produce Japanese/Chinese bilingual formatting, emojis, images, voice markers, emotion markers, or memory markers.
            Never invent a fact. If a fact is unavailable, say so plainly.
            This runtime policy overrides persona formatting rules for this reply.
            """,
        ConversationMode.Technical =>
            """
            Response mode: serious technical answer.
            Answer in clear Simplified Chinese, with the conclusion first and short steps only when useful.
            Do not role-play, produce Japanese/Chinese bilingual formatting, emojis, images, voice markers, emotion markers, or memory markers.
            Do not claim to have inspected files, disks, logs, web pages, tools, or permissions unless the application supplied the result in trusted context.
            Explicitly distinguish verified facts, unavailable capabilities, and suggestions. This runtime policy overrides persona formatting rules for this reply.
            """,
        _ =>
            """
            Response mode: ordinary conversation.
            Keep the active Hime persona, but stay responsive to the user's concrete question. Do not invent access to tools, files, web pages, or system state.
            """
    };
}

public enum ConversationMode
{
    Casual,
    Factual,
    Technical
}

public sealed record ConversationRoute(ConversationMode Mode, string Reason, bool AllowDecorativeMedia);
