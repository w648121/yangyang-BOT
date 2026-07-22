namespace Hime.Services;

/// <summary>
/// 主动群聊的安全边界。默认关闭，且白名单为空时不会向任何群发消息。
/// </summary>
public sealed class ProactiveAgentOptions
{
    public bool Enabled { get; set; }

    /// <summary>演练模式只输出决策日志，不会发送消息或消耗群额度。</summary>
    public bool DryRun { get; set; }

    /// <summary>只允许这些群使用主动功能；空列表等同于全部禁用。</summary>
    public List<long> AllowedGroupIds { get; set; } = [];

    public int ScanIntervalSeconds { get; set; } = 60;
    public int InitialDelaySeconds { get; set; } = 120;
    public int MaximumGroupsPerScan { get; set; } = 1;
    public int MinimumIntervalMinutes { get; set; } = 12;
    public int ActiveConversationWindowMinutes { get; set; } = 8;
    public int CoolingConversationWindowMinutes { get; set; } = 25;
    public int LivelySpeakerThreshold { get; set; } = 3;
    public int MinimumHourlyMessages { get; set; } = 3;
    public int MaximumHourlyMessages { get; set; } = 5;
    public int QuietHoursStart { get; set; } = 23;
    public int QuietHoursEnd { get; set; } = 8;
    public int ContextMessageLimit { get; set; } = 16;
    public int MaxTextLength { get; set; } = 80;
    public int MaxArticleLength { get; set; } = 260;
    public double DuplicateSimilarityThreshold { get; set; } = 0.72;
    public int MaximumArticlesPerHourPerGroup { get; set; } = 1;
    public bool AllowText { get; set; } = true;
    public bool AllowSticker { get; set; } = true;
    public bool AllowVoice { get; set; } = true;
    public bool AllowArticles { get; set; } = true;
    public double TextWeight { get; set; } = 0.55;
    public double StickerWeight { get; set; } = 0.10;
    public double VoiceWeight { get; set; } = 0.15;
    public double ArticleWeight { get; set; } = 0.20;
    public string Voice { get; set; } = "nina";
}
