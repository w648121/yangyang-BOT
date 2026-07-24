using Hime.Data.Services;
using Hime.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Command.Attributes;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Commands;

/// <summary>Administrator-controlled, database-backed group response switch.</summary>
[CommandGroup(Name = "群响应", Prefix = "/")]
public sealed class GroupResponseCommand
{
    private readonly GroupResponseStateService _states;
    private readonly IGroupActivityService _activities;
    private readonly AdminOptions _admin;
    private readonly ILogger<GroupResponseCommand> _logger;

    public GroupResponseCommand(
        GroupResponseStateService states,
        IGroupActivityService activities,
        IOptions<AdminOptions> admin,
        ILogger<GroupResponseCommand> logger)
    {
        _states = states;
        _activities = activities;
        _admin = admin.Value;
        _logger = logger;
    }

    [Command(
        Expressions = ["响应"],
        MatchType = Sora.Core.Enums.MatchType.Full,
        Description = "管理员开启当前群的 AI、自然接话和主动响应")]
    public ValueTask Enable(MessageReceivedEvent e) => ChangeAsync(e, enabled: true);

    [Command(
        Expressions = ["停止"],
        MatchType = Sora.Core.Enums.MatchType.Full,
        Description = "管理员停止当前群的 AI、自然接话和主动响应")]
    public ValueTask Disable(MessageReceivedEvent e) => ChangeAsync(e, enabled: false);

    private async ValueTask ChangeAsync(MessageReceivedEvent e, bool enabled)
    {
        if (e.Message.SourceType != MessageSourceType.Group)
        {
            await ReplyAsync(e, "该指令只能在群聊中使用。");
            return;
        }

        var userId = e.Sender?.UserId ?? e.Message.SenderId;
        if (!_admin.Enabled || !_admin.AllowedUserIds.Contains(userId))
        {
            await ReplyAsync(e, "只有机器人管理员可以切换本群响应状态。");
            return;
        }

        var groupId = (long)e.Message.GroupId;
        var wasEnabled = _states.IsEnabled(groupId);
        var groupName = e.Group?.GroupName ?? groupId.ToString();
        _states.SetEnabled(groupId, enabled, userId, groupName);
        if (enabled)
            _activities.EnsureGroups([groupId]);

        var response = enabled
            ? wasEnabled
                ? "本群响应已经是开启状态。"
                : "已开启本群响应。AI 对话、自然接话、表情和主动发言现在可以运行。"
            : wasEnabled
                ? "已停止本群响应。发送 /响应 可以重新开启。"
                : "本群响应已经是停止状态。发送 /响应 可以重新开启。";

        _logger.LogInformation(
            "Group response state changed (GroupId={GroupId}, Enabled={Enabled}, UserId={UserId})",
            groupId,
            enabled,
            userId);
        await ReplyAsync(e, response);
    }

    private static async ValueTask ReplyAsync(MessageReceivedEvent e, string text)
    {
        var body = new MessageBody(text);
        if (e.Message.SourceType == MessageSourceType.Group)
            _ = await e.Api.SendGroupMessageAsync(e.Message.GroupId, body);
        else
            _ = await e.Api.SendFriendMessageAsync(e.Message.SenderId, body);
    }
}
