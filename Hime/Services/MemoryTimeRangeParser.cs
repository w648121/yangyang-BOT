using System.Globalization;
using System.Text.RegularExpressions;

namespace Hime.Services;

public sealed record MemoryTimeRange(
    DateTime StartUtc,
    DateTime EndUtcExclusive,
    string Label);

/// <summary>
/// 将聊天中的中文相对时间转换为北京时间自然日范围，供长期记忆检索使用。
/// </summary>
public static partial class MemoryTimeRangeParser
{
    public static TimeZoneInfo BeijingTimeZone { get; } = ResolveBeijingTimeZone();

    public static bool TryParse(
        string? text,
        out MemoryTimeRange range,
        DateTimeOffset? nowUtc = null)
    {
        range = default!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var beijingNow = TimeZoneInfo.ConvertTime(now, BeijingTimeZone);
        var today = beijingNow.Date;
        DateTime startLocal;
        DateTime endLocal;
        string label;

        if (text.Contains("今天", StringComparison.Ordinal))
        {
            startLocal = today;
            endLocal = today.AddDays(1);
            label = "今天";
        }
        else if (text.Contains("前天", StringComparison.Ordinal))
        {
            startLocal = today.AddDays(-2);
            endLocal = startLocal.AddDays(1);
            label = "前天";
        }
        else if (text.Contains("昨天", StringComparison.Ordinal))
        {
            startLocal = today.AddDays(-1);
            endLocal = startLocal.AddDays(1);
            label = "昨天";
        }
        else if (text.Contains("上周", StringComparison.Ordinal))
        {
            var offset = ((int)today.DayOfWeek + 6) % 7;
            endLocal = today.AddDays(-offset);
            startLocal = endLocal.AddDays(-7);
            label = "上周";
        }
        else if (text.Contains("上个月", StringComparison.Ordinal) ||
                 text.Contains("上月", StringComparison.Ordinal))
        {
            endLocal = new DateTime(today.Year, today.Month, 1);
            startLocal = endLocal.AddMonths(-1);
            label = "上个月";
        }
        else if (TryMatchOffset(DaysAgoRegex(), text, out var days))
        {
            startLocal = today.AddDays(-days);
            endLocal = startLocal.AddDays(1);
            label = $"{days}天前";
        }
        else if (TryMatchOffset(WeeksAgoRegex(), text, out var weeks))
        {
            startLocal = today.AddDays(-7 * weeks);
            endLocal = startLocal.AddDays(7);
            label = $"{weeks}周前";
        }
        else if (TryMatchOffset(MonthsAgoRegex(), text, out var months))
        {
            var target = today.AddMonths(-months);
            startLocal = new DateTime(target.Year, target.Month, target.Day);
            endLocal = startLocal.AddDays(1);
            label = $"{months}个月前";
        }
        else
        {
            return false;
        }

        range = new MemoryTimeRange(
            LocalToUtc(startLocal),
            LocalToUtc(endLocal),
            label);
        return true;
    }

    public static DateTime ToBeijing(DateTime utc)
    {
        var normalized = utc.Kind == DateTimeKind.Utc
            ? utc
            : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(normalized, BeijingTimeZone);
    }

    private static bool TryMatchOffset(Regex regex, string text, out int value)
    {
        value = 0;
        var match = regex.Match(text);
        return match.Success &&
               TryParseChineseNumber(match.Groups["value"].Value, out value) &&
               value is >= 1 and <= 3650;
    }

    private static bool TryParseChineseNumber(string value, out int result)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result))
            return true;

        var digits = new Dictionary<char, int>
        {
            ['零'] = 0, ['〇'] = 0, ['一'] = 1, ['二'] = 2, ['两'] = 2,
            ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6, ['七'] = 7,
            ['八'] = 8, ['九'] = 9
        };
        var total = 0;
        var current = 0;
        foreach (var ch in value)
        {
            if (digits.TryGetValue(ch, out var digit))
            {
                current = digit;
                continue;
            }

            if (ch == '十')
            {
                total += (current == 0 ? 1 : current) * 10;
                current = 0;
                continue;
            }

            if (ch == '百')
            {
                total += (current == 0 ? 1 : current) * 100;
                current = 0;
                continue;
            }

            result = 0;
            return false;
        }

        result = total + current;
        return result > 0;
    }

    private static DateTime LocalToUtc(DateTime value) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(value, DateTimeKind.Unspecified),
            BeijingTimeZone);

    private static TimeZoneInfo ResolveBeijingTimeZone()
    {
        foreach (var id in new[] { "China Standard Time", "Asia/Shanghai" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "UTC+08",
            TimeSpan.FromHours(8),
            "北京时间",
            "北京时间");
    }

    [GeneratedRegex(@"(?<value>[零〇一二两三四五六七八九十百\d]+)\s*天前", RegexOptions.CultureInvariant)]
    private static partial Regex DaysAgoRegex();

    [GeneratedRegex(@"(?<value>[零〇一二两三四五六七八九十百\d]+)\s*(?:周|星期)前", RegexOptions.CultureInvariant)]
    private static partial Regex WeeksAgoRegex();

    [GeneratedRegex(@"(?<value>[零〇一二两三四五六七八九十百\d]+)\s*个?月前", RegexOptions.CultureInvariant)]
    private static partial Regex MonthsAgoRegex();
}
