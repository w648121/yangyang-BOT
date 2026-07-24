using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Services;

/// <summary>Coordinates image retrieval and the final OneBot merged-forward response.</summary>
public sealed class SetuRequestService
{
    private readonly SetuApiService _api;
    private readonly OneBotForwardMessageSender _forwardSender;
    private readonly SetuOptions _options;
    private readonly ILogger<SetuRequestService> _logger;

    public SetuRequestService(
        SetuApiService api,
        OneBotForwardMessageSender forwardSender,
        IOptions<SetuOptions> options,
        ILogger<SetuRequestService> logger)
    {
        _api = api;
        _forwardSender = forwardSender;
        _options = options.Value;
        _logger = logger;
    }

    public async Task HandleAsync(
        MessageReceivedEvent messageEvent,
        SetuRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            await ReplyAsync(messageEvent, "二次元图片功能当前未开启。", cancellationToken);
            return;
        }

        if (request.RejectedUnsafe)
        {
            await ReplyAsync(messageEvent, "这里只提供非 R18、非 AI 的二次元图片，换个普通标签吧。", cancellationToken);
            return;
        }

        var result = await _api.FetchAsync(request, cancellationToken);
        if (result.Images.Count == 0)
        {
            var tagText = request.Tags.Count > 0 ? $"“{string.Join('、', request.Tags)}”" : "这次请求";
            await ReplyAsync(
                messageEvent,
                result.Error ?? $"没有找到符合 {tagText} 的非 R18、非 AI 图片，换个关键词试试吧。",
                cancellationToken);
            return;
        }

        if (!await _forwardSender.SendAsync(messageEvent, result.Images, cancellationToken))
        {
            await ReplyAsync(messageEvent, "图片已经找到了，但合并转发发送失败了，请稍后再试。", cancellationToken);
            return;
        }

        _logger.LogInformation(
            "Image request completed (Source={Source}, Requested={Requested}, Sent={Sent}, MatchMode={MatchMode}, Tags={Tags})",
            request.Source,
            request.Count,
            result.Images.Count,
            result.MatchMode,
            string.Join(',', request.Tags));
    }

    private static async Task ReplyAsync(
        MessageReceivedEvent messageEvent,
        string text,
        CancellationToken cancellationToken)
    {
        var body = new MessageBody(text);
        if (messageEvent.Message.SourceType == MessageSourceType.Group)
            _ = await messageEvent.Api.SendGroupMessageAsync(messageEvent.Message.GroupId, body, cancellationToken);
        else
            _ = await messageEvent.Api.SendFriendMessageAsync(messageEvent.Message.SenderId, body, cancellationToken);
    }
}
