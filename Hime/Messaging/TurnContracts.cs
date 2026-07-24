namespace Hime.Messaging;

/// <summary>
/// Describes why a conversational turn entered the social reply pipeline.
/// The value is persisted with assistant messages so later context assembly
/// can distinguish explicit replies, natural participation and proactive turns.
/// </summary>
public enum TurnTrigger
{
    ExplicitAi,
    RuntimeFact,
    Reactive,
    Proactive
}

/// <summary>
/// Stable, platform-neutral identity for one conversational decision.
/// A turn starts when Hime accepts input and ends only after a reply is delivered
/// or deliberately discarded.
/// </summary>
public sealed record TurnContext(
    string TurnId,
    string Platform,
    string AccountId,
    string CorrelationId,
    string ScopeKey,
    string? SourceMessageId,
    long UserId,
    string Nickname,
    long? GroupId,
    string UserText,
    IReadOnlyList<string> UserImagePaths,
    TurnTrigger Trigger,
    DateTimeOffset StartedAt)
{
    public bool IsProactive => Trigger == TurnTrigger.Proactive;

    public static TurnContext FromIncoming(
        IncomingMessage message,
        string userText,
        string nickname,
        TurnTrigger trigger,
        IReadOnlyList<string>? userImagePaths = null) =>
        new(
            Guid.NewGuid().ToString("N"),
            message.Platform,
            message.AccountId,
            message.CorrelationId,
            message.ScopeKey,
            message.MessageId.ToString(),
            message.SenderId,
            string.IsNullOrWhiteSpace(nickname) ? message.SenderId.ToString() : nickname.Trim(),
            message.GroupId,
            userText ?? string.Empty,
            userImagePaths ?? Array.Empty<string>(),
            trigger,
            DateTimeOffset.UtcNow);

    public static TurnContext ForProactive(long groupId, string? groupName = null) =>
        new(
            Guid.NewGuid().ToString("N"),
            "qq",
            "primary",
            $"proactive-{groupId}-{Guid.NewGuid():N}",
            $"qq:group:{groupId}",
            null,
            0,
            string.IsNullOrWhiteSpace(groupName) ? "group" : groupName.Trim(),
            groupId,
            string.Empty,
            Array.Empty<string>(),
            TurnTrigger.Proactive,
            DateTimeOffset.UtcNow);
}

/// <summary>
/// What was actually delivered to the platform for a completed turn.
/// This contract intentionally contains visible content rather than raw model output.
/// </summary>
public sealed record DeliveredTurn(
    string Text,
    IReadOnlyList<string> ImagePaths,
    string? Emotion,
    string Source,
    bool RecordGroupActivity = true)
{
    public static DeliveredTurn TextOnly(string text, string? emotion, string source) =>
        new(text, Array.Empty<string>(), emotion, source);
}
