using System.Security.Cryptography;
using System.Text;
using Hime.Data;
using Hime.Data.Models;
using Hime.Data.Services;
using Hime.Messaging;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace Hime.Jobs;

public sealed record TemporalAnchorResolution(
    JobScheduleSpec Schedule,
    string Source);

/// <summary>
/// Resolves arbitrary named events (work, meetings, classes, flights, streams,
/// birthdays, and so on) without hard-coding one event type. Only explicit,
/// deterministic time evidence is accepted.
/// </summary>
public sealed class TemporalAnchorService
{
    private readonly ILiteCollection<TemporalAnchorRecord> _anchors;
    private readonly JobTimeParser _parser;
    private readonly IPersonaStateService _personaStates;
    private readonly IChatService _chat;
    private readonly ILogger<TemporalAnchorService> _logger;
    private readonly object _sync = new();

    public TemporalAnchorService(
        HimeDbContext db,
        JobTimeParser parser,
        IPersonaStateService personaStates,
        IChatService chat,
        ILogger<TemporalAnchorService> logger)
    {
        _anchors = db.Database.GetCollection<TemporalAnchorRecord>("temporal_event_anchors");
        _anchors.EnsureIndex(item => item.UserId);
        _anchors.EnsureIndex(item => item.GroupId);
        _anchors.EnsureIndex(item => item.NormalizedEventName);
        _parser = parser;
        _personaStates = personaStates;
        _chat = chat;
        _logger = logger;
    }

    public void ObserveExplicitStatement(IncomingMessage message)
    {
        var text = message.Text.Trim();
        if (string.IsNullOrWhiteSpace(text) ||
            text.Contains("提醒", StringComparison.Ordinal) ||
            text.Contains("闹钟", StringComparison.Ordinal))
        {
            return;
        }

        if (!_parser.TryParseTemporalPoint(text, DateTimeOffset.UtcNow, out var point))
            return;
        var eventName = TryExtractEventName(text);
        if (string.IsNullOrWhiteSpace(eventName))
            return;
        Save(message, eventName, point, text);
    }

    public bool LearnExpectedEvent(
        IncomingMessage message,
        string eventName,
        JobRecurrenceKind recurrenceHint,
        string statement)
    {
        if (!_parser.TryParseTemporalPoint(statement, DateTimeOffset.UtcNow, out var point))
            return false;
        if (recurrenceHint != JobRecurrenceKind.None &&
            point.Recurrence == JobRecurrenceKind.None)
        {
            point = point with
            {
                Recurrence = recurrenceHint,
                LocalDayOfWeek = recurrenceHint == JobRecurrenceKind.Weekly
                    ? point.LocalDayOfWeek ?? (int)TimeZoneInfo.ConvertTimeFromUtc(
                        point.RunAtUtc,
                        JobTimeZones.Beijing).DayOfWeek
                    : null
            };
        }
        Save(message, eventName, point, statement);
        return true;
    }

    public TemporalAnchorResolution? TryResolve(
        IncomingMessage message,
        JobEventReference reference,
        DateTimeOffset nowUtc)
    {
        var normalizedEvent = NormalizeEventName(reference.EventName);
        if (string.IsNullOrWhiteSpace(normalizedEvent))
            return null;

        var stored = FindStored(message, normalizedEvent);
        if (stored is not null)
        {
            var point = ToPoint(stored);
            var schedule = BuildSchedule(point, reference, nowUtc);
            if (schedule is not null)
                return new TemporalAnchorResolution(schedule, "saved-event-anchor");
        }

        var facts = _personaStates.GetConfirmedFacts(
            message.SenderId,
            message.GroupId,
            reference.EventName,
            maximum: 12);
        foreach (var fact in facts)
        {
            if (!FactMatches(fact, normalizedEvent) ||
                !_parser.TryParseTemporalPoint(fact.Value, nowUtc, out var point))
            {
                continue;
            }
            var adjusted = ApplyRecurrenceHint(point, reference.RecurrenceHint);
            Save(message, reference.EventName, adjusted, $"memory-fact:{fact.Key}");
            var schedule = BuildSchedule(adjusted, reference, nowUtc);
            if (schedule is not null)
                return new TemporalAnchorResolution(schedule, $"structured-memory:{fact.Key}");
        }

        foreach (var candidate in ReadMemoryCandidates(message, reference.EventName))
        {
            if (!TextMatchesEvent(candidate, normalizedEvent) ||
                !_parser.TryParseTemporalPoint(candidate, nowUtc, out var point))
            {
                continue;
            }
            var adjusted = ApplyRecurrenceHint(point, reference.RecurrenceHint);
            Save(message, reference.EventName, adjusted, $"chat-memory:{candidate}");
            var schedule = BuildSchedule(adjusted, reference, nowUtc);
            if (schedule is not null)
                return new TemporalAnchorResolution(schedule, "chat-memory");
        }

        return null;
    }

    private IReadOnlyList<string> ReadMemoryCandidates(
        IncomingMessage message,
        string eventName)
    {
        var recent = _chat.GetHistory(message.SenderId, message.GroupId, eventName)
            .Where(item =>
                item.Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                item.UserId == message.SenderId)
            .OrderByDescending(item => item.Time)
            .Select(item => item.Content);
        var archived = _chat.GetRelevantMemories(
                message.SenderId,
                message.GroupId,
                eventName,
                maximum: 16)
            .OrderByDescending(item => item.OccurredAtUtc)
            .Select(item => item.Content);
        return recent
            .Concat(archived)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Take(30)
            .ToArray();
    }

    private TemporalAnchorRecord? FindStored(
        IncomingMessage message,
        string normalizedEvent)
    {
        lock (_sync)
        {
            return _anchors.Find(item =>
                    item.Platform == message.Platform &&
                    item.AccountId == message.AccountId &&
                    item.UserId == message.SenderId)
                .Where(item =>
                    item.GroupId == message.GroupId ||
                    item.GroupId is null)
                .Where(item =>
                    item.NormalizedEventName == normalizedEvent ||
                    item.NormalizedEventName.Contains(normalizedEvent, StringComparison.Ordinal) ||
                    normalizedEvent.Contains(item.NormalizedEventName, StringComparison.Ordinal))
                .OrderByDescending(item => item.GroupId == message.GroupId)
                .ThenByDescending(item => item.UpdatedAtUtc)
                .FirstOrDefault();
        }
    }

    private void Save(
        IncomingMessage message,
        string eventName,
        TemporalPoint point,
        string sourceText)
    {
        var normalized = NormalizeEventName(eventName);
        if (string.IsNullOrWhiteSpace(normalized))
            return;
        var idSource =
            $"{message.Platform}|{message.AccountId}|{message.SenderId}|{message.GroupId?.ToString() ?? "private"}|{normalized}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idSource)))[..24]
            .ToLowerInvariant();
        var record = new TemporalAnchorRecord
        {
            Id = id,
            Platform = message.Platform,
            AccountId = message.AccountId,
            UserId = message.SenderId,
            GroupId = message.GroupId,
            EventName = eventName.Trim(),
            NormalizedEventName = normalized,
            Kind = point.Recurrence switch
            {
                JobRecurrenceKind.Daily => TemporalAnchorKind.Daily,
                JobRecurrenceKind.Weekly => TemporalAnchorKind.Weekly,
                _ => TemporalAnchorKind.OneOff
            },
            AnchorAtUtc = point.RunAtUtc,
            LocalHour = point.LocalHour,
            LocalMinute = point.LocalMinute,
            LocalDayOfWeek = point.LocalDayOfWeek,
            SourceText = sourceText.Length <= 500 ? sourceText : sourceText[..500],
            UpdatedAtUtc = DateTime.UtcNow
        };
        lock (_sync)
            _anchors.Upsert(record);
        _logger.LogInformation(
            "Remembered temporal event anchor (UserId={UserId}, GroupId={GroupId}, Event={Event}, Recurrence={Recurrence}, Time={Hour:00}:{Minute:00})",
            message.SenderId,
            message.GroupId,
            normalized,
            point.Recurrence,
            point.LocalHour,
            point.LocalMinute);
    }

    private static JobScheduleSpec? BuildSchedule(
        TemporalPoint anchor,
        JobEventReference reference,
        DateTimeOffset nowUtc)
    {
        var zone = JobTimeZones.Beijing;
        var utcNow = nowUtc.UtcDateTime;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var recurrence = reference.RecurrenceHint != JobRecurrenceKind.None
            ? reference.RecurrenceHint
            : anchor.Recurrence;
        if (recurrence != JobRecurrenceKind.None &&
            (reference.Offset.Years > 0 || reference.Offset.Months > 0))
        {
            return null;
        }

        var anchorLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(anchor.RunAtUtc, DateTimeKind.Utc),
            zone);
        DateTime localRun;
        int? scheduledWeekday = null;

        if (recurrence == JobRecurrenceKind.Daily)
        {
            var baseLocal = new DateTime(
                localNow.Year,
                localNow.Month,
                localNow.Day,
                anchor.LocalHour,
                anchor.LocalMinute,
                0,
                DateTimeKind.Unspecified);
            localRun = reference.Offset.Apply(baseLocal, reference.Direction);
            while (localRun <= localNow)
                localRun = localRun.AddDays(1);
        }
        else if (recurrence == JobRecurrenceKind.Weekly)
        {
            var wanted = (DayOfWeek)(anchor.LocalDayOfWeek ?? (int)anchorLocal.DayOfWeek);
            var days = ((int)wanted - (int)localNow.DayOfWeek + 7) % 7;
            var baseLocal = new DateTime(
                    localNow.Year,
                    localNow.Month,
                    localNow.Day,
                    anchor.LocalHour,
                    anchor.LocalMinute,
                    0,
                    DateTimeKind.Unspecified)
                .AddDays(days);
            localRun = reference.Offset.Apply(baseLocal, reference.Direction);
            while (localRun <= localNow)
                localRun = localRun.AddDays(7);
            scheduledWeekday = (int)localRun.DayOfWeek;
        }
        else
        {
            localRun = reference.Offset.Apply(anchorLocal, reference.Direction);
            if (localRun <= localNow)
                return null;
        }

        var runAtUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localRun, DateTimeKind.Unspecified),
            zone);
        var display = recurrence switch
        {
            JobRecurrenceKind.Daily => $"每天 {localRun:HH:mm}",
            JobRecurrenceKind.Weekly => $"每周{WeekdayText(localRun.DayOfWeek)} {localRun:HH:mm}",
            _ => localRun.ToString("yyyy-MM-dd HH:mm")
        };
        return new JobScheduleSpec(
            runAtUtc,
            reference.Content,
            recurrence,
            localRun.Hour,
            localRun.Minute,
            scheduledWeekday,
            display);
    }

    private static TemporalPoint ToPoint(TemporalAnchorRecord record)
    {
        var recurrence = record.Kind switch
        {
            TemporalAnchorKind.Daily => JobRecurrenceKind.Daily,
            TemporalAnchorKind.Weekly => JobRecurrenceKind.Weekly,
            _ => JobRecurrenceKind.None
        };
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(record.AnchorAtUtc, DateTimeKind.Utc),
            JobTimeZones.Beijing);
        var display = recurrence switch
        {
            JobRecurrenceKind.Daily => $"每天 {record.LocalHour:00}:{record.LocalMinute:00}",
            JobRecurrenceKind.Weekly =>
                $"每周{WeekdayText((DayOfWeek)(record.LocalDayOfWeek ?? (int)local.DayOfWeek))} {record.LocalHour:00}:{record.LocalMinute:00}",
            _ => local.ToString("yyyy-MM-dd HH:mm")
        };
        return new TemporalPoint(
            record.AnchorAtUtc,
            recurrence,
            record.LocalHour,
            record.LocalMinute,
            record.LocalDayOfWeek,
            display);
    }

    private static TemporalPoint ApplyRecurrenceHint(
        TemporalPoint point,
        JobRecurrenceKind hint) =>
        hint == JobRecurrenceKind.None || point.Recurrence != JobRecurrenceKind.None
            ? point
            : point with
            {
                Recurrence = hint,
                LocalDayOfWeek = hint == JobRecurrenceKind.Weekly
                    ? point.LocalDayOfWeek ?? (int)TimeZoneInfo.ConvertTimeFromUtc(
                        point.RunAtUtc,
                        JobTimeZones.Beijing).DayOfWeek
                    : null
            };

    private static bool FactMatches(
        PersonaMemoryFact fact,
        string normalizedEvent)
    {
        var aliases = (fact.Aliases ?? [])
            .Append(fact.Key)
            .Append(fact.Value)
            .Select(NormalizeEventName)
            .Where(value => !string.IsNullOrWhiteSpace(value));
        if (fact.Key.Equals("work_end_time", StringComparison.OrdinalIgnoreCase))
            aliases = aliases.Append("下班");
        return aliases.Any(value =>
            value.Contains(normalizedEvent, StringComparison.Ordinal) ||
            normalizedEvent.Contains(value, StringComparison.Ordinal));
    }

    private static bool TextMatchesEvent(string text, string normalizedEvent)
    {
        var normalizedText = NormalizeEventName(text);
        return normalizedText.Contains(normalizedEvent, StringComparison.Ordinal) ||
               normalizedEvent.Contains(normalizedText, StringComparison.Ordinal);
    }

    private static string? TryExtractEventName(string text)
    {
        var candidate = JobTimeParser.RemoveTemporalExpressions(text);
        foreach (var noise in new[]
                 {
                     "我的", "我们", "我", "通常", "一般", "平时", "会在", "将在", "将会", "要在", "是",
                     "时间是", "时间", "的时候"
                 })
        {
            candidate = candidate.Replace(noise, string.Empty, StringComparison.Ordinal);
        }
        candidate = candidate.Trim(' ', '，', ',', '。', '.', '！', '!', '？', '?', '：', ':', '的');
        return candidate.Length is >= 1 and <= 24 ? candidate : null;
    }

    public static string NormalizeEventName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character is '·' or '_')
                builder.Append(character);
        }
        var normalized = builder.ToString();
        foreach (var suffix in new[] { "的时间", "时间", "的时候" })
            normalized = normalized.Replace(suffix, string.Empty, StringComparison.Ordinal);
        return normalized;
    }

    private static string WeekdayText(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日"
    };
}
