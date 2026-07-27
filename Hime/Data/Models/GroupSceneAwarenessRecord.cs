using LiteDB;

namespace Hime.Data.Models;

/// <summary>
/// Recent, lightweight group-scene events inferred from normal group messages.
/// They help the model understand "why", "say more", and quoted follow-ups
/// without turning the whole chat history into a prompt.
/// </summary>
public sealed class GroupSceneAwarenessRecord
{
    [BsonId]
    public long GroupId { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }

    public List<GroupSceneEventRecord> RecentEvents { get; set; } = [];
}

public sealed class GroupSceneEventRecord
{
    public string Id { get; set; } = string.Empty;

    public string RuleId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string SocialHint { get; set; } = string.Empty;

    public string ReplyHint { get; set; } = string.Empty;

    public long UserId { get; set; }

    public string Nickname { get; set; } = string.Empty;

    public long MessageId { get; set; }

    public long? ReplyToMessageId { get; set; }

    public long? ReplyToUserId { get; set; }

    public List<long> MentionedUserIds { get; set; } = [];

    public string TopicId { get; set; } = string.Empty;

    public List<long> ConversationParticipants { get; set; } = [];

    public double Weight { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
