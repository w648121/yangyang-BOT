using Hime.Data.Models;

namespace Hime.Data.Services;

/// <summary>记录群的近期活动，并保存主动互动的群级冷却与额度。</summary>
public interface IGroupActivityService
{
    void RecordIncoming(
        long groupId,
        string groupName,
        long userId,
        string nickname,
        string content,
        IReadOnlyList<string> imagePaths,
        IReadOnlyList<string>? stickerEmotions = null,
        IReadOnlyList<string>? stickerTags = null,
        long messageId = 0,
        string? accountId = null,
        long? replyToMessageId = null,
        long? replyToUserId = null,
        string? quotedText = null,
        IReadOnlyList<long>? mentionedUserIds = null,
        string? topicId = null,
        IReadOnlyList<long>? conversationParticipants = null);

    void RecordBotReply(
        long groupId,
        string content,
        long? replyToMessageId = null,
        long? replyToUserId = null,
        string? topicId = null,
        IReadOnlyList<long>? conversationParticipants = null,
        long messageId = 0);

    void RecordProactiveDecision(long groupId);

    void RecordProactiveSent(long groupId, string content);

    void RecordProactiveArticleSent(long groupId, string content);

    void EnsureGroups(IEnumerable<long> groupIds);

    ProactiveHourlyQuota GetHourlyQuota(long groupId, int minimum, int maximum);

    bool CanSendProactiveArticle(long groupId, int maximumPerHour);

    IReadOnlyList<GroupActivityMessage> GetRecentMessages(long groupId, int maximum);

    IReadOnlyList<GroupActivityRecord> GetGroups();
}

public sealed record ProactiveHourlyQuota(
    DateTime HourUtc,
    int Sent,
    int Target,
    DateTime? LastSentAt);
