using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// A lightweight, hot-reloadable social intent classifier. It does not decide
/// the final wording; it only identifies the user's social move so the prompt
/// can ask the active persona to answer the right kind of moment.
/// </summary>
public sealed class SocialIntentAnalyzer(IOptionsMonitor<SocialIntelligenceOptions> options)
{
    public SocialIntentResult Analyze(
        string? text,
        DialogueDecision decision,
        ConversationRoute route)
    {
        var current = options.CurrentValue;
        var normalizedText = text?.Trim() ?? string.Empty;
        if (!current.Enabled || current.IntentRules.Count == 0)
        {
            return BuildDefault(current, decision, route);
        }

        var scored = current.IntentRules
            .Select(rule =>
            {
                var matched = rule.Markers
                    .Where(marker => !string.IsNullOrWhiteSpace(marker))
                    .Where(marker => normalizedText.Contains(
                        marker.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var markerWeight = rule.Markers.Count == 0
                    ? 0
                    : matched.Length / (double)Math.Max(1, rule.Markers.Count);
                var routeWeight = decision.Act.ToString()
                    .Equals(rule.SocialAction, StringComparison.OrdinalIgnoreCase)
                    ? 0.15d
                    : 0d;
                return new
                {
                    Rule = rule,
                    Matched = matched,
                    Score = Math.Clamp(markerWeight + routeWeight, 0, 1)
                };
            })
            .Where(item => item.Matched.Length > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Matched.Length)
            .FirstOrDefault();

        if (scored is null)
            return BuildDefault(current, decision, route);

        return new SocialIntentResult(
            NormalizeId(scored.Rule.Id, current.DefaultIntentId),
            scored.Rule.Description.Trim(),
            scored.Rule.Posture.Trim(),
            scored.Rule.ReplyGoal.Trim(),
            string.IsNullOrWhiteSpace(scored.Rule.SocialAction)
                ? decision.Act.ToString()
                : scored.Rule.SocialAction.Trim(),
            scored.Rule.StickerIntent.Trim(),
            scored.Matched,
            scored.Rule.Avoid
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToArray(),
            scored.Score);
    }

    private static SocialIntentResult BuildDefault(
        SocialIntelligenceOptions options,
        DialogueDecision decision,
        ConversationRoute route) =>
        new(
            NormalizeId(options.DefaultIntentId, "ordinary_chat"),
            $"ordinary {route.Mode} turn",
            options.DefaultPosture.Trim(),
            options.DefaultReplyGoal.Trim(),
            decision.Act.ToString(),
            string.Empty,
            [],
            [],
            0.1d);

    private static string NormalizeId(string? value, string fallback)
    {
        var normalized = new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
            .ToArray());
        return normalized.Length == 0 ? fallback : normalized;
    }
}
