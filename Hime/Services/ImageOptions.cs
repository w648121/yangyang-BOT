namespace Hime.Services;

/// <summary>
/// 图片资源配置（绑定 appsettings.json 的 "Images" 节点）
/// </summary>
public sealed class ImageOptions
{
    /// <summary>图片资源目录（相对程序运行目录，或绝对路径）</summary>
    public string Directory { get; set; } = "resources/images";

    /// <summary>允许的图片扩展名（小写）</summary>
    public List<string> AllowedExtensions { get; set; } = new() { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

    /// <summary>收到的聊天图片归档目录（相对程序运行目录，或绝对路径）</summary>
    public string IncomingDirectory { get; set; } = "data/chat-images";

    /// <summary>单张聊天图片最大下载字节数</summary>
    public long MaxDownloadBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>Maximum simultaneous image downloads or local copies.</summary>
    public int DownloadConcurrency { get; set; } = 3;

    /// <summary>Maximum resource-to-blob shortcuts retained in memory.</summary>
    public int SessionCacheLimit { get; set; } = 2048;

    /// <summary>
    /// Retention for raw QQ sticker downloads that are waiting to be counted and
    /// reviewed. This does not affect normal chat-image history or approved stickers.
    /// </summary>
    public int StickerArchiveRetentionDays { get; set; } = 7;

    /// <summary>
    /// Maximum number of stickers that an AI reply may request through repeated
    /// emotion markers. This prevents accidental image spam in a group.
    /// </summary>
    public int MaxEmotionImagesPerReply { get; set; } = 3;

    /// <summary>When enabled, only explicitly curated stickers may be sent by Hime.</summary>
    public bool OnlyUseApprovedStickers { get; set; } = true;

    /// <summary>Curated sticker file names. A name is enough because assets are local-only.</summary>
    public List<string> ApprovedStickerFileNames { get; set; } = new() { "smile.gif" };

    /// <summary>Directories for manually curated cute/anime stickers.</summary>
    public List<string> ApprovedStickerDirectories { get; set; } = new() { "resources/images/approved" };

    /// <summary>额外扫描的表情包目录；会递归读取 emotion_*.ext 文件。</summary>
    public List<string> AdditionalDirectories { get; set; } = new() { "resources/collected-stickers" };

    /// <summary>情绪标签到本地表情包文件名的映射</summary>
    public Dictionary<string, string> EmotionMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["happy"] = "smile.gif"
    };
}
