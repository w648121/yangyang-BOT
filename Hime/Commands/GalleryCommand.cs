using Hime.Services;
using Sora.Command.Attributes;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Commands;

/// <summary>发送经过来源、非 AI 与热度校验的鸣潮角色美图。</summary>
[CommandGroup(Name = "鸣潮美图", Prefix = "/")]
public sealed class GalleryCommand
{
    private readonly GalleryService _gallery;

    public GalleryCommand(GalleryService gallery) => _gallery = gallery;

    [Command(
        Expressions = ["美图", "gallery", "art"],
        MatchType = Sora.Core.Enums.MatchType.Keyword,
        Description = "鸣潮美图：/美图 [角色]；不写角色则随机")]
    public async ValueTask Send(MessageReceivedEvent e)
    {
        var argument = ExtractArgument(e.Message.Body?.GetText(), "/美图", "/gallery", "/art");
        if (argument is "帮助" or "help" or "?")
        {
            await ReplyTextAsync(e, "用法：/美图 [角色]\n例如：/美图 秧秧、/美图 玄翎\n不写角色时从全角色合规美图库随机发送；发送 /美图 角色 可查看角色列表。");
            return;
        }

        if (argument is "角色" or "列表" or "list")
        {
            var characters = _gallery.GetCharacters();
            var lines = characters.Chunk(8).Select(chunk => string.Join("、", chunk));
            await ReplyTextAsync(e, "当前可指定角色：\n" + string.Join('\n', lines));
            return;
        }

        var result = _gallery.Select(string.IsNullOrWhiteSpace(argument) ? null : argument);
        if (!result.Found || result.Item is null || string.IsNullOrWhiteSpace(result.FullPath))
        {
            await ReplyTextAsync(e, result.Error ?? "没有找到可发送的美图。");
            return;
        }

        var item = result.Item;
        var sourceLabel = result.HighEngagement ? "高热度非 AI 来源" : "官方角色立绘兜底";
        var caption = $"{item.Character} · {sourceLabel}";
        var body = new MessageBody(caption)
            .AddImage(new Uri(Path.GetFullPath(result.FullPath)).AbsoluteUri, ImageSubType.Normal);
        await ReplyAsync(e, body);
    }

    private static string ExtractArgument(string? raw, params string[] prefixes)
    {
        var text = raw?.Trim() ?? string.Empty;
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return text[prefix.Length..].Trim();
        }

        var firstSpace = text.IndexOfAny([' ', '\t', '\r', '\n']);
        return firstSpace < 0 ? string.Empty : text[(firstSpace + 1)..].Trim();
    }

    private static async ValueTask ReplyTextAsync(MessageReceivedEvent e, string text) =>
        await ReplyAsync(e, new MessageBody(text));

    private static async ValueTask ReplyAsync(MessageReceivedEvent e, MessageBody body)
    {
        if (e.Message.SourceType == MessageSourceType.Group)
            _ = await e.Api.SendGroupMessageAsync(e.Message.GroupId, body);
        else
            _ = await e.Api.SendFriendMessageAsync(e.Message.SenderId, body);
    }
}
