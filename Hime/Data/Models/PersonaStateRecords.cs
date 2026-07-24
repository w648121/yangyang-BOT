using LiteDB;

namespace Hime.Data.Models;

/// <summary>
/// Per-group emotional state and durable group-level context for the persona.
/// </summary>
public sealed class PersonaGroupState
{
    [BsonId]
    public long GroupId { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public string Mood { get; set; } = "neutral";

    /// <summary>0..1. The value decays when it is read.</summary>
    public double MoodIntensity { get; set; }

    public DateTime MoodUpdatedAt { get; set; } = DateTime.UtcNow;

    public string? RecentTopic { get; set; }

    public DateTime? RecentTopicUpdatedAt { get; set; }

    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Relationship continuity for one member. In a group it is intentionally scoped to that group.
/// </summary>
public sealed class PersonaMemberState
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long? GroupId { get; set; }

    public long UserId { get; set; }

    public string Nickname { get; set; } = string.Empty;

    public int InteractionCount { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastRepliedAt { get; set; }
}

/// <summary>
/// A small, reviewable memory fact. Facts are not placed into prompts until confirmed.
/// </summary>
public sealed class PersonaMemoryFact
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long? GroupId { get; set; }

    public long? UserId { get; set; }

    public string Scope { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public int ConfirmationCount { get; set; }

    public bool IsConfirmed { get; set; }

    public string Source { get; set; } = "model-proposal";

    public DateTime FirstProposedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastConfirmedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ExpiresAt { get; set; }

    /// <summary>0..1，用于在大量事实中优先选择真正重要的内容。</summary>
    public double Importance { get; set; } = 0.5d;

    /// <summary>固定事实不会仅因时间较早而被近期低价值事实挤出上下文。</summary>
    public bool IsPinned { get; set; }

    public List<string> Aliases { get; set; } = [];

    public int AccessCount { get; set; }

    public DateTime? LastAccessedAt { get; set; }

    public string? SupersededBy { get; set; }
}

/// <summary>
/// Parsed from a hidden reply marker. It stays a proposal until the service validates it.
/// </summary>
public sealed record PersonaMemoryProposal(
    string Scope,
    string Kind,
    string Key,
    string Value);

/// <summary>
/// 从过期原始消息中提取的可检索长期记忆。仅保存用户原话，不保存模型回复。
/// </summary>
public sealed class LongTermMemoryRecord
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public long UserId { get; set; }

    public long? GroupId { get; set; }

    public string Nickname { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public List<string> SearchTerms { get; set; } = [];

    public double Importance { get; set; } = 0.5d;

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime ArchivedAtUtc { get; set; } = DateTime.UtcNow;

    public int AccessCount { get; set; }

    public DateTime? LastAccessedAtUtc { get; set; }

    public string Source { get; set; } = "chat-compaction";
}
