using LiteDB;

namespace Hime.Data.Models;

/// <summary>
/// A human-reviewed reply example. Good examples are structure references;
/// bad examples are anti-patterns. They are runtime learning data, not persona lore.
/// </summary>
public sealed class ReplyLearningRecord
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string PersonaId { get; set; } = "yangyang";

    public string Label { get; set; } = "good";

    public string IntentId { get; set; } = string.Empty;

    public string Scene { get; set; } = string.Empty;

    public string UserMessage { get; set; } = string.Empty;

    public string BotReply { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public long CreatedByUserId { get; set; }

    public long? GroupId { get; set; }

    public string Source { get; set; } = "manual-command";

    /// <summary>
    /// Human-adjusted influence weight. 1.0 means normal; higher values make the
    /// example more likely to be retrieved and used by the candidate judge.
    /// </summary>
    public double Weight { get; set; } = 1.0d;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public int UsedCount { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }
}

public sealed record ReplyLearningExampleDraft(
    string PersonaId,
    string Label,
    string IntentId,
    string Scene,
    string UserMessage,
    string BotReply,
    string Reason,
    long CreatedByUserId,
    long? GroupId,
    string Source = "manual-command");

public sealed record ReplyLearningExampleMatch(
    ReplyLearningRecord Record,
    double Score);

public sealed record ReplyLearningStats(
    int Total,
    int Good,
    int Bad,
    int GroupScoped,
    int Global,
    double AverageWeight);
