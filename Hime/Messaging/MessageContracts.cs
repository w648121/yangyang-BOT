using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Segments;

namespace Hime.Messaging;

/// <summary>
/// Platform-neutral message shape used after an adapter has decoded the native event.
/// NativeEvent is a temporary compatibility bridge while legacy Sora handlers migrate.
/// </summary>
public sealed record IncomingMessage(
    string Platform,
    string CorrelationId,
    string ScopeKey,
    long ConversationKey,
    long MessageId,
    long SelfId,
    long SenderId,
    long? GroupId,
    string Text,
    bool MentionsSelf,
    bool HasAnyMention,
    bool ContainsVisual,
    MessageReceivedEvent NativeEvent)
{
    public bool IsGroup => GroupId.HasValue;
}

public interface ISoraMessageAdapter
{
    IncomingMessage Adapt(MessageReceivedEvent message);
}

public sealed class SoraMessageAdapter : ISoraMessageAdapter
{
    public IncomingMessage Adapt(MessageReceivedEvent e)
    {
        var senderId = e.Sender?.UserId ?? e.Message.SenderId;
        var isGroup = e.Message.SourceType == MessageSourceType.Group;
        long? groupId = isGroup ? e.Message.GroupId : null;
        var scopeKey = isGroup ? $"qq:group:{groupId}" : $"qq:private:{senderId}";
        var correlationId = $"{(isGroup ? "g" : "p")}-{(isGroup ? groupId : senderId)}-{e.Message.MessageId}";
        var body = e.Message.Body;

        return new IncomingMessage(
            "qq",
            correlationId,
            scopeKey,
            isGroup ? e.Message.GroupId : unchecked(long.MinValue + senderId),
            e.Message.MessageId,
            e.SelfId,
            senderId,
            groupId,
            body?.GetText() ?? string.Empty,
            body?.OfType<MentionSegment>().Any(mention => mention.Target == e.SelfId) == true,
            body?.OfType<MentionSegment>().Any() == true,
            body?.OfType<ImageSegment>().Any() == true,
            e);
    }
}

public sealed class MessageContext
{
    public MessageContext(IncomingMessage message) => Message = message;

    public IncomingMessage Message { get; }
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public bool Handled { get; private set; }
    public string Outcome { get; private set; } = "continued";

    public void MarkHandled(string outcome)
    {
        Handled = true;
        Outcome = string.IsNullOrWhiteSpace(outcome) ? "handled" : outcome;
    }
}
