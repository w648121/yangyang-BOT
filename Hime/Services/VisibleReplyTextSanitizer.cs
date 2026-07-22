using System.Text.RegularExpressions;

namespace Hime.Services;

/// <summary>
/// Removes formatting delimiters accidentally left behind when a model wraps a
/// hidden protocol marker (for example [emotion:happy]) in Markdown backticks.
/// Valid single-backtick and fenced-code Markdown is preserved.
/// </summary>
public static class VisibleReplyTextSanitizer
{
    private static readonly Regex RoleContinuation = new(
        @"(?im)^[ \t]*\[(?:USER|ASSISTANT)(?:[^\]\r\n]*)\][ \t]*$",
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

    public static string Clean(string? text)
    {
        var cleaned = text ?? string.Empty;

        // A model must never be allowed to continue the serialized transport transcript
        // by inventing another user/assistant turn. Keep only its reply before the leak.
        var continuation = RoleContinuation.Match(cleaned);
        if (continuation.Success)
            cleaned = cleaned[..continuation.Index];

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

        return cleaned;
    }
}
