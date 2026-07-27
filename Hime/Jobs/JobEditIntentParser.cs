using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Hime.Jobs;

public sealed record JobEditParseResult(
    JobScheduleSpec? Schedule,
    string Error)
{
    public bool Success => Schedule is not null;
}

/// <summary>
/// Parses edits to an existing reminder. Recurrence-only edits intentionally
/// reuse the existing local clock and content.
/// </summary>
public sealed class JobEditIntentParser(
    JobTimeParser timeParser,
    IOptionsMonitor<JobOptions> options)
{
    private static readonly Regex ShortCodeRegex = new(
        @"(?:编号\s*)?(?<code>\d{4})",
        RegexOptions.Compiled);

    private static readonly Regex WeeklyRegex = new(
        @"每(?:周|星期)(?<day>[一二三四五六日天])",
        RegexOptions.Compiled);

    public bool IsEditRequest(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length > 0 &&
               options.CurrentValue.EditMarkers
                   .Where(marker => !string.IsNullOrWhiteSpace(marker))
                   .Any(marker => value.Contains(marker.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public bool TryReadShortCode(string? text, out string code)
    {
        var match = ShortCodeRegex.Match(text ?? string.Empty);
        if (match.Success)
        {
            code = match.Groups["code"].Value;
            return true;
        }

        code = string.Empty;
        return false;
    }

    public JobEditParseResult Parse(
        string editText,
        ScheduledJobRecord existing,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var payload = Normalize(editText);
        if (string.IsNullOrWhiteSpace(payload))
            return new JobEditParseResult(null, "要把这个提醒改成什么呢？");

        var parseText = payload.Contains("提醒我", StringComparison.Ordinal)
            ? payload
            : $"{payload} 提醒我 {existing.Content}";
        var parsed = timeParser.Parse(parseText, nowUtc);
        if (parsed.Schedule is not null)
            return new JobEditParseResult(parsed.Schedule, string.Empty);

        if (payload.Contains("每天", StringComparison.Ordinal) ||
            payload.Contains("每日", StringComparison.Ordinal))
        {
            return new JobEditParseResult(
                BuildRecurring(
                    existing,
                    JobRecurrenceKind.Daily,
                    null,
                    nowUtc),
                string.Empty);
        }

        var weekly = WeeklyRegex.Match(payload);
        if (weekly.Success)
        {
            var day = ParseWeekday(weekly.Groups["day"].Value);
            return new JobEditParseResult(
                BuildRecurring(
                    existing,
                    JobRecurrenceKind.Weekly,
                    (int)day,
                    nowUtc),
                string.Empty);
        }

        return new JobEditParseResult(
            null,
            $"{parsed.Error} 例如“改成每天中午12点”或“改成每周一上午9点”。");
    }

    private static JobScheduleSpec BuildRecurring(
        ScheduledJobRecord existing,
        JobRecurrenceKind recurrence,
        int? localDayOfWeek,
        DateTimeOffset nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            nowUtc.UtcDateTime,
            JobTimeZones.Beijing);
        var localRun = new DateTime(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            existing.LocalHour,
            existing.LocalMinute,
            0,
            DateTimeKind.Unspecified);

        if (recurrence == JobRecurrenceKind.Daily)
        {
            if (localRun <= localNow)
                localRun = localRun.AddDays(1);
        }
        else
        {
            var wanted = (DayOfWeek)(localDayOfWeek ?? existing.LocalDayOfWeek ?? (int)localNow.DayOfWeek);
            var days = ((int)wanted - (int)localRun.DayOfWeek + 7) % 7;
            localRun = localRun.AddDays(days);
            if (localRun <= localNow)
                localRun = localRun.AddDays(7);
            localDayOfWeek = (int)wanted;
        }

        var runAtUtc = TimeZoneInfo.ConvertTimeToUtc(localRun, JobTimeZones.Beijing);
        var display = recurrence == JobRecurrenceKind.Daily
            ? $"每天 {localRun:HH:mm}"
            : $"每周{WeekdayText((DayOfWeek)(localDayOfWeek ?? 0))} {localRun:HH:mm}";
        return new JobScheduleSpec(
            runAtUtc,
            existing.Content,
            recurrence,
            localRun.Hour,
            localRun.Minute,
            localDayOfWeek,
            display);
    }

    private string Normalize(string text)
    {
        var value = text.Trim();
        value = TrimConfiguredPrefix(value, options.CurrentValue.AssistantAliases);
        value = TrimConfiguredPrefix(value, options.CurrentValue.EditLeadInMarkers);
        value = TrimConfiguredPrefix(value, options.CurrentValue.EditStripMarkers);
        value = value.Trim(' ', '，', ',', '。', '.', '！', '!', '：', ':');
        if (value.StartsWith("是", StringComparison.Ordinal) && value.Length > 1)
            value = value[1..].Trim();
        return value;
    }

    private static string TrimConfiguredPrefix(string text, IEnumerable<string> prefixes)
    {
        var value = text;
        foreach (var prefix in prefixes
                     .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                     .OrderByDescending(prefix => prefix.Length))
        {
            if (!value.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            value = value[prefix.Trim().Length..]
                .Trim(' ', '，', ',', '。', '.', '！', '!', '：', ':');
            break;
        }
        return value;
    }

    private static DayOfWeek ParseWeekday(string value) => value switch
    {
        "一" => DayOfWeek.Monday,
        "二" => DayOfWeek.Tuesday,
        "三" => DayOfWeek.Wednesday,
        "四" => DayOfWeek.Thursday,
        "五" => DayOfWeek.Friday,
        "六" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday
    };

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
