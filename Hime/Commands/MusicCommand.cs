using Hime.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Command.Attributes;
using Sora.Core.Types;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Commands;

/// <summary>
/// Searches song metadata through local ncm-api-rs. It only sends official song-page links,
/// never a raw audio URL, download URL, account cookie, or login information.
/// </summary>
[CommandGroup(Name = "music", Prefix = "/")]
public sealed class MusicCommand
{
    private readonly NcmMusicService _music;
    private readonly OneBotMusicCardSender _oneBotMusicCardSender;
    private readonly MusicOptions _options;
    private readonly ILogger<MusicCommand> _logger;

    public MusicCommand(
        NcmMusicService music,
        OneBotMusicCardSender oneBotMusicCardSender,
        IOptions<MusicOptions> options,
        ILogger<MusicCommand> logger)
    {
        _music = music;
        _oneBotMusicCardSender = oneBotMusicCardSender;
        _options = options.Value;
        _logger = logger;
    }

    [Command(
        Expressions = ["music", "song", "\u70b9\u6b4c"],
        MatchType = Sora.Core.Enums.MatchType.Keyword,
        Description = "\u70b9\u6b4c\uff1a/music \u6b4c\u540d \u6216 /\u70b9\u6b4c \u6b4c\u540d")]
    public async ValueTask Search(MessageReceivedEvent e)
    {
        var keywords = ExtractArgument(e.Message.Body?.GetText(), "/music", "/song", "/\u70b9\u6b4c");
        if (string.IsNullOrWhiteSpace(keywords))
        {
            await ReplyAsync(e, "\u7528\u6cd5\uff1a/music \u6b4c\u540d\n\u4f8b\u5982\uff1a/music \u4f2f\u864e\u8bf4");
            return;
        }

        // A normal song request should behave like the native QQ music experience:
        // choose the top exact-match result instead of asking members to pick a version.
        await SearchAndRememberAsync(e, keywords, sendFirstSong: true);
    }

    [Command(
        Expressions = ["\u9009\u6b4c", "pick"],
        MatchType = Sora.Core.Enums.MatchType.Keyword,
        Description = "\u9009\u62e9\u5019\u9009\u6b4c\u66f2\uff1a/\u9009\u6b4c \u5e8f\u53f7")]
    public async ValueTask Pick(MessageReceivedEvent e)
    {
        var value = ExtractArgument(e.Message.Body?.GetText(), "/\u9009\u6b4c", "/pick");
        if (!int.TryParse(value, out var index))
        {
            await ReplyAsync(e, "\u8bf7\u53d1\u9001 /\u9009\u6b4c \u5e8f\u53f7\uff0c\u4f8b\u5982 /\u9009\u6b4c 1\u3002");
            return;
        }

        var userId = e.Sender?.UserId ?? e.Message.SenderId;
        if (!_music.TryGetSelection(GetConversationId(e), userId, index, out var song) || song is null)
        {
            await ReplyAsync(e, "\u6ca1\u6709\u627e\u5230\u53ef\u9009\u7684\u6b4c\u66f2\u3002\u8bf7\u5148\u4f7f\u7528 /music \u6b4c\u540d \u641c\u7d22\u3002");
            return;
        }

        await SendSongAsync(e, song);
    }

    public async Task PlayFirstAsync(MessageReceivedEvent e, string keywords, CancellationToken cancellationToken = default)
    {
        await SearchAndRememberAsync(e, keywords, sendFirstSong: true, cancellationToken);
    }

    public static bool TryExtractNaturalPlayRequest(string rawText, bool isAtBot, out string keywords)
    {
        keywords = string.Empty;
        if (!isAtBot)
            return false;

        var text = rawText.Trim().TrimStart('\u3002', '\uff0c', ',', ':', '\uff1a');
        foreach (var prefix in new[] { "\u64ad\u653e", "\u70b9\u6b4c", "\u6765\u9996", "\u6765\u4e00\u9996" })
        {
            // Some QQ clients include the complete @ display name in GetText().
            // The event already proved that Hime was mentioned, so find the request verb
            // rather than requiring it to be the first visible character.
            var prefixIndex = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (prefixIndex < 0)
                continue;

            keywords = text[(prefixIndex + prefix.Length)..].Trim();
            return keywords.Length > 0;
        }

        return false;
    }

    private async Task SearchAndRememberAsync(
        MessageReceivedEvent e,
        string keywords,
        bool sendFirstSong,
        CancellationToken cancellationToken = default)
    {
        if (!_music.IsEnabled)
        {
            await ReplyAsync(e, "\u70b9\u6b4c\u529f\u80fd\u5f53\u524d\u672a\u5f00\u542f\u3002");
            return;
        }

        keywords = keywords.Trim();
        if (keywords.Length is 0 or > 80)
        {
            await ReplyAsync(e, "\u6b4c\u540d\u8bf7\u4fdd\u6301\u5728 1\uff5e80 \u4e2a\u5b57\u7b26\u5185\u3002");
            return;
        }

        try
        {
            var songs = await _music.SearchAsync(keywords, cancellationToken);
            if (songs.Count == 0)
            {
                await ReplyAsync(e, $"\u6ca1\u627e\u5230\u300a{keywords}\u300b\u3002");
                return;
            }

            var userId = e.Sender?.UserId ?? e.Message.SenderId;
            _music.RememberSelection(GetConversationId(e), userId, songs);
            if (sendFirstSong)
            {
                await SendSongAsync(e, songs[0]);
                return;
            }

            await SendSongAsync(e, songs[0]);
        }
        catch (MusicSearchException ex)
        {
            _logger.LogWarning(ex, "Music search command failed.");
            await ReplyAsync(e, "\u70b9\u6b4c\u670d\u52a1\u6682\u65f6\u4e0d\u53ef\u7528\uff0c\u8bf7\u7a0d\u540e\u518d\u8bd5\u3002");
        }
    }

    private async Task SendSongAsync(MessageReceivedEvent e, MusicSong song)
    {
        if (_options.PreferRichCard)
        {
            if (await _oneBotMusicCardSender.TrySendAsync(e, song))
                return;

            _logger.LogWarning("Native QQ music card was unavailable; falling back to the official song link.");
        }

        await ReplyAsync(e, $"{song.Title}\n{song.Artist} - {song.Album}\n\u7f51\u6613\u4e91\u97f3\u4e50\uff1a{song.OfficialUrl}");
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

    private static long GetConversationId(MessageReceivedEvent e) =>
        e.Message.SourceType == Sora.Core.Enums.MessageSourceType.Group
            ? e.Message.GroupId
            : -e.Message.SenderId;

    private static async Task<SendMessageResult> SendAsync(MessageReceivedEvent e, MessageBody message)
    {
        if (e.Message.SourceType == Sora.Core.Enums.MessageSourceType.Group)
            return await e.Api.SendGroupMessageAsync(e.Message.GroupId, message);

        return await e.Api.SendFriendMessageAsync(e.Message.SenderId, message);
    }

    private static async Task ReplyAsync(MessageReceivedEvent e, string text) =>
        _ = await SendAsync(e, new MessageBody(text));
}
