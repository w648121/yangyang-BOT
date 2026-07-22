using Sora.Core.Enums;

namespace Hime.Hosting;

/// <summary>将主动服务与 Sora 连接生命周期隔离开来的受限群消息发送接口。</summary>
public interface IGroupMessageSender
{
    bool IsReady { get; }

    Task SendGroupTextAsync(long groupId, string text, CancellationToken cancellationToken = default);

    Task SendGroupImageAsync(
        long groupId,
        string localPath,
        ImageSubType subType = ImageSubType.Normal,
        CancellationToken cancellationToken = default);

    Task SendGroupAudioAsync(long groupId, string localPath, CancellationToken cancellationToken = default);

    Task SendFriendAudioAsync(long userId, string localPath, CancellationToken cancellationToken = default);
}
