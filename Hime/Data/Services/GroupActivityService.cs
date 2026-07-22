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
    private readonly ProactiveAgentOptions _options;
    private readonly Dictionary<long, GroupActivityRecord> _records;
    private readonly object _sync = new();

    public GroupActivityService(
        HimeDbContext context,
        LiteDbWriteBehindService writeBehind,
        IOptions<ProactiveAgentOptions> options)
    {
        _writeBehind = writeBehind;
        _options = options.Value;
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
        IReadOnlyList<string>? stickerTags = null)
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
                UserId = userId,
                Nickname = Trim(nickname, 80),
                Content = Trim(content, 800),
                ImagePaths = imagePaths.Take(8).ToList(),
                StickerEmotions = NormalizeStickerEmotions(stickerEmotions),
                StickerTags = NormalizeStickerTags(stickerTags),
                Time = now
            });

            TrimRecentMessages(record);
            QueuePersist(record);
        }
    }

    public void RecordBotReply(long groupId, string content)
    {
        lock (_sync)
        {
            var record = GetOrCreate(groupId, null);
            var now = DateTime.UtcNow;
            record.LastBotReplyAt = now;
            AppendBotMessage(record, content, now);
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
        var limit = Math.Clamp(maximum, 1, 100);
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
        var limit = Math.Clamp(_options.ContextMessageLimit, 4, 100);
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

    private void AppendBotMessage(GroupActivityRecord record, string? content, DateTime time)
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
            UserId = 0,
            Nickname = "Hime",
            Content = normalized,
            Time = time
        });
        TrimRecentMessages(record);
    }

    private static GroupActivityMessage CloneMessage(GroupActivityMessage source) => new()
    {
        IsBot = source.IsBot,
        UserId = source.UserId,
        Nickname = source.Nickname,
        Content = source.Content,
        ImagePaths = (source.ImagePaths ?? []).ToList(),
        StickerEmotions = (source.StickerEmotions ?? []).ToList(),
        StickerTags = (source.StickerTags ?? []).ToList(),
        Time = source.Time
    };

    private static List<string> NormalizeStickerEmotions(IReadOnlyList<string>? emotions) =>
        (emotions ?? [])
            .Where(emotion => ImageService.CanonicalEmotions.Contains(emotion, StringComparer.OrdinalIgnoreCase))
            .Select(emotion => emotion.ToLowerInvariant())
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
