using Hime.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Core.Enums;
using Sora.Entities.Events;

namespace Hime.Services;

/// <summary>Sends image results as custom-node merged forwards through OneBot V11.</summary>
public sealed class OneBotForwardMessageSender
{
    private readonly OneBotApiClient _oneBot;
    private readonly SetuOptions _options;
    private readonly BotAccountsOptions _accounts;
    private readonly ILogger<OneBotForwardMessageSender> _logger;

    public OneBotForwardMessageSender(
        OneBotApiClient oneBot,
        IOptions<SetuOptions> options,
        IOptions<BotAccountsOptions> accounts,
        ILogger<OneBotForwardMessageSender> logger)
    {
        _oneBot = oneBot;
        _options = options.Value;
        _accounts = accounts.Value;
        _logger = logger;
    }

    public async Task<bool> SendAsync(
        MessageReceivedEvent messageEvent,
        IReadOnlyList<SetuImage> images,
        CancellationToken cancellationToken = default)
    {
        if (images.Count == 0)
            return false;

        var selfId = ResolveSelfId(messageEvent.SelfId);
        if (selfId <= 0)
        {
            _logger.LogWarning(
                "Cannot send merged forward because neither the incoming message nor BotAccounts provides a SelfId.");
            return false;
        }

        var nodes = images.Select((image, index) => new
        {
            type = "node",
            data = new
            {
                name = _options.ForwardNickname.Trim(),
                uin = selfId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                content = new object[]
                {
                    new
                    {
                        type = "text",
                        data = new { text = BuildCaption(image, index + 1, images.Count) }
                    },
                    new
                    {
                        type = "image",
                        data = new { file = image.OriginalUrl, cache = "0", proxy = "1", timeout = "30" }
                    }
                }
            }
        }).ToArray();

        var isGroup = messageEvent.Message.SourceType == MessageSourceType.Group;
        var action = isGroup ? "send_group_forward_msg" : "send_private_forward_msg";
        object payload = isGroup
            ? new { group_id = (long)messageEvent.Message.GroupId, messages = nodes }
            : new { user_id = (long)messageEvent.Message.SenderId, messages = nodes };
        var result = await _oneBot.PostAsync(action, payload, selfId, cancellationToken);
        if (result.Success)
        {
            _logger.LogInformation(
                "Sent {Count} image(s) through OneBot merged forward (Action={Action}).",
                images.Count,
                action);
        }
        return result.Success;
    }

    private long ResolveSelfId(long eventSelfId)
    {
        if (eventSelfId > 0)
            return eventSelfId;

        var enabled = _accounts.GetEnabledConnections();
        return enabled.FirstOrDefault(account => account.IsPrimary && account.SelfId > 0)?.SelfId
               ?? enabled.FirstOrDefault(account => account.SelfId > 0)?.SelfId
               ?? 0;
    }

    private static string BuildCaption(SetuImage image, int index, int total)
    {
        var parts = new List<string> { $"[{index}/{total}] {image.Source}" };
        if (!string.IsNullOrWhiteSpace(image.Title))
            parts.Add(image.Title.Trim());
        if (!string.IsNullOrWhiteSpace(image.Author))
            parts.Add($"作者：{image.Author.Trim()}");
        if (image.PixivId is not null)
            parts.Add($"Pixiv：{image.PixivId}");
        if (image.VerifiedNonR18 && image.VerifiedNonAi)
            parts.Add("非 R18 · 非 AI · original");
        return string.Join('\n', parts) + "\n";
    }
}
