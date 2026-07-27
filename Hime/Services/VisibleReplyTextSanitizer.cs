using System.Text.RegularExpressions;

namespace Hime.Services;

/// <summary>
/// Removes formatting delimiters accidentally left behind when a model wraps a
/// hidden protocol marker (for example [emotion:happy]) in Markdown backticks.
/// Valid single-backtick and fenced-code Markdown is preserved.
/// </summary>
public static class VisibleReplyTextSanitizer
{
    private static readonly Regex RoleLine = new(
        @"(?im)^[ \t]*\[(?<role>USER|ASSISTANT)(?:[^\]\r\n]*)\][ \t]*(?<inline>[^\r\n]*)",
        RegexOptions.Compiled);

    private static readonly Regex LeadingRolePlayAction = new(
        @"(?sx)^\s*(?:(?:（|\()\s*[^（）()\r\n]{1,100}\s*(?:）|\))|(?:\*|＊)[^*＊\r\n]{1,100}(?:\*|＊))\s*",
        RegexOptions.Compiled);

    private static readonly Regex InlineRolePlayAction = new(
        @"(?x)
          (?:（|\()
          [^（）()\r\n]{0,80}
          (?:笑|叹|看|望|转|停|走|歪|脸|红|头|眼|手|轻轻|微微|小声|语气|目光|脚步|摇|眨|戳|点头|低头|抬头|偏过|凑近|退开)
          [^（）()\r\n]{0,80}
          (?:）|\))",
        RegexOptions.Compiled);

    private static readonly Regex SpacedArtifact = new(
        @"(?mx)
          ^[ \t]*(?:`{6}|`{4}|`{2})[ \t]*$
          |
          [ \t]+(?:`{6}|`{4}|`{2})(?=[ \t]*(?:$|[。！？!?，,；;：:、…]))",
        RegexOptions.Compiled);

    private static readonly Regex EvenBacktickRun = new(
        @"(?<!`)(?:`{6}|`{4}|`{2})(?!`)",
        RegexOptions.Compiled);

    private static readonly Regex GenericAssistantClosing = new(
        @"(?isx)
          (?<closing>
            (?:
              有什么我(?:能|可以)?帮(?:到)?你(?:的)?
              |
              如果你(?:还有|有)(?:其他|别的|任何)?(?:问题|需要|需求|想法|事情)
              |
              有(?:任何|其他|别的)?(?:问题|需要|需求)(?:的话)?
              |
              需要(?:我)?帮忙(?:的话)?
            )
            (?:[，,]?\s*(?:都|也)?(?:可以)?\s*(?:随时)?\s*(?:告诉|问|找|跟|和)\s*我(?:说)?(?:就好)?)?
            [呀啊哦吧]?[。！？!?]*
          )
          \s*
          (?<markers>(?:\[(?:emotion|sticker(?:-id)?|情绪|情緒|表情|表情包):[^\]\r\n]+\]\s*)*)
          $",
        RegexOptions.Compiled);

    public static string Clean(string? text)
    {
        var cleaned = text ?? string.Empty;

        // A model must never be allowed to continue the serialized transport transcript
        // by inventing another user/assistant turn. Keep only its reply before the leak.
        cleaned = RemoveSerializedRoleTurns(cleaned);

        // M2-her occasionally emits novel-like stage directions despite explicit persona
        // rules. Strip them deterministically while preserving ordinary explanatory
        // parentheses that do not look like actions.
        string previous;
        do
        {
            previous = cleaned;
            cleaned = LeadingRolePlayAction.Replace(cleaned, string.Empty, 1);
        } while (!string.Equals(previous, cleaned, StringComparison.Ordinal));
        cleaned = InlineRolePlayAction.Replace(cleaned, string.Empty);
        cleaned = Regex.Replace(cleaned, @"[ \t]{2,}", " ");
        cleaned = Regex.Replace(cleaned, @"^[，,；;：:、\s]+", string.Empty);
        cleaned = SpacedArtifact.Replace(cleaned, string.Empty).Trim();
        if (cleaned.Length == 0)
            return string.Empty;

        // A single unmatched even-length run at either outer edge is another
        // common remainder of ``[emotion:...]`` after the marker is removed.
        // Two runs are left intact because they can be intentional inline code.
        var matches = EvenBacktickRun.Matches(cleaned);
        if (matches.Count == 1)
        {
            var match = matches[0];
            if (match.Index == 0 || match.Index + match.Length == cleaned.Length)
                cleaned = cleaned.Remove(match.Index, match.Length).Trim();
        }

        cleaned = RemoveGenericAssistantClosing(cleaned);
        return cleaned;
    }

    private static string RemoveGenericAssistantClosing(string value)
    {
        var match = GenericAssistantClosing.Match(value);
        if (!match.Success)
            return value;

        var body = value[..match.Index].TrimEnd();
        var markers = match.Groups["markers"].Value.Trim();
        return string.IsNullOrWhiteSpace(markers)
            ? body
            : string.IsNullOrWhiteSpace(body)
                ? markers
                : $"{body} {markers}";
    }

    private static string RemoveSerializedRoleTurns(string value)
    {
        var matches = RoleLine.Matches(value);
        if (matches.Count == 0)
            return value;

        var beforeFirstRole = value[..matches[0].Index];
        if (!string.IsNullOrWhiteSpace(beforeFirstRole))
            return beforeFirstRole;

        // Some providers wrap the actual answer in an ASSISTANT label. Recover only
        // that first assistant turn and discard every invented USER/ASSISTANT turn
        // after it. A leading USER turn without an assistant answer is never sent.
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            if (!match.Groups["role"].Value.Equals("ASSISTANT", StringComparison.OrdinalIgnoreCase))
                continue;

            var inline = match.Groups["inline"];
            var start = inline.Success && inline.Length > 0
                ? inline.Index
                : match.Index + match.Length;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : value.Length;
            return value[start..end];
        }

        return string.Empty;
    }
}
