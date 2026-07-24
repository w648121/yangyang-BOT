using Hime.Data.Services;
using Microsoft.Extensions.Logging;
using Sora.Entities.Segments;

namespace Hime.Messaging;

/// <summary>
/// Stops conversational processing before any model, image or reply pipeline runs.
/// Control commands remain routable so an administrator can re-enable a stopped group.
/// </summary>
public sealed class GroupResponseGateMiddleware(
    GroupResponseStateService states,
    IGroupActivityService groupActivities,
    IRelationshipTrajectoryService relationshipTrajectory,
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

        RecordSilently(message);
        context.MarkHandled("group-response-disabled");
        logger.LogDebug(
            "Observed group conversation without responding because database response state is disabled (GroupId={GroupId})",
            message.GroupId.Value);
    }

    private void RecordSilently(IncomingMessage message)
    {
        if (!message.GroupId.HasValue ||
            message.SenderId <= 0 ||
            message.SenderId == message.SelfId)
        {
            return;
        }

        var native = message.NativeEvent;
        var groupId = message.GroupId.Value;
        var nickname = native.Sender?.Nickname ??
                       native.Member?.Nickname ??
                       message.SenderId.ToString();
        var groupName = native.Group?.GroupName ?? groupId.ToString();
        var addressedUsers = native.Message.Body?
            .OfType<MentionSegment>()
            .Select(mention => (long)mention.Target)
            .Where(target => target > 0 && target != message.SelfId)
            .Distinct()
            .ToArray() ?? [];

        // “停止响应”只关闭输出能力，不关闭群上下文观察。这里故意不运行
        // 图片下载、表情随机回复、AI 或任何业务命令，确保停用状态完全静默。
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
            imagePaths: []);
    }

    public static bool IsControlCommand(string? text)
    {
        var command = text?.Trim() ?? string.Empty;
        return command.Equals("/响应", StringComparison.OrdinalIgnoreCase) ||
               command.Equals("/停止", StringComparison.OrdinalIgnoreCase);
    }
}
