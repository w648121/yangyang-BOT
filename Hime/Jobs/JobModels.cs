using LiteDB;

namespace Hime.Jobs;

public enum ScheduledJobKind
{
    Reminder = 0
}

public enum ScheduledJobStatus
{
    Scheduled = 0,
    Running = 1,
    Completed = 2,
    Cancelled = 3,
    Failed = 4
}

public enum JobRecurrenceKind
{
    None = 0,
    Daily = 1,
    Weekly = 2
}

public enum JobDeliveryStatus
{
    Started = 0,
    Delivered = 1,
    Failed = 2
}

public sealed class ScheduledJobRecord
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ShortCode { get; set; } = string.Empty;

    public ScheduledJobKind Kind { get; set; } = ScheduledJobKind.Reminder;

    public ScheduledJobStatus Status { get; set; } = ScheduledJobStatus.Scheduled;

    public string Platform { get; set; } = "qq";

    public string AccountId { get; set; } = "primary";

    public string ScopeKey { get; set; } = string.Empty;

    public long ConversationKey { get; set; }

    public long CreatorUserId { get; set; }

    public bool IsGroup { get; set; }

    public long TargetId { get; set; }

    public string Content { get; set; } = string.Empty;

    public string OriginalText { get; set; } = string.Empty;

    public string CreationKey { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime NextRunAtUtc { get; set; }

    public DateTime? LastRunAtUtc { get; set; }

    public DateTime? LeaseUntilUtc { get; set; }

    public string TimeZoneId { get; set; } = JobTimeZones.Beijing.Id;

    public JobRecurrenceKind Recurrence { get; set; }

    public int LocalHour { get; set; }

    public int LocalMinute { get; set; }

    public int? LocalDayOfWeek { get; set; }

    public int DeliveryAttemptCount { get; set; }

    public string LastError { get; set; } = string.Empty;
}

public sealed class ScheduledJobDeliveryRecord
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string JobId { get; set; } = string.Empty;

    public DateTime ScheduledAtUtc { get; set; }

    public JobDeliveryStatus Status { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    public string Error { get; set; } = string.Empty;
}

public sealed class JobOptions
{
    public bool Enabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 2;

    public int ClaimBatchSize { get; set; } = 16;

    public int LeaseSeconds { get; set; } = 120;

    public int DisabledGroupPostponeSeconds { get; set; } = 60;

    public int OfflineAccountPostponeSeconds { get; set; } = 20;

    public int MaxActiveJobsPerUser { get; set; } = 20;

    /// <summary>
    /// Natural-language markers that opt an ordinary message into reminder parsing.
    /// Commands remain stable protocol entry points; this list can be extended without recompiling.
    /// </summary>
    public List<string> IntentMarkers { get; set; } =
    [
        "提醒我",
        "提醒一下",
        "提醒下",
        "别忘记提醒",
        "别忘了提醒",
        "定个闹钟",
        "订个闹钟",
        "顶个闹钟",
        "设个闹钟",
        "设置闹钟"
    ];

    public List<string> CommandPrefixes { get; set; } = ["/提醒", "/任务"];

    /// <summary>Natural-language markers that opt a reply or quoted reminder into edit parsing.</summary>
    public List<string> EditMarkers { get; set; } =
    [
        "改一下",
        "修改",
        "改成",
        "调整",
        "换成",
        "变成",
        "以后每天",
        "每天都"
    ];
}

public sealed record JobScheduleSpec(
    DateTime RunAtUtc,
    string Content,
    JobRecurrenceKind Recurrence,
    int LocalHour,
    int LocalMinute,
    int? LocalDayOfWeek,
    string DisplayTime);

public enum JobParseNeed
{
    None = 0,
    Time = 1,
    Content = 2,
    EventAnchor = 3
}

public sealed record JobParseResult(
    JobScheduleSpec? Schedule,
    JobParseNeed Need,
    string Error,
    JobEventReference? EventReference = null)
{
    public bool Success => Schedule is not null;

    public static JobParseResult Parsed(JobScheduleSpec schedule) =>
        new(schedule, JobParseNeed.None, string.Empty);

    public static JobParseResult Missing(JobParseNeed need, string error) =>
        new(null, need, error);

    public static JobParseResult MissingAnchor(
        string error,
        JobEventReference reference) =>
        new(null, JobParseNeed.EventAnchor, error, reference);
}

public sealed record CalendarDuration(
    int Years,
    int Months,
    int Days,
    int Hours,
    int Minutes)
{
    public bool IsZero => Years == 0 && Months == 0 && Days == 0 && Hours == 0 && Minutes == 0;

    public DateTime Apply(DateTime value, int direction = 1)
    {
        var sign = direction < 0 ? -1 : 1;
        return value
            .AddYears(checked(Years * sign))
            .AddMonths(checked(Months * sign))
            .AddDays((double)Days * sign)
            .AddHours((double)Hours * sign)
            .AddMinutes((double)Minutes * sign);
    }
}

public sealed record JobEventReference(
    string EventName,
    int Direction,
    CalendarDuration Offset,
    JobRecurrenceKind RecurrenceHint,
    string Content);

public sealed record TemporalPoint(
    DateTime RunAtUtc,
    JobRecurrenceKind Recurrence,
    int LocalHour,
    int LocalMinute,
    int? LocalDayOfWeek,
    string DisplayTime);

public enum TemporalAnchorKind
{
    OneOff = 0,
    Daily = 1,
    Weekly = 2
}

public sealed class TemporalAnchorRecord
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string Platform { get; set; } = "qq";

    public string AccountId { get; set; } = "primary";

    public long UserId { get; set; }

    public long? GroupId { get; set; }

    public string EventName { get; set; } = string.Empty;

    public string NormalizedEventName { get; set; } = string.Empty;

    public TemporalAnchorKind Kind { get; set; }

    public DateTime AnchorAtUtc { get; set; }

    public int LocalHour { get; set; }

    public int LocalMinute { get; set; }

    public int? LocalDayOfWeek { get; set; }

    public string SourceText { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class JobInteractionScopes
{
    public static string ForUser(string scopeKey, long userId) =>
        scopeKey.StartsWith("qq:private:", StringComparison.OrdinalIgnoreCase)
            ? scopeKey
            : $"{scopeKey}:user:{userId}";
}

public static class JobTimeZones
{
    public static TimeZoneInfo Beijing { get; } = ResolveBeijing();

    private static TimeZoneInfo ResolveBeijing()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        }
    }
}
