using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Messaging;

/// <summary>
/// Opaque return path attached by a platform adapter. Business handlers can reply
/// without selecting an account or knowing how that account is connected.
/// </summary>
public interface IReplyChannel
{
    string AccountId { get; }

    long SelfId { get; }

    bool IsGroup { get; }

    long TargetId { get; }

    Task SendTextAsync(string text, CancellationToken cancellationToken = default);

    Task SendImageAsync(
        string localPath,
        ImageSubType subType = ImageSubType.Normal,
        CancellationToken cancellationToken = default);

    Task SendAudioAsync(string localPath, CancellationToken cancellationToken = default);
}

internal sealed class SoraReplyChannel : IReplyChannel
{
    private readonly MessageReceivedEvent _event;

    public SoraReplyChannel(string accountId, MessageReceivedEvent messageEvent)
    {
        AccountId = string.IsNullOrWhiteSpace(accountId) ? "primary" : accountId;
        _event = messageEvent;
        SelfId = messageEvent.SelfId;
        IsGroup = messageEvent.Message.SourceType == MessageSourceType.Group;
        TargetId = IsGroup
            ? messageEvent.Message.GroupId
            : messageEvent.Sender?.UserId ?? messageEvent.Message.SenderId;
    }

    public string AccountId { get; }

    public long SelfId { get; }

    public bool IsGroup { get; }

    public long TargetId { get; }

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default) =>
        SendAsync(new MessageBody(text), cancellationToken);

    public Task SendImageAsync(
        string localPath,
        ImageSubType subType = ImageSubType.Normal,
        CancellationToken cancellationToken = default)
    {
        var fileUri = new Uri(Path.GetFullPath(localPath)).AbsoluteUri;
        return SendAsync(new MessageBody().AddImage(fileUri, subType), cancellationToken);
    }

    public Task SendAudioAsync(string localPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath))
            throw new FileNotFoundException("Reply audio file does not exist.", localPath);

        var fileUri = new Uri(Path.GetFullPath(localPath)).AbsoluteUri;
        return SendAsync(new MessageBody().AddAudio(fileUri), cancellationToken);
    }

    private async Task SendAsync(MessageBody body, CancellationToken cancellationToken)
    {
        if (IsGroup)
            await _event.Api.SendGroupMessageAsync(TargetId, body, cancellationToken);
        else
            await _event.Api.SendFriendMessageAsync(TargetId, body, cancellationToken);
    }
}
