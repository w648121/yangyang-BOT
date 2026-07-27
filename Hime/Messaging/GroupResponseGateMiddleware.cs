using Hime.Data.Services;
using Hime.Services;
using Microsoft.Extensions.Logging;

namespace Hime.Messaging;

/// <summary>
/// Stops conversational output before any model, image or reply pipeline runs.
/// Control commands remain routable so an administrator can re-enable a stopped group.
/// </summary>
public sealed class GroupResponseGateMiddleware(
    GroupResponseStateService states,
    IGroupActivityService groupActivities,
    ConversationTopicGraph topicGraph,
    IRelationshipTrajectoryService relationshipTrajectory,
    ForwardMessageIngestService forwardMessageIngest,
    ILogger<GroupResponseGateMiddleware> logger) : IMessageMiddleware
{
    public int Order => 150;

    public async Task InvokeAsync(
        MessageContext context,
        MessageHandlerDelegate next,
        CancellationToken cancellationToken)
    {
        var message = context.Message;
        if (!message.IsGroup ||
            IsControlCommand(message.Text) ||
            states.IsEnabled(message.GroupId!.Value))
        {
            await next(context, cancellationToken);
            return;
        }

        var observed = await RecordSilentlyAsync(message, cancellationToken);
        if (observed is not null)
            context.ReplaceMessage(observed);

        context.MarkHandled("group-response-disabled");
        logger.LogDebug(
            "Observed group conversation without responding because database response state is disabled (GroupId={GroupId})",
            message.GroupId.Value);
    }

    private async Task<IncomingMessage?> RecordSilentlyAsync(
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        if (!message.GroupId.HasValue ||
            message.SenderId <= 0 ||
            message.SenderId == message.SelfId)
        {
            return null;
        }

        var native = message.NativeEvent;
        var groupId = message.GroupId.Value;
        var nickname = native.Sender?.Nickname ??
                       native.Member?.Nickname ??
                       message.SenderId.ToString();
        var groupName = native.Group?.GroupName ?? groupId.ToString();
        var addressedUsers = message.MentionedUserIds
            .Where(target => target > 0 && target != message.SelfId)
            .Distinct()
            .ToArray();
        var topic = topicGraph.Resolve(
            message,
            groupActivities.GetRecentMessages(groupId, 24));
        var observed = message with
        {
            TopicId = topic.TopicId,
            ConversationParticipants = topic.Participants
        };

        // A disabled group should be silent, not blind. Keep lightweight
        // observation and merged-forward evidence while skipping image downloads,
        // stickers, AI replies, voice synthesis and business commands.
        relationshipTrajectory.RecordUserMessage(
            message.MessageId,
            message.SenderId,
            nickname,
            groupId,
            message.Text,
            imagePaths: [],
            explicitlyAddressedUserIds: addressedUsers,
            platform: message.Platform);
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
            topicId: topic.TopicId,
            conversationParticipants: topic.Participants);

        await forwardMessageIngest.IngestAsync(
            native,
            observed,
            groupName,
            cancellationToken);
        return observed;
    }

    public static bool IsControlCommand(string? text)
    {
        var command = text?.Trim() ?? string.Empty;
        return command.Equals("/\u54cd\u5e94", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("/\u505c\u6b62", StringComparison.OrdinalIgnoreCase);
    }
}
