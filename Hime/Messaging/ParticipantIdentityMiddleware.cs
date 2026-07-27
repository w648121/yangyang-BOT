using Hime.Services;
using Hime.Data.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Messaging;

public static class MessageContextKeys
{
    public const string ParticipantKind = "participant.kind";
}

/// <summary>
/// Classifies every sender before commands or conversational services run.
/// External bots are observable in transport logs but cannot enter Hime's
/// relationship, natural-reply, image, or model-context pipelines.
/// </summary>
public sealed class ParticipantIdentityMiddleware(
    ParticipantIdentityService participants,
    IGroupActivityService groupActivities,
    IOptionsMonitor<ConversationFocusOptions> focusOptions,
    ILogger<ParticipantIdentityMiddleware> logger) : IMessageMiddleware
{
    public int Order => 125;

    public async Task InvokeAsync(
        MessageContext context,
        MessageHandlerDelegate next,
        CancellationToken cancellationToken)
    {
        var message = context.Message;
        var kind = participants.GetKind(message.Platform, message.SenderId, message.SelfId);
        context.Items[MessageContextKeys.ParticipantKind] = kind;

        if (kind is ParticipantKind.SelfBot or ParticipantKind.ExternalBot &&
            IsSelfAliasTrigger(message.Text, focusOptions.CurrentValue.BotAliases.Prepend(focusOptions.CurrentValue.BotDisplayName)))
        {
            logger.LogInformation(
                "Allowed non-human participant message because it starts with the assistant alias (Platform={Platform}, SenderId={SenderId}, Kind={Kind}, CorrelationId={CorrelationId})",
                message.Platform,
                message.SenderId,
                kind,
                message.CorrelationId);
            await next(context, cancellationToken);
            return;
        }

        if (kind is ParticipantKind.ExternalBot or ParticipantKind.SelfBot)
        {
            if (kind == ParticipantKind.ExternalBot)
                RecordExternalBotObservation(message);
            context.MarkHandled(kind == ParticipantKind.ExternalBot
                ? "external-bot-observed"
                : "self-message-observed");
            logger.LogInformation(
                "Skipped non-human participant before business routing (Platform={Platform}, SenderId={SenderId}, Kind={Kind}, CorrelationId={CorrelationId})",
                message.Platform,
                message.SenderId,
                kind,
                message.CorrelationId);
            return;
        }

        await next(context, cancellationToken);
    }

    public static bool IsSelfAliasTrigger(string? text, IEnumerable<string> aliases)
    {
        var value = text?.TrimStart() ?? string.Empty;
        if (value.Length == 0)
            return false;

        return aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Any(alias => value.StartsWith(alias.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void RecordExternalBotObservation(IncomingMessage message)
    {
        if (!message.GroupId.HasValue || message.SenderId <= 0)
            return;

        var native = message.NativeEvent;
        var groupId = message.GroupId.Value;
        var nickname = native?.Sender?.Nickname ??
                       native?.Member?.Nickname ??
                       message.SenderId.ToString();
        var groupName = native?.Group?.GroupName ?? groupId.ToString();
        groupActivities.RecordIncoming(
            groupId,
            groupName,
            message.SenderId,
            nickname,
            message.Text,
            imagePaths: [],
            messageId: message.MessageId,
            accountId: message.AccountId,
            replyToMessageId: message.ReplyToMessageId,
            replyToUserId: message.ReplyToUserId,
            quotedText: message.QuotedText,
            mentionedUserIds: message.MentionedUserIds,
            topicId: message.TopicId,
            conversationParticipants: message.ConversationParticipants);
    }
}
