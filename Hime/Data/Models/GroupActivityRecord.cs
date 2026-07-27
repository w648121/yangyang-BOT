using LiteDB;

namespace Hime.Data.Models;

/// <summary>
/// 群级主动互动所需的最小状态。该状态只保存近期上下文和频率控制数据，
/// 不赋予模型任何直接发送消息的权限。
/// </summary>
public sealed class GroupActivityRecord
{
    [BsonId]
    public long GroupId { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public DateTime LastIncomingAt { get; set; }

    public DateTime? LastBotReplyAt { get; set; }

    public DateTime? LastProactiveDecisionAt { get; set; }

    public DateTime? LastProactiveAt { get; set; }

    /// <summary>UTC 整点，用于每小时主动消息额度。</summary>
    public DateTime? ProactiveCounterHourUtc { get; set; }

    public int ProactiveSentThisHour { get; set; }

    /// <summary>当前整点时段由程序随机选出的发送目标，范围受配置限制。</summary>
    public int ProactiveTargetThisHour { get; set; }

    /// <summary>UTC 整点，用于限制每群主动小文章的频率。</summary>
    public DateTime? ProactiveArticleCounterHourUtc { get; set; }

    /// <summary>当前小时内已发送的主动小文章数量。</summary>
    public int ProactiveArticlesThisHour { get; set; }

    public List<GroupActivityMessage> RecentMessages { get; set; } = [];
}

/// <summary>提供给主动 Agent 的、经过数量限制的群聊上下文。</summary>
public sealed class GroupActivityMessage
{
    /// <summary>True when this entry was sent by Hime rather than a group member.</summary>
    public bool IsBot { get; set; }

    /// <summary>Native platform message ID. Legacy/activity-only bot entries may be zero.</summary>
    public long MessageId { get; set; }

    public string AccountId { get; set; } = string.Empty;

    public long UserId { get; set; }

    public string Nickname { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public List<string> ImagePaths { get; set; } = [];

    /// <summary>
    /// Canonical, local-only emotion hints inferred from QQ Sticker segments.
    /// They are weak context cues, not a claim about the image's exact semantics.
    /// </summary>
    public List<string> StickerEmotions { get; set; } = [];

    /// <summary>Safe WDv3 expression tags that explain the local emotion hint.</summary>
    public List<string> StickerTags { get; set; } = [];

    public long? ReplyToMessageId { get; set; }

    public long? ReplyToUserId { get; set; }

    /// <summary>Visible text copied from the platform quote/reply card, when available.</summary>
    public string QuotedText { get; set; } = string.Empty;

    public List<long> MentionedUserIds { get; set; } = [];

    public string TopicId { get; set; } = string.Empty;

    public List<long> ConversationParticipants { get; set; } = [];

    public DateTime Time { get; set; }
}
