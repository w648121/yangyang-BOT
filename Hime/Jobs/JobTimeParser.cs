using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Hime.Jobs;

public sealed class JobIntentDetector(IOptionsMonitor<JobOptions> options)
{
    public bool IsPotentialRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var normalized = text.Trim();
        var current = options.CurrentValue;
        if (current.CommandPrefixes
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Any(prefix => normalized.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return current.IntentMarkers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Any(marker => normalized.Contains(marker.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class JobTimeParser
{
    private readonly IOptionsMonitor<JobOptions> _options;

    private const string NumberPattern = @"(?:\d{1,4}|[零〇一二两三四五六七八九十百]+)";
    private const string DurationComponentPattern =
        $@"(?:{NumberPattern}\s*(?:年|个月|月|天|小时|分钟|分)|半\s*小时)";

    private static readonly Regex RelativeRegex = new(
        $@"(?<duration>(?:{DurationComponentPattern}\s*)+)(?:后|以后)",
        RegexOptions.Compiled);

    private static readonly Regex DurationTokenRegex = new(
        $@"(?<value>{NumberPattern}|半)\s*(?<unit>年|个月|月|天|小时|分钟|分)",
        RegexOptions.Compiled);

    private static readonly Regex EventRelativeRegex = new(
        $@"(?<event>[\u3400-\u9fffA-Za-z0-9_·]{{1,30}}?)(?<direction>前|后)\s*(?<duration>(?:{DurationComponentPattern}\s*)+)",
        RegexOptions.Compiled);

    private static readonly Regex ClockRegex = new(
        @"(?:(?<period>凌晨|早上|早晨|上午|中午|下午|傍晚|晚上|晚|今晚|明早)\s*)?(?<hour>\d{1,2}|[零〇一二两三四五六七八九十]{1,3})\s*(?:点|时)(?:(?<half>半)|(?<minute>\d{1,2}|[零〇一二两三四五六七八九十]{1,3})\s*分?)?",
        RegexOptions.Compiled);

    private static readonly Regex ColonClockRegex = new(
        @"(?:(?<period>凌晨|早上|早晨|上午|中午|下午|傍晚|晚上|晚|今晚|明早)\s*)?(?<hour>[01]?\d|2[0-3])[:：](?<minute>[0-5]\d)",
        RegexOptions.Compiled);

    private static readonly Regex NumericDateRegex = new(
        @"(?:(?<year>20\d{2})[-/.年])?(?<month>1[0-2]|0?[1-9])[-/.月](?<day>3[01]|[12]\d|0?[1-9])日?",
        RegexOptions.Compiled);

    private static readonly Regex WeeklyRegex = new(
        @"每(?:周|星期)(?<day>[一二三四五六日天])",
        RegexOptions.Compiled);

    private static readonly Regex MarkerContentRegex = new(
        @"(?:别忘记|别忘了|记得)?\s*提醒(?:我|一下|下)?\s*(?<content>.+)$",
        RegexOptions.Compiled);

    public JobTimeParser(IOptionsMonitor<JobOptions> options)
    {
        _options = options;
    }

    public JobParseResult Parse(
        string? rawText,
        DateTimeOffset nowUtc)
    {
        var text = Normalize(rawText);
        if (string.IsNullOrWhiteSpace(text))
            return JobParseResult.Missing(JobParseNeed.Content, "要提醒什么呢？");

        var zone = JobTimeZones.Beijing;
        var utcNow = nowUtc.UtcDateTime;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var recurrence = ResolveRecurrence(text, out var weeklyDay);

        var eventRelative = EventRelativeRegex.Match(text);
        if (eventRelative.Success &&
            TryParseDuration(eventRelative.Groups["duration"].Value, out var eventOffset))
        {
            var eventName = CleanEventName(eventRelative.Groups["event"].Value);
            var eventContent = ExtractContent(text);
            if (string.IsNullOrWhiteSpace(eventContent))
                return JobParseResult.Missing(JobParseNeed.Content, "到时间要提醒你做什么？");
            if (string.IsNullOrWhiteSpace(eventName))
                return JobParseResult.Missing(JobParseNeed.Time, "要以哪个事件作为时间基准？");

            var direction = eventRelative.Groups["direction"].Value == "前" ? -1 : 1;
            return JobParseResult.MissingAnchor(
                $"我需要先确认“{eventName}”具体是什么时间。",
                new JobEventReference(eventName, direction, eventOffset, recurrence, eventContent));
        }

        var relative = RelativeRegex.Match(text);
        DateTime localRun;
        DateTime runAtUtc;
        int localHour;
        int localMinute;

        if (relative.Success)
        {
            if (recurrence != JobRecurrenceKind.None)
                return JobParseResult.Missing(JobParseNeed.Time, "循环提醒需要一个固定时间，例如“每天早上八点”。");

            if (!TryParseDuration(relative.Groups["duration"].Value, out var duration) ||
                duration.IsZero)
                return JobParseResult.Missing(JobParseNeed.Time, "提醒时间需要大于零。");
            try
            {
                localRun = duration.Apply(localNow);
            }
            catch (ArgumentOutOfRangeException)
            {
                return JobParseResult.Missing(JobParseNeed.Time, "这个日期超出了可用范围，请换一个时间。");
            }
            runAtUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localRun, DateTimeKind.Unspecified),
                zone);
            localHour = localRun.Hour;
            localMinute = localRun.Minute;
        }
        else
        {
            if (!TryReadClock(text, out localHour, out localMinute, out var period))
                return JobParseResult.Missing(JobParseNeed.Time, "什么时候提醒你？例如“20分钟后”或“明天早上8点”。");

            if (!TryResolveDate(text, localNow, recurrence, weeklyDay, out var localDate))
                return JobParseResult.Missing(JobParseNeed.Time, "日期还不够明确，请补充“今天、明天”或具体日期。");

            localHour = NormalizeHour(localHour, period);
            if (localHour is < 0 or > 23 || localMinute is < 0 or > 59)
                return JobParseResult.Missing(JobParseNeed.Time, "这个时间看起来不太对，请换成例如“晚上8点30分”。");

            localRun = new DateTime(
                localDate.Year,
                localDate.Month,
                localDate.Day,
                localHour,
                localMinute,
                0,
                DateTimeKind.Unspecified);

            if (recurrence == JobRecurrenceKind.Daily && localRun <= localNow)
                localRun = localRun.AddDays(1);
            else if (recurrence == JobRecurrenceKind.Weekly && localRun <= localNow)
                localRun = localRun.AddDays(7);
            else if (recurrence == JobRecurrenceKind.None && localRun <= localNow)
                return JobParseResult.Missing(
                    JobParseNeed.Time,
                    "这个时间今天已经过去了，请补充“明天”或换一个之后的时间。");

            runAtUtc = TimeZoneInfo.ConvertTimeToUtc(localRun, zone);
        }

        var content = ExtractContent(text);
        if (string.IsNullOrWhiteSpace(content))
            return JobParseResult.Missing(JobParseNeed.Content, "到时间要提醒你做什么？");

        var display = recurrence switch
        {
            JobRecurrenceKind.Daily => $"每天 {localHour:00}:{localMinute:00}",
            JobRecurrenceKind.Weekly =>
                $"每周{ToChineseWeekday((DayOfWeek)(weeklyDay ?? (int)localRun.DayOfWeek))} {localHour:00}:{localMinute:00}",
            _ => localRun.ToString("yyyy-MM-dd HH:mm")
        };

        return JobParseResult.Parsed(new JobScheduleSpec(
            DateTime.SpecifyKind(runAtUtc, DateTimeKind.Utc),
            content,
            recurrence,
            localHour,
            localMinute,
            weeklyDay,
            display));
    }

    public bool TryParseTemporalPoint(
        string? rawText,
        DateTimeOffset nowUtc,
        out TemporalPoint point)
    {
        var text = Normalize(rawText);
        var zone = JobTimeZones.Beijing;
        var utcNow = nowUtc.UtcDateTime;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var recurrence = ResolveRecurrence(text, out var weeklyDay);

        var relative = RelativeRegex.Match(text);
        if (relative.Success &&
            TryParseDuration(relative.Groups["duration"].Value, out var duration) &&
            !duration.IsZero)
        {
            try
            {
                var localRelative = duration.Apply(localNow);
                var relativeUtc = TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(localRelative, DateTimeKind.Unspecified),
                    zone);
                point = new TemporalPoint(
                    relativeUtc,
                    JobRecurrenceKind.None,
                    localRelative.Hour,
                    localRelative.Minute,
                    null,
                    localRelative.ToString("yyyy-MM-dd HH:mm"));
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                point = default!;
                return false;
            }
        }

        if (!TryReadClock(text, out var hour, out var minute, out var period) ||
            !TryResolveDate(text, localNow, recurrence, weeklyDay, out var localDate))
        {
            point = default!;
            return false;
        }

        hour = NormalizeHour(hour, period);
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            point = default!;
            return false;
        }

        var local = new DateTime(
            localDate.Year,
            localDate.Month,
            localDate.Day,
            hour,
            minute,
            0,
            DateTimeKind.Unspecified);
        var runAtUtc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        var display = recurrence switch
        {
            JobRecurrenceKind.Daily => $"每天 {hour:00}:{minute:00}",
            JobRecurrenceKind.Weekly =>
                $"每周{ToChineseWeekday((DayOfWeek)(weeklyDay ?? (int)local.DayOfWeek))} {hour:00}:{minute:00}",
            _ => local.ToString("yyyy-MM-dd HH:mm")
        };
        point = new TemporalPoint(runAtUtc, recurrence, hour, minute, weeklyDay, display);
        return true;
    }

    private string Normalize(string? text)
    {
        var value = (text ?? string.Empty)
            .Trim()
            .Replace('，', ' ')
            .Replace('。', ' ')
            .Replace('？', ' ')
            .Replace('！', ' ');
        foreach (var (source, replacement) in _options.CurrentValue.InputNormalizationReplacements)
        {
            if (!string.IsNullOrWhiteSpace(source))
                value = value.Replace(source, replacement ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        return value;
    }

    private static JobRecurrenceKind ResolveRecurrence(string text, out int? weeklyDay)
    {
        weeklyDay = null;
        var weekly = WeeklyRegex.Match(text);
        if (weekly.Success)
        {
            weeklyDay = (int)ParseWeekday(weekly.Groups["day"].Value);
            return JobRecurrenceKind.Weekly;
        }

        return text.Contains("每天", StringComparison.Ordinal) ||
               text.Contains("每日", StringComparison.Ordinal)
            ? JobRecurrenceKind.Daily
            : JobRecurrenceKind.None;
    }

    private static bool TryReadClock(
        string text,
        out int hour,
        out int minute,
        out string period)
    {
        var colon = ColonClockRegex.Match(text);
        if (colon.Success)
        {
            hour = int.Parse(colon.Groups["hour"].Value);
            minute = int.Parse(colon.Groups["minute"].Value);
            period = colon.Groups["period"].Value;
            return true;
        }

        var clock = ClockRegex.Match(text);
        if (clock.Success)
        {
            hour = ParseChineseNumber(clock.Groups["hour"].Value);
            minute = clock.Groups["half"].Success
                ? 30
                : clock.Groups["minute"].Success
                    ? ParseChineseNumber(clock.Groups["minute"].Value)
                    : 0;
            period = clock.Groups["period"].Value;
            return true;
        }

        hour = 0;
        minute = 0;
        period = string.Empty;
        return false;
    }

    private static bool TryResolveDate(
        string text,
        DateTime localNow,
        JobRecurrenceKind recurrence,
        int? weeklyDay,
        out DateTime date)
    {
        if (recurrence == JobRecurrenceKind.Weekly && weeklyDay.HasValue)
        {
            var days = (weeklyDay.Value - (int)localNow.DayOfWeek + 7) % 7;
            date = localNow.Date.AddDays(days);
            return true;
        }

        if (text.Contains("后天", StringComparison.Ordinal))
        {
            date = localNow.Date.AddDays(2);
            return true;
        }

        if (text.Contains("明天", StringComparison.Ordinal) ||
            text.Contains("明早", StringComparison.Ordinal))
        {
            date = localNow.Date.AddDays(1);
            return true;
        }

        if (text.Contains("今天", StringComparison.Ordinal) ||
            text.Contains("今晚", StringComparison.Ordinal) ||
            recurrence == JobRecurrenceKind.Daily)
        {
            date = localNow.Date;
            return true;
        }

        var numeric = NumericDateRegex.Match(text);
        if (numeric.Success)
        {
            var year = numeric.Groups["year"].Success
                ? int.Parse(numeric.Groups["year"].Value)
                : localNow.Year;
            var month = int.Parse(numeric.Groups["month"].Value);
            var day = int.Parse(numeric.Groups["day"].Value);
            try
            {
                date = new DateTime(year, month, day);
                if (!numeric.Groups["year"].Success && date.Date < localNow.Date)
                    date = date.AddYears(1);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                date = default;
                return false;
            }
        }

        // A clock without a day means today only while that clock is still ahead.
        date = localNow.Date;
        return true;
    }

    private string ExtractContent(string text)
    {
        var marked = MarkerContentRegex.Match(text);
        if (marked.Success)
            return CleanContent(marked.Groups["content"].Value);

        var content = text;
        content = RelativeRegex.Replace(content, " ");
        content = ClockRegex.Replace(content, " ");
        content = ColonClockRegex.Replace(content, " ");
        content = NumericDateRegex.Replace(content, " ");
        content = WeeklyRegex.Replace(content, " ");
        foreach (var value in ConfiguredRemovalMarkers(_options.CurrentValue))
            content = content.Replace(value, " ", StringComparison.Ordinal);
        return CleanContent(content);
    }

    public string RemoveTemporalExpressions(string text)
    {
        var value = Normalize(text);
        value = RelativeRegex.Replace(value, " ");
        value = ClockRegex.Replace(value, " ");
        value = ColonClockRegex.Replace(value, " ");
        value = NumericDateRegex.Replace(value, " ");
        value = WeeklyRegex.Replace(value, " ");
        foreach (var marker in _options.CurrentValue.TemporalRemovalMarkers.Where(marker => !string.IsNullOrWhiteSpace(marker)))
            value = value.Replace(marker, " ", StringComparison.Ordinal);
        return CleanContent(value);
    }

    private static string CleanContent(string value) =>
        Regex.Replace(value, @"\s+", " ")
            .Trim(' ', ',', '，', '.', '。', '!', '！', '?', '？', ':', '：');

    private string CleanEventName(string value)
    {
        var eventName = CleanContent(value);
        var options = _options.CurrentValue;
        foreach (var marker in options.AssistantAliases
                     .Concat(options.EventNameNoiseWords)
                     .Concat(options.TemporalRemovalMarkers)
                     .Where(marker => !string.IsNullOrWhiteSpace(marker))
                     .OrderByDescending(marker => marker.Length))
        {
            while (eventName.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                eventName = CleanContent(eventName[marker.Length..]);
        }

        foreach (var suffix in options.EventNameSuffixes
                     .Where(suffix => !string.IsNullOrWhiteSpace(suffix))
                     .OrderByDescending(suffix => suffix.Length))
        {
            while (eventName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                eventName = CleanContent(eventName[..^suffix.Length]);
        }

        return CleanContent(eventName).TrimEnd('的');
    }

    private static IEnumerable<string> ConfiguredRemovalMarkers(JobOptions options) =>
        options.AssistantAliases
            .Concat(options.ContentRemovalMarkers)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length);

    public static bool TryParseDuration(string value, out CalendarDuration duration)
    {
        var years = 0;
        var months = 0;
        var days = 0;
        var hours = 0;
        var minutes = 0;
        var matches = DurationTokenRegex.Matches(value);
        if (matches.Count == 0)
        {
            duration = new CalendarDuration(0, 0, 0, 0, 0);
            return false;
        }

        foreach (Match match in matches)
        {
            if (match.Groups["value"].Value == "半")
            {
                if (match.Groups["unit"].Value != "小时")
                {
                    duration = new CalendarDuration(0, 0, 0, 0, 0);
                    return false;
                }
                minutes = checked(minutes + 30);
                continue;
            }

            var number = ParseChineseNumber(match.Groups["value"].Value);
            if (number < 0)
            {
                duration = new CalendarDuration(0, 0, 0, 0, 0);
                return false;
            }
            switch (match.Groups["unit"].Value)
            {
                case "年":
                    years = checked(years + number);
                    break;
                case "个月":
                case "月":
                    months = checked(months + number);
                    break;
                case "天":
                    days = checked(days + number);
                    break;
                case "小时":
                    hours = checked(hours + number);
                    break;
                default:
                    minutes = checked(minutes + number);
                    break;
            }
        }

        if (years > 100 || months > 1200 || days > 36500 || hours > 876000 || minutes > 52560000)
        {
            duration = new CalendarDuration(0, 0, 0, 0, 0);
            return false;
        }

        duration = new CalendarDuration(years, months, days, hours, minutes);
        return true;
    }

    private static int NormalizeHour(int hour, string period)
    {
        if (period is "下午" or "傍晚" or "晚上" or "晚" or "今晚")
            return hour is >= 1 and <= 11 ? hour + 12 : hour;
        if (period == "中午")
            return hour is >= 1 and <= 10 ? hour + 12 : hour;
        if (period == "凌晨" && hour == 12)
            return 0;
        if (period is "早上" or "早晨" or "上午" or "明早" && hour == 12)
            return 0;
        return hour;
    }

    private static int ParseChineseNumber(string value)
    {
        if (int.TryParse(value, out var numeric))
            return numeric;

        var total = 0;
        var current = 0;
        foreach (var character in value)
        {
            var digit = character switch
            {
                '零' or '〇' => 0,
                '一' => 1,
                '二' or '两' => 2,
                '三' => 3,
                '四' => 4,
                '五' => 5,
                '六' => 6,
                '七' => 7,
                '八' => 8,
                '九' => 9,
                _ => -1
            };
            if (digit >= 0)
            {
                current = digit;
                continue;
            }

            if (character == '十')
            {
                total += (current == 0 ? 1 : current) * 10;
                current = 0;
            }
            else if (character == '百')
            {
                total += (current == 0 ? 1 : current) * 100;
                current = 0;
            }
        }
        return total + current;
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

    private static string ToChineseWeekday(DayOfWeek day) => day switch
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
