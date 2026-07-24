using Hime.Data;
using LiteDB;

namespace Hime.Jobs;

public enum BeginDeliveryResult
{
    Started = 0,
    AlreadyDelivered = 1,
    PreviousAttemptUncertain = 2
}

public sealed record CreateScheduledJobResult(ScheduledJobRecord Job, bool Created);

public sealed class ScheduledJobStore
{
    private readonly HimeDbContext _db;
    private readonly object _sync = new();

    public ScheduledJobStore(HimeDbContext db)
    {
        _db = db;
        Jobs.EnsureIndex(item => item.CreationKey, unique: true);
        Jobs.EnsureIndex(item => item.ShortCode);
        Jobs.EnsureIndex(item => item.CreatorUserId);
        Jobs.EnsureIndex(item => item.NextRunAtUtc);
        Jobs.EnsureIndex(item => item.Status);
        Deliveries.EnsureIndex(item => item.JobId);
        Deliveries.EnsureIndex(item => item.Status);
    }

    private ILiteCollection<ScheduledJobRecord> Jobs =>
        _db.Database.GetCollection<ScheduledJobRecord>("scheduled_jobs");

    private ILiteCollection<ScheduledJobDeliveryRecord> Deliveries =>
        _db.Database.GetCollection<ScheduledJobDeliveryRecord>("scheduled_job_deliveries");

    public CreateScheduledJobResult Create(
        ScheduledJobRecord job,
        int maxActiveJobsPerUser)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_sync)
        {
            var existing = Jobs.FindOne(item => item.CreationKey == job.CreationKey);
            if (existing is not null)
                return new CreateScheduledJobResult(existing, false);

            var activeCount = Jobs.Find(item =>
                    item.AccountId == job.AccountId &&
                    item.CreatorUserId == job.CreatorUserId)
                .Count(IsActive);
            if (activeCount >= Math.Clamp(maxActiveJobsPerUser, 1, 100))
                throw new InvalidOperationException($"每位用户最多保留 {maxActiveJobsPerUser} 个未完成提醒。");

            job.ShortCode = AllocateShortCode(job.AccountId, job.CreatorUserId, job.Id);
            job.CreatedAtUtc = DateTime.UtcNow;
            job.UpdatedAtUtc = job.CreatedAtUtc;
            Jobs.Insert(job);
            _db.SaveChanges();
            return new CreateScheduledJobResult(job, true);
        }
    }

    public ScheduledJobRecord? Get(string id)
    {
        lock (_sync)
            return Jobs.FindById(id);
    }

    public IReadOnlyList<ScheduledJobRecord> ListActive(
        string accountId,
        string scopeKey,
        long creatorUserId)
    {
        lock (_sync)
        {
            return Jobs.Find(item =>
                    item.AccountId == accountId &&
                    item.ScopeKey == scopeKey &&
                    item.CreatorUserId == creatorUserId)
                .Where(IsActive)
                .OrderBy(item => item.NextRunAtUtc)
                .ToArray();
        }
    }

    public ScheduledJobRecord? FindActiveByCode(
        string accountId,
        string scopeKey,
        long creatorUserId,
        string shortCode)
    {
        lock (_sync)
        {
            return Jobs.Find(item =>
                    item.AccountId == accountId &&
                    item.ScopeKey == scopeKey &&
                    item.CreatorUserId == creatorUserId &&
                    item.ShortCode == shortCode)
                .Where(IsActive)
                .OrderByDescending(item => item.CreatedAtUtc)
                .FirstOrDefault();
        }
    }

    public ScheduledJobRecord? UpdateSchedule(
        string accountId,
        string scopeKey,
        long creatorUserId,
        string shortCode,
        JobScheduleSpec schedule,
        string editText)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        lock (_sync)
        {
            var job = Jobs.Find(item =>
                    item.AccountId == accountId &&
                    item.ScopeKey == scopeKey &&
                    item.CreatorUserId == creatorUserId &&
                    item.ShortCode == shortCode)
                .Where(item => item.Status == ScheduledJobStatus.Scheduled)
                .OrderByDescending(item => item.CreatedAtUtc)
                .FirstOrDefault();
            if (job is null)
                return null;

            job.NextRunAtUtc = DateTime.SpecifyKind(schedule.RunAtUtc, DateTimeKind.Utc);
            job.Content = schedule.Content;
            job.Recurrence = schedule.Recurrence;
            job.LocalHour = schedule.LocalHour;
            job.LocalMinute = schedule.LocalMinute;
            job.LocalDayOfWeek = schedule.LocalDayOfWeek;
            job.OriginalText =
                $"{job.OriginalText}\n[修改] {editText.Trim()}";
            job.LastError = string.Empty;
            job.LeaseUntilUtc = null;
            job.UpdatedAtUtc = DateTime.UtcNow;
            Jobs.Update(job);
            _db.SaveChanges();
            return job;
        }
    }

    public bool Cancel(
        string accountId,
        string scopeKey,
        long creatorUserId,
        string shortCode)
    {
        lock (_sync)
        {
            var job = Jobs.Find(item =>
                    item.AccountId == accountId &&
                    item.ScopeKey == scopeKey &&
                    item.CreatorUserId == creatorUserId &&
                    item.ShortCode == shortCode)
                .Where(IsActive)
                .OrderByDescending(item => item.CreatedAtUtc)
                .FirstOrDefault();
            if (job is null)
                return false;

            job.Status = ScheduledJobStatus.Cancelled;
            job.LeaseUntilUtc = null;
            job.UpdatedAtUtc = DateTime.UtcNow;
            Jobs.Update(job);
            _db.SaveChanges();
            return true;
        }
    }

    public IReadOnlyList<ScheduledJobRecord> ClaimDue(
        DateTime utcNow,
        int batchSize,
        TimeSpan lease)
    {
        lock (_sync)
        {
            foreach (var abandoned in Jobs.Find(item =>
                         item.Status == ScheduledJobStatus.Running &&
                         item.LeaseUntilUtc != null &&
                         item.LeaseUntilUtc <= utcNow))
            {
                abandoned.Status = ScheduledJobStatus.Scheduled;
                abandoned.LeaseUntilUtc = null;
                abandoned.LastError = "执行租约过期，已恢复调度。";
                abandoned.UpdatedAtUtc = utcNow;
                Jobs.Update(abandoned);
            }

            var due = Jobs.Find(item =>
                    item.Status == ScheduledJobStatus.Scheduled &&
                    item.NextRunAtUtc <= utcNow)
                .OrderBy(item => item.NextRunAtUtc)
                .Take(Math.Clamp(batchSize, 1, 100))
                .ToArray();

            foreach (var job in due)
            {
                job.Status = ScheduledJobStatus.Running;
                job.LeaseUntilUtc = utcNow.Add(lease);
                job.UpdatedAtUtc = utcNow;
                Jobs.Update(job);
            }

            if (due.Length > 0)
                _db.SaveChanges();
            return due;
        }
    }

    public int RepairCompositeRelativeSchedules(JobTimeParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        lock (_sync)
        {
            var repaired = 0;
            var candidates = Jobs.Find(item =>
                    item.Status == ScheduledJobStatus.Scheduled &&
                    item.Recurrence == JobRecurrenceKind.None)
                .Where(item =>
                    item.OriginalText.Contains("年", StringComparison.Ordinal) ||
                    item.OriginalText.Contains("个月", StringComparison.Ordinal))
                .ToArray();
            foreach (var job in candidates)
            {
                var created = NormalizeUtc(job.CreatedAtUtc);
                var parsed = parser.Parse(job.OriginalText, new DateTimeOffset(created));
                if (!parsed.Success ||
                    parsed.Schedule is null ||
                    Math.Abs((
                        NormalizeUtc(parsed.Schedule.RunAtUtc) -
                        NormalizeUtc(job.NextRunAtUtc)).TotalMinutes) < 1)
                {
                    continue;
                }

                job.NextRunAtUtc = parsed.Schedule.RunAtUtc;
                job.LocalHour = parsed.Schedule.LocalHour;
                job.LocalMinute = parsed.Schedule.LocalMinute;
                job.LocalDayOfWeek = parsed.Schedule.LocalDayOfWeek;
                job.LastError = "已修复旧版复合年月日解析结果。";
                job.UpdatedAtUtc = DateTime.UtcNow;
                Jobs.Update(job);
                repaired++;
            }

            if (repaired > 0)
                _db.SaveChanges();
            return repaired;
        }
    }

    public void Postpone(string id, DateTime nextRunAtUtc, string reason)
    {
        lock (_sync)
        {
            var job = Jobs.FindById(id);
            if (job is null || job.Status != ScheduledJobStatus.Running)
                return;
            job.Status = ScheduledJobStatus.Scheduled;
            job.NextRunAtUtc = DateTime.SpecifyKind(nextRunAtUtc, DateTimeKind.Utc);
            job.LeaseUntilUtc = null;
            job.LastError = reason;
            job.UpdatedAtUtc = DateTime.UtcNow;
            Jobs.Update(job);
            _db.SaveChanges();
        }
    }

    public BeginDeliveryResult TryBeginDelivery(ScheduledJobRecord job)
    {
        lock (_sync)
        {
            var executionId = ExecutionId(job);
            var existing = Deliveries.FindById(executionId);
            if (existing?.Status == JobDeliveryStatus.Delivered)
                return BeginDeliveryResult.AlreadyDelivered;
            if (existing is not null)
                return BeginDeliveryResult.PreviousAttemptUncertain;

            Deliveries.Insert(new ScheduledJobDeliveryRecord
            {
                Id = executionId,
                JobId = job.Id,
                ScheduledAtUtc = job.NextRunAtUtc,
                Status = JobDeliveryStatus.Started,
                StartedAtUtc = DateTime.UtcNow
            });
            var current = Jobs.FindById(job.Id);
            if (current is not null)
            {
                current.DeliveryAttemptCount++;
                current.UpdatedAtUtc = DateTime.UtcNow;
                Jobs.Update(current);
            }
            _db.SaveChanges();
            return BeginDeliveryResult.Started;
        }
    }

    public void MarkDelivered(string id, DateTime utcNow)
    {
        lock (_sync)
        {
            var job = Jobs.FindById(id);
            if (job is null)
                return;

            var delivery = Deliveries.FindById(ExecutionId(job));
            if (delivery is not null)
            {
                delivery.Status = JobDeliveryStatus.Delivered;
                delivery.FinishedAtUtc = utcNow;
                Deliveries.Update(delivery);
            }

            job.LastRunAtUtc = utcNow;
            job.LeaseUntilUtc = null;
            job.LastError = string.Empty;
            job.UpdatedAtUtc = utcNow;
            if (job.Recurrence == JobRecurrenceKind.None)
            {
                job.Status = ScheduledJobStatus.Completed;
            }
            else
            {
                job.Status = ScheduledJobStatus.Scheduled;
                job.NextRunAtUtc = CalculateNextOccurrence(job, utcNow);
            }
            Jobs.Update(job);
            _db.SaveChanges();
        }
    }

    public void MarkAttemptFailed(string id, string error, DateTime utcNow)
    {
        lock (_sync)
        {
            var job = Jobs.FindById(id);
            if (job is null)
                return;

            var delivery = Deliveries.FindById(ExecutionId(job));
            if (delivery is not null)
            {
                delivery.Status = JobDeliveryStatus.Failed;
                delivery.FinishedAtUtc = utcNow;
                delivery.Error = Truncate(error, 1000);
                Deliveries.Update(delivery);
            }

            // Once the QQ send call has started its outcome can be ambiguous. Do not
            // automatically repeat it and risk duplicate messages or account controls.
            job.Status = ScheduledJobStatus.Failed;
            job.LeaseUntilUtc = null;
            job.LastError = $"发送结果不确定，未自动重试：{Truncate(error, 500)}";
            job.UpdatedAtUtc = utcNow;
            Jobs.Update(job);
            _db.SaveChanges();
        }
    }

    public void MarkUncertainWithoutResend(string id, DateTime utcNow)
    {
        lock (_sync)
        {
            var job = Jobs.FindById(id);
            if (job is null)
                return;
            job.Status = ScheduledJobStatus.Failed;
            job.LeaseUntilUtc = null;
            job.LastError = "检测到上次发送在中断前已开始；为避免重复发送，已停止自动重试。";
            job.UpdatedAtUtc = utcNow;
            Jobs.Update(job);
            _db.SaveChanges();
        }
    }

    private string AllocateShortCode(string accountId, long creatorUserId, string id)
    {
        var seed = (int)((uint)StringComparer.Ordinal.GetHashCode(id) & 0x7fffffff);
        for (var attempt = 0; attempt < 9000; attempt++)
        {
            var value = 1000 + ((seed + attempt * 7919) % 9000);
            var code = value.ToString("0000");
            var collision = Jobs.Find(item =>
                    item.AccountId == accountId &&
                    item.CreatorUserId == creatorUserId &&
                    item.ShortCode == code)
                .Any(IsActive);
            if (!collision)
                return code;
        }

        throw new InvalidOperationException("暂时无法分配提醒编号，请稍后再试。");
    }

    private static bool IsActive(ScheduledJobRecord item) =>
        item.Status is ScheduledJobStatus.Scheduled or ScheduledJobStatus.Running;

    private static string ExecutionId(ScheduledJobRecord job) =>
        $"{job.Id}:{job.NextRunAtUtc.Ticks}";

    private static DateTime CalculateNextOccurrence(ScheduledJobRecord job, DateTime utcNow)
    {
        var zone = ResolveTimeZone(job.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
            zone);
        var localCandidate = new DateTime(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            job.LocalHour,
            job.LocalMinute,
            0,
            DateTimeKind.Unspecified);

        if (job.Recurrence == JobRecurrenceKind.Daily)
        {
            if (localCandidate <= localNow)
                localCandidate = localCandidate.AddDays(1);
        }
        else
        {
            var wanted = (DayOfWeek)(job.LocalDayOfWeek ?? (int)localNow.DayOfWeek);
            var days = ((int)wanted - (int)localCandidate.DayOfWeek + 7) % 7;
            localCandidate = localCandidate.AddDays(days);
            if (localCandidate <= localNow)
                localCandidate = localCandidate.AddDays(7);
        }

        return TimeZoneInfo.ConvertTimeToUtc(localCandidate, zone);
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            return JobTimeZones.Beijing;
        }
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? "未知错误"
            : value.Length <= maxLength ? value : value[..maxLength];
}
