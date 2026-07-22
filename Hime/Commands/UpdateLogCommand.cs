using Hime.Services;
using Sora.Command.Attributes;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Commands;

/// <summary>供所有用户查询 Hime 程序版本和发布更新日志。</summary>
[CommandGroup(Name = "程序更新", Prefix = "/")]
public sealed class UpdateLogCommand
{
    private readonly ProgramUpdateLogService _updates;

    public UpdateLogCommand(ProgramUpdateLogService updates)
    {
        _updates = updates;
    }

    [Command(
        Expressions = ["更新日志"],
        MatchType = Sora.Core.Enums.MatchType.Keyword,
        Description = "查看程序更新：/更新日志 [最新|全部|版本号]")]
    public ValueTask UpdateLog(MessageReceivedEvent e) =>
        Reply(e, _updates.BuildLog(ExtractArgument(e.Message.Body?.GetText(), "/更新日志")));

    [Command(
        Expressions = ["版本"],
        MatchType = Sora.Core.Enums.MatchType.Full,
        Description = "查看当前程序版本")]
    public ValueTask Version(MessageReceivedEvent e) => Reply(e, _updates.BuildVersionStatus());

    private static async ValueTask Reply(MessageReceivedEvent e, string text)
    {
        var body = new MessageBody(text);
        if (e.Message.SourceType == MessageSourceType.Group)
            _ = await e.Api.SendGroupMessageAsync(e.Message.GroupId, body);
        else
            _ = await e.Api.SendFriendMessageAsync(e.Message.SenderId, body);
    }

    private static string ExtractArgument(string? raw, string prefix)
    {
        var text = raw?.Trim() ?? string.Empty;
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? text[prefix.Length..].Trim()
            : text;
    }
}
