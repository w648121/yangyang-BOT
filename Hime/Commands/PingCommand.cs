using Sora.Command.Attributes;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;
using SoraCommandMatchType = Sora.Core.Enums.MatchType;

namespace Hime.Commands;

/// <summary>
/// Ping/Pong 指令
/// </summary>
[CommandGroup(Name = "", Prefix = "")]
public static class PingCommand
{
    /// <summary>
    /// /ping - 回复 pong
    /// </summary>
    [Command(Expressions = ["ping"], MatchType = Sora.Core.Enums.MatchType.Full, Description = "回复 pong")]
    public static async ValueTask Ping(MessageReceivedEvent e)
    {
        MessageBody reply = new("pong");
        if (e.Message.SourceType == MessageSourceType.Group)
            await e.Api.SendGroupMessageAsync(e.Message.GroupId, reply);
        else
            await e.Api.SendFriendMessageAsync(e.Message.SenderId, reply);
    }
}
