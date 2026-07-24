using LiteDB;

namespace Hime.Data.Models;

/// <summary>A real inbound or outbound interaction in the new v2 trajectory.</summary>
public sealed class RelationshipEventRecord
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string SchemaVersion { get; set; } = "v2";

    public string TrackId { get; set; } = string.Empty;

    /// <summary>
    /// Stable user-to-Yangyang relationship identity shared by private chat and
    /// group scenes. Raw message retrieval still uses <see cref="TrackId"/> so
    /// private text is never copied into a group prompt.
    /// </summary>
    public string GlobalUserTrackId { get; set; } = string.Empty;

    public string ScopeKey { get; set; } = string.Empty;

    public string Platform { get; set; } = "qq";

    public long MessageId { get; set; }

    public long UserId { get; set; }

    public long? GroupId { get; set; }

    public string Actor { get; set; } = "user";

    public string Kind { get; set; } = "message";

    public string Nickname { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public List<string> ImagePaths { get; set; } = [];

    public string? Emotion { get; set; }

    public bool IsVerified { get; set; } = true;

    public string Source { get; set; } = "qq-event";

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A group-local, directed user-to-user edge. It is updated only from explicit
/// addressing evidence (currently QQ mentions) and is never shared across groups.
/// </summary>
public sealed class GroupSocialEdgeRecord
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string SchemaVersion { get; set; } = "v2";

    public long GroupId { get; set; }

    public long FromUserId { get; set; }

    public long ToUserId { get; set; }

    public int ExplicitInteractionCount { get; set; }

    public string LastEvidenceEventId { get; set; } = string.Empty;

    public DateTime FirstObservedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastObservedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A model-derived possibility. It is never promoted to a verified fact and is
/// always shown to later models separately from real events.
/// </summary>
public sealed class RelationshipInferenceRecord
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string SchemaVersion { get; set; } = "v2";

    public string TrackId { get; set; } = string.Empty;

    public string Scope { get; set; } = "user";

    public string Kind { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public List<string> EvidenceEventIds { get; set; } = [];

    public double Confidence { get; set; } = 0.35d;

    public int SupportCount { get; set; } = 1;

    public string Status { get; set; } = "unverified-inference";

    public string Source { get; set; } = "model-proposal";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastSupportedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ExpiresAtUtc { get; set; }
}

public sealed record YangyangInteractionPlan(
    string PlanId,
    string TrackId,
    string CurrentEventId,
    string PersonaStage,
    string CounterpartRole,
    IReadOnlyList<string> EvidenceEventIds,
    int RepeatedCurrentMessageCount,
    IReadOnlyList<string> ResponseImpulses,
    string PromptContext);
