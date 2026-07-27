using System.Text.RegularExpressions;

namespace Hime.Services;

/// <summary>
/// Parses the stable sticker-request grammar while resolving all emotion words
/// through the hot-reloadable vocabulary. Command syntax stays deterministic;
/// emotion semantics remain data-driven.
/// </summary>
public sealed class StickerRequestParser(StickerLabelVocabulary labels)
{
    private static readonly Regex Intent = new(
        @"(?:表情|情绪标签|\bemoji(?:s)?\b|\bsticker(?:s)?\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CountBeforeNoun = new(
        @"(?:(?<number>[1-3])|(?<chinese>[一二两俩三壹贰叁什]))\s*(?:个|张|只|套|枚|条|次|发|轮|连)?\s*(?:表情包?|情绪标签|emoji(?:s)?|sticker(?:s)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShortCount = new(
        @"(?:来|发|整|要|给(?:我)?)(?:(?<number>[1-3])|(?<chinese>[一二两俩三壹贰叁什]))(?:个|张|只|套|枚|条|次|发|轮|连)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public int GetRequestedCount(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !Intent.IsMatch(prompt))
            return 0;

        var match = CountBeforeNoun.Match(prompt);
        if (!match.Success)
            match = ShortCount.Match(prompt);
        if (!match.Success)
            return 0;

        if (match.Groups["number"].Success &&
            int.TryParse(match.Groups["number"].Value, out var numeric))
            return Math.Clamp(numeric, 1, 3);

        return match.Groups["chinese"].Value switch
        {
            "一" or "壹" => 1,
            "二" or "两" or "俩" or "贰" => 2,
            "三" or "叁" or "什" => 3,
            _ => 0
        };
    }

    public IReadOnlyList<string> GetRequestedEmotions(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !Intent.IsMatch(prompt))
            return [];

        var matches = new List<(int Index, string Emotion)>();
        foreach (var definition in labels.All)
        {
            foreach (var term in definition.Aliases
                         .Append(definition.ChineseName)
                         .Append(definition.Canonical))
            {
                var index = FindLabel(prompt, term);
                if (index < 0)
                    continue;

                var negated = index > 0 && prompt[index - 1] is '不' or '没' or '无';
                if (negated)
                {
                    if (!string.IsNullOrWhiteSpace(definition.NegatedBaseEmotion))
                        matches.Add((index - 1, definition.NegatedBaseEmotion));
                    continue;
                }

                matches.Add((index, definition.BaseEmotion));
            }
        }

        return matches
            .OrderBy(match => match.Index)
            .Select(match => match.Emotion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
    }

    private static int FindLabel(string prompt, string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return -1;
        if (term.All(character => character <= 127))
        {
            var match = Regex.Match(
                prompt,
                $@"(?<![a-zA-Z0-9_]){Regex.Escape(term)}(?![a-zA-Z0-9_])",
                RegexOptions.IgnoreCase);
            return match.Success ? match.Index : -1;
        }

        return prompt.IndexOf(term, StringComparison.OrdinalIgnoreCase);
    }
}
