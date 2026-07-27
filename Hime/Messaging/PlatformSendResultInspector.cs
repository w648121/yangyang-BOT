using System.Collections;
using System.Globalization;
using System.Reflection;

namespace Hime.Messaging;

/// <summary>
/// Extracts a native platform message id from adapter-specific send results.
/// Sora adapters may wrap the id in different result/data/value shapes, so this
/// helper stays reflection-based and best-effort.
/// </summary>
public static class PlatformSendResultInspector
{
    private static readonly string[] PreferredPropertyNames =
    [
        "MessageId",
        "MessageID",
        "MessageSeq",
        "MessageSequence",
        "MsgId",
        "MsgID",
        "Id",
        "ID",
        "Value",
        "Data",
        "Result",
        "Message"
    ];

    public static long? TryGetMessageId(object? result) =>
        TryGetMessageId(result, 0);

    private static long? TryGetMessageId(object? result, int depth)
    {
        if (result is null || depth > 4)
            return null;

        if (TryConvertNumber(result, out var direct))
            return direct;

        if (result is string text &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        var type = result.GetType();
        if (type.Name.Contains("MessageId", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(result.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var fromText))
        {
            return fromText;
        }

        var properties = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0)
            .ToArray();

        foreach (var name in PreferredPropertyNames)
        {
            var property = properties.FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (property is null)
                continue;

            var nested = TryGetMessageId(SafeGet(property, result), depth + 1);
            if (nested is > 0)
                return nested;
        }

        foreach (var property in properties.Where(property =>
                     property.Name.Contains("message", StringComparison.OrdinalIgnoreCase) ||
                     property.Name.Contains("result", StringComparison.OrdinalIgnoreCase) ||
                     property.Name.Contains("data", StringComparison.OrdinalIgnoreCase) ||
                     property.Name.Contains("id", StringComparison.OrdinalIgnoreCase)))
        {
            var nested = TryGetMessageId(SafeGet(property, result), depth + 1);
            if (nested is > 0)
                return nested;
        }

        if (result is IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                var nested = TryGetMessageId(item, depth + 1);
                if (nested is > 0)
                    return nested;
            }
        }

        return null;
    }

    private static object? SafeGet(PropertyInfo property, object target)
    {
        try
        {
            return property.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryConvertNumber(object value, out long result)
    {
        switch (value)
        {
            case long longValue:
                result = longValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case uint uintValue:
                result = uintValue;
                return true;
            case ulong ulongValue when ulongValue <= long.MaxValue:
                result = (long)ulongValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case ushort ushortValue:
                result = ushortValue;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
