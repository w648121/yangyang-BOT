using Sora.Command.Attributes;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;
using SoraCommandMatchType = Sora.Core.Enums.MatchType;

namespace Hime.Commands;

/// <summary>
/// 示例指令集合
/// </summary>
[CommandGroup(Name = "打招呼")]
public static class ExampleCommands
{

    [Command(Expressions = ["hello"], MatchType = SoraCommandMatchType.Full, Description = "Say hello")]
    public static async ValueTask Hello(MessageReceivedEvent e)
    {
        MessageBody reply = new("Hello! 你好！");
        if (e.Message.SourceType == MessageSourceType.Group)
            await e.Api.SendGroupMessageAsync(e.Message.GroupId, reply);
        else
            await e.Api.SendFriendMessageAsync(e.Message.SenderId, reply);
    }
}