using LiteDB;

namespace Hime.Data.Models;

/// <summary>
/// Durable fact describing one reply that was already delivered to a platform.
/// Chat history, group activity and relationship state are projections of this fact.
/// </summary>
public sealed class ConversationTurnRecord
{
    [BsonId]
    public string TurnId { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string ScopeKey { get; set; } = string.Empty;

    public string? SourceMessageId { get; set; }

    public long UserId { get; set; }

    public string Nickname { get; set; } = string.Empty;

    public long? GroupId { get; set; }

    public string TopicId { get; set; } = string.Empty;

    public List<long> ConversationParticipants { get; set; } = [];

    public string UserText { get; set; } = string.Empty;

    public List<string> UserImagePaths { get; set; } = [];

    public string Trigger { get; set; } = string.Empty;

    public string AssistantText { get; set; } = string.Empty;

    public List<string> AssistantImagePaths { get; set; } = [];

    public string? Emotion { get; set; }

    public string Source { get; set; } = string.Empty;

    public long? AssistantMessageId { get; set; }

    public string SocialIntentId { get; set; } = string.Empty;

    public string DialogueAct { get; set; } = string.Empty;

    public string CandidateSummary { get; set; } = string.Empty;

    public DateTime StartedAtUtc { get; set; }

    public DateTime DeliveredAtUtc { get; set; }
}
