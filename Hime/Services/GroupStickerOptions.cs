namespace Hime.Services;

/// <summary>群友高频表情包收集与随机回复配置。</summary>
public sealed class GroupStickerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>达到频次阈值后，表情包复制到此目录。</summary>
    public string CollectionDirectory { get; set; } = "resources/images";

    /// <summary>收录后额外同步到这些目录，便于同时保留源码资源和运行目录资源。</summary>
    public List<string> MirrorDirectories { get; set; } = [];

    /// <summary>同一群中相同图片至少出现多少次才收录。</summary>
    public int MinOccurrences { get; set; } = 3;

    /// <summary>
    /// Enables automatic collection of QQ sticker segments. Collected files enter
    /// the review queue only; they are never automatically added to the send pool.
    /// </summary>
    public bool AutoCollectStickers { get; set; } = true;

    /// <summary>后台视觉分析队列上限；满载时保留消息处理速度并记录丢弃日志。</summary>
    public int AnalysisQueueCapacity { get; set; } = 64;

    /// <summary>
    /// Only these file types may remain in the collected sticker pool. Regular
    /// chat-image archival and local visual recognition use a separate path.
    /// </summary>
    public List<string> AllowedExtensions { get; set; } = new() { ".gif" };

    /// <summary>
    /// Requires a human to move a reviewed sticker into Images.ApprovedStickerDirectories
    /// before Hime may send it. Keep this enabled for a cute/anime-only library.
    /// </summary>
    public bool RequireManualApproval { get; set; } = true;

    /// <summary>
    /// Number of days an unapproved file may remain in the review queue. Zero disables
    /// automatic cleanup. Approved files are in a different directory and are untouched.
    /// </summary>
    public int ReviewRetentionDays { get; set; } = 7;

    /// <summary>普通群消息触发随机表情回复的概率，范围 0～1。</summary>
    public double ReplyProbability { get; set; } = 0.03;

    /// <summary>同一群两次随机表情回复的最小间隔秒数。</summary>
    public int MinReplyIntervalSeconds { get; set; } = 600;
}
