using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Entities.Events;

namespace Hime.Services;

/// <summary>
/// Sends a standard OneBot music segment through LLBot's loopback-only HTTP endpoint.
/// LLBot signs and renders this as QQ's native music sharing card.
/// </summary>
public sealed class OneBotMusicCardSender
{
    private readonly OneBotApiClient _oneBot;
    private readonly MusicOptions _options;
    private readonly ILogger<OneBotMusicCardSender> _logger;

    public OneBotMusicCardSender(
        OneBotApiClient oneBot,
        IOptions<MusicOptions> options,
        ILogger<OneBotMusicCardSender> logger)
    {
        _oneBot = oneBot;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> TrySendAsync(
        MessageReceivedEvent messageEvent,
        MusicSong song,
        CancellationToken cancellationToken = default)
    {
        if (!_options.UseOneBotMusicCard)
            return false;

        var isGroup = messageEvent.Message.SourceType == Sora.Core.Enums.MessageSourceType.Group;
        var endpoint = isGroup ? "send_group_msg" : "send_private_msg";
        var targetId = isGroup
            ? (long)messageEvent.Message.GroupId
            : (long)messageEvent.Message.SenderId;
        if (targetId <= 0)
            return false;

        var segment = new
        {
            type = "music",
            data = new
            {
                type = "163",
                id = song.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        object requestBody = isGroup
            ? new { group_id = targetId, message = new[] { segment } }
            : new { user_id = targetId, message = new[] { segment } };

        var result = await _oneBot.PostAsync(endpoint, requestBody, messageEvent.SelfId, cancellationToken);
        if (!result.Success)
            return false;

        _logger.LogInformation(
            "Sent native QQ music card through OneBot (Target={Target}, SongId={SongId}).",
            targetId,
            song.Id);
        return true;
    }
}
