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
    string AccountId,
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
    IReplyChannel ReplyChannel,
    MessageReceivedEvent NativeEvent)
{
    public bool IsGroup => GroupId.HasValue;

    /// <summary>Message ID quoted by this message, when the adapter exposes it.</summary>
    public long? ReplyToMessageId { get; init; }

    /// <summary>Sender of the quoted message, when supplied by the platform.</summary>
    public long? ReplyToUserId { get; init; }

    /// <summary>Explicit mention targets after platform decoding, including self when present.</summary>
    public IReadOnlyList<long> MentionedUserIds { get; init; } = Array.Empty<long>();

    /// <summary>Visible text embedded in the quote/reply segment. It is untrusted conversation evidence.</summary>
    public string QuotedText { get; init; } = string.Empty;

    /// <summary>Incoming merged-forward IDs that can be expanded through the platform API.</summary>
    public IReadOnlyList<string> ForwardIds { get; init; } = Array.Empty<string>();

    /// <summary>True when this message carries one or more merged-forward references.</summary>
    public bool ContainsMergedForward => ForwardIds.Count > 0;

    /// <summary>Topic assigned by the group conversation graph; empty before focus resolution.</summary>
    public string TopicId { get; init; } = string.Empty;

    /// <summary>Known human participants in the resolved topic, bounded by focus configuration.</summary>
    public IReadOnlyList<long> ConversationParticipants { get; init; } = Array.Empty<long>();
}

public interface ISoraMessageAdapter
{
    IncomingMessage Adapt(MessageReceivedEvent message, string accountId = "primary");
}

public sealed class SoraMessageAdapter : ISoraMessageAdapter
{
    public IncomingMessage Adapt(MessageReceivedEvent e, string accountId = "primary")
    {
        var senderId = e.Sender?.UserId ?? e.Message.SenderId;
        var isGroup = e.Message.SourceType == MessageSourceType.Group;
        long? groupId = isGroup ? e.Message.GroupId : null;
        var scopeKey = isGroup ? $"qq:group:{groupId}" : $"qq:private:{senderId}";
        var correlationId = $"{(isGroup ? "g" : "p")}-{(isGroup ? groupId : senderId)}-{e.Message.MessageId}";
        var body = e.Message.Body;
        var reply = body?.OfType<ReplySegment>().FirstOrDefault();
        var mentionedUsers = body?
            .OfType<MentionSegment>()
            .Select(mention => (long)mention.Target)
            .Where(userId => userId > 0)
            .Distinct()
            .ToArray() ?? [];
        var forwardIds = body?
            .OfType<ForwardSegment>()
            .Select(segment => segment.ForwardId?.Trim())
            .Where(forwardId => !string.IsNullOrWhiteSpace(forwardId))
            .Select(forwardId => forwardId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

        return new IncomingMessage(
            "qq",
            accountId,
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
            new SoraReplyChannel(accountId, e),
            e)
        {
            ReplyToMessageId = reply is not null && (long)reply.TargetId > 0
                ? (long)reply.TargetId
                : null,
            ReplyToUserId = reply is not null && (long)reply.SenderId > 0
                ? (long)reply.SenderId
                : null,
            MentionedUserIds = mentionedUsers,
            QuotedText = reply?.Content?.GetText()?.Trim() ?? string.Empty,
            ForwardIds = forwardIds
        };
    }
}

public sealed class MessageContext
{
    public MessageContext(IncomingMessage message) => Message = message;

    public IncomingMessage Message { get; private set; }
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public bool Handled { get; private set; }
    public string Outcome { get; private set; } = "continued";

    public void MarkHandled(string outcome)
    {
        Handled = true;
        Outcome = string.IsNullOrWhiteSpace(outcome) ? "handled" : outcome;
    }

    public void ReplaceMessage(IncomingMessage message) =>
        Message = message ?? throw new ArgumentNullException(nameof(message));
}
