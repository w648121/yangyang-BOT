using Hime.Data.Models;
using Hime.Services;
using LiteDB;
using Microsoft.Extensions.Options;

namespace Hime.Data.Services;

/// <summary>
/// In-memory group activity window backed by coalesced LiteDB snapshots. Reads and
/// mutations stay on the message path; disk writes are performed in the background.
/// </summary>
public sealed class GroupActivityService : IGroupActivityService
{
    private readonly ILiteCollection<GroupActivityRecord> _groups;
    private readonly LiteDbWriteBehindService _writeBehind;
    private readonly IOptionsMonitor<GroupActivityOptions> _options;
    private readonly StickerLabelVocabulary _stickerLabels;
    private readonly IOptionsMonitor<ConversationFocusOptions> _focusOptions;
    private readonly Dictionary<long, GroupActivityRecord> _records;
    private readonly object _sync = new();

    public GroupActivityService(
        HimeDbContext context,
        LiteDbWriteBehindService writeBehind,
        IOptionsMonitor<GroupActivityOptions> options,
        StickerLabelVocabulary stickerLabels,
        IOptionsMonitor<ConversationFocusOptions> focusOptions)
    {
        _writeBehind = writeBehind;
        _options = options;
        _stickerLabels = stickerLabels;
        _focusOptions = focusOptions;
        _groups = context.Database.GetCollection<GroupActivityRecord>("group_activity");
        _groups.EnsureIndex(record => record.LastIncomingAt);
        _records = _groups.FindAll()
            .Select(Clone)
            .GroupBy(record => record.GroupId)
            .ToDictionary(group => group.Key, group => group.Last());
    }

    public void RecordIncoming(
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
        IReadOnlyList<long>? conversationParticipants = null)
    {
        lock (_sync)
        {
            var record = GetOrCreate(groupId, groupName);
            var now = DateTime.UtcNow;
            record.LastIncomingAt = now;
            record.RecentMessages ??= [];
            record.RecentMessages.Add(new GroupActivityMessage
            {
                IsBot = false,
                MessageId = messageId,
                AccountId = Trim(accountId, 80),
                UserId = userId,
                Nickname = Trim(nickname, 80),
                Content = Trim(content, 800),
                ImagePaths = imagePaths.Take(8).ToList(),
                StickerEmotions = NormalizeStickerEmotions(stickerEmotions),
                StickerTags = NormalizeStickerTags(stickerTags),
                ReplyToMessageId = replyToMessageId,
                ReplyToUserId = replyToUserId,
                QuotedText = Trim(quotedText, 800),
                MentionedUserIds = NormalizeUserIds(mentionedUserIds, 16),
                TopicId = Trim(topicId, 120),
                ConversationParticipants = NormalizeUserIds(conversationParticipants, 24),
                Time = now
            });

            TrimRecentMessages(record);
            QueuePersist(record);
        }
    }

    public void RecordBotReply(
        long groupId,
        string content,
        long? replyToMessageId = null,
        long? replyToUserId = null,
        string? topicId = null,
        IReadOnlyList<long>? conversationParticipants = null,
        long messageId = 0)
    {
        lock (_sync)
        {
            var record = GetOrCreate(groupId, null);
            var now = DateTime.UtcNow;
            record.LastBotReplyAt = now;
            AppendBotMessage(
                record,
                content,
                now,
                replyToMessageId,
                replyToUserId,
                topicId,
                conversationParticipants,
                messageId);
            QueuePersist(record);
        }
    }

    public void RecordProactiveDecision(long groupId)
    {
        lock (_sync)
        {
            var record = GetOrCreate(groupId, null);
            record.LastProactiveDecisionAt = DateTime.UtcNow;
            QueuePersist(record);
        }
    }

    public void RecordProactiveSent(long groupId, string content)
        => RecordProactiveSent(groupId, content, isArticle: false);

    public void RecordProactiveArticleSent(long groupId, string content)
        => RecordProactiveSent(groupId, content, isArticle: true);

    private void RecordProactiveSent(long groupId, string content, bool isArticle)
    {
        lock (_sync)
        {
            var record = GetOrCreate(groupId, null);
            var now = DateTime.UtcNow;
            var hour = StartOfHour(now);
            if (record.ProactiveCounterHourUtc != hour)
            {
                record.ProactiveCounterHourUtc = hour;
                record.ProactiveSentThisHour = 0;
            }

            record.LastProactiveDecisionAt = now;
            record.LastProactiveAt = now;
            record.LastBotReplyAt = now;
            AppendBotMessage(record, content, now);
            record.ProactiveSentThisHour++;

            if (isArticle)
            {
                if (record.ProactiveArticleCounterHourUtc != hour)
                {
                    record.ProactiveArticleCounterHourUtc = hour;
                    record.ProactiveArticlesThisHour = 0;
                }

                record.ProactiveArticlesThisHour++;
            }

            QueuePersist(record);
        }
    }

    public void EnsureGroups(IEnumerable<long> groupIds)
    {
        lock (_sync)
        {
            foreach (var groupId in groupIds.Distinct())
            {
                var existed = _records.ContainsKey(groupId);
                var record = GetOrCreate(groupId, $"群{groupId}");
                if (!existed)
                    QueuePersist(record);
            }
        }
    }

    public ProactiveHourlyQuota GetHourlyQuota(long groupId, int minimum, int maximum)
    {
        var lower = Math.Max(1, Math.Min(minimum, maximum));
        var upper = Math.Max(lower, maximum);
        var now = DateTime.UtcNow;
        var hour = StartOfHour(now);

        lock (_sync)
        {
            var record = GetOrCreate(groupId, null);
            var changed = false;
            if (record.ProactiveCounterHourUtc != hour)
            {
                record.ProactiveCounterHourUtc = hour;
                record.ProactiveSentThisHour = 0;
                record.ProactiveTargetThisHour = Random.Shared.Next(lower, upper + 1);
                changed = true;
            }
            else if (record.ProactiveTargetThisHour < lower || record.ProactiveTargetThisHour > upper)
            {
                record.ProactiveTargetThisHour = Random.Shared.Next(lower, upper + 1);
                changed = true;
            }

            if (changed)
                QueuePersist(record);

            return new ProactiveHourlyQuota(
                hour,
                record.ProactiveSentThisHour,
                record.ProactiveTargetThisHour,
                record.LastProactiveAt);
        }
    }

    public bool CanSendProactiveArticle(long groupId, int maximumPerHour)
    {
        var limit = Math.Max(0, maximumPerHour);
        if (limit == 0)
            return false;

        var hour = StartOfHour(DateTime.UtcNow);
        lock (_sync)
        {
            var record = GetOrCreate(groupId, null);
            return record.ProactiveArticleCounterHourUtc != hour || record.ProactiveArticlesThisHour < limit;
        }
    }

    public IReadOnlyList<GroupActivityRecord> GetGroups()
    {
        lock (_sync)
        {
            return _records.Values
                .Select(Clone)
                .ToList()
                .AsReadOnly();
        }
    }

    public IReadOnlyList<GroupActivityMessage> GetRecentMessages(long groupId, int maximum)
    {
        var maximumRetention = Math.Clamp(_options.CurrentValue.RecentMessageRetentionLimit, 16, 500);
        var limit = Math.Clamp(maximum, 1, maximumRetention);
        lock (_sync)
        {
            _records.TryGetValue(groupId, out var record);
            return (record?.RecentMessages ?? [])
                .OrderBy(message => message.Time)
                .TakeLast(limit)
                .Select(CloneMessage)
                .ToList()
                .AsReadOnly();
        }
    }

    private GroupActivityRecord GetOrCreate(long groupId, string? groupName)
    {
        if (!_records.TryGetValue(groupId, out var record))
        {
            record = new GroupActivityRecord { GroupId = groupId };
            _records.Add(groupId, record);
        }

        if (!string.IsNullOrWhiteSpace(groupName))
            record.GroupName = Trim(groupName, 120);
        return record;
    }

    private void QueuePersist(GroupActivityRecord record)
    {
        var snapshot = Clone(record);
        _writeBehind.Enqueue($"group-activity:{record.GroupId}", () => _groups.Upsert(snapshot));
    }

    private void TrimRecentMessages(GroupActivityRecord record)
    {
        var limit = Math.Clamp(_options.CurrentValue.RecentMessageRetentionLimit, 16, 500);
        if (record.RecentMessages.Count > limit)
            record.RecentMessages = record.RecentMessages.TakeLast(limit).ToList();
    }

    private static DateTime StartOfHour(DateTime time) =>
        new(time.Year, time.Month, time.Day, time.Hour, 0, 0, DateTimeKind.Utc);

    private static GroupActivityRecord Clone(GroupActivityRecord source) => new()
    {
        GroupId = source.GroupId,
        GroupName = source.GroupName,
        LastIncomingAt = source.LastIncomingAt,
        LastBotReplyAt = source.LastBotReplyAt,
        LastProactiveDecisionAt = source.LastProactiveDecisionAt,
        LastProactiveAt = source.LastProactiveAt,
        ProactiveCounterHourUtc = source.ProactiveCounterHourUtc,
        ProactiveSentThisHour = source.ProactiveSentThisHour,
        ProactiveTargetThisHour = source.ProactiveTargetThisHour,
        ProactiveArticleCounterHourUtc = source.ProactiveArticleCounterHourUtc,
        ProactiveArticlesThisHour = source.ProactiveArticlesThisHour,
        RecentMessages = (source.RecentMessages ?? [])
            .Select(CloneMessage)
            .ToList()
    };

    private static string Trim(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private void AppendBotMessage(
        GroupActivityRecord record,
        string? content,
        DateTime time,
        long? replyToMessageId = null,
        long? replyToUserId = null,
        string? topicId = null,
        IReadOnlyList<long>? conversationParticipants = null,
        long messageId = 0)
    {
        var normalized = Trim(content, 800);
        // Internal status markers are useful for rate control but are not conversational context.
        if (string.IsNullOrWhiteSpace(normalized) ||
            (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.EndsWith(']')))
        {
            return;
        }

        record.RecentMessages ??= [];
        record.RecentMessages.Add(new GroupActivityMessage
        {
            IsBot = true,
            MessageId = Math.Max(0, messageId),
            UserId = 0,
            Nickname = Trim(_focusOptions.CurrentValue.BotDisplayName, 80),
            Content = normalized,
            ReplyToMessageId = replyToMessageId,
            ReplyToUserId = replyToUserId,
            TopicId = Trim(topicId, 120),
            ConversationParticipants = NormalizeUserIds(conversationParticipants, 24),
            Time = time
        });
        TrimRecentMessages(record);
    }

    private static GroupActivityMessage CloneMessage(GroupActivityMessage source) => new()
    {
        IsBot = source.IsBot,
        MessageId = source.MessageId,
        AccountId = source.AccountId,
        UserId = source.UserId,
        Nickname = source.Nickname,
        Content = source.Content,
        ImagePaths = (source.ImagePaths ?? []).ToList(),
        StickerEmotions = (source.StickerEmotions ?? []).ToList(),
        StickerTags = (source.StickerTags ?? []).ToList(),
        ReplyToMessageId = source.ReplyToMessageId,
        ReplyToUserId = source.ReplyToUserId,
        QuotedText = source.QuotedText,
        MentionedUserIds = (source.MentionedUserIds ?? []).ToList(),
        TopicId = source.TopicId,
        ConversationParticipants = (source.ConversationParticipants ?? []).ToList(),
        Time = source.Time
    };

    private static List<long> NormalizeUserIds(IReadOnlyList<long>? userIds, int maximum) =>
        (userIds ?? [])
            .Where(userId => userId > 0)
            .Distinct()
            .Take(Math.Max(1, maximum))
            .ToList();

    private List<string> NormalizeStickerEmotions(IReadOnlyList<string>? emotions) =>
        (emotions ?? [])
            .Select(emotion => _stickerLabels.TryNormalizeBaseEmotion(emotion, out var normalized)
                ? normalized
                : string.Empty)
            .Where(emotion => !string.IsNullOrWhiteSpace(emotion))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

    private static List<string> NormalizeStickerTags(IReadOnlyList<string>? tags) =>
        (tags ?? [])
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Where(tag => tag.All(character => char.IsLetterOrDigit(character) || character is '_' or '-'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
}

/// <summary>
/// Storage window for observed group messages. This is intentionally separated
/// from proactive/reply prompt limits so imported merged-forward evidence is not
/// discarded before investigation commands can read it.
/// </summary>
public sealed class GroupActivityOptions
{
    public int RecentMessageRetentionLimit { get; set; } = 160;

    public bool IsValid() => RecentMessageRetentionLimit is >= 16 and <= 500;
}
