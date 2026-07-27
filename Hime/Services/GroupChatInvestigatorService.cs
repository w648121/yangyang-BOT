using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Hime.Data.Models;
using Hime.Data.Services;
using Hime.Messaging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

public sealed class GroupChatInvestigatorOptions
{
    public bool Enabled { get; set; } = true;

    public int RecentMessageLimit { get; set; } = 80;

    public int MaxEvidenceMessages { get; set; } = 8;

    public int MaxRankItems { get; set; } = 5;

    public int MaxExtractedTerms { get; set; } = 6;

    public int MinimumTermLength { get; set; } = 2;

    public int MaxTermLength { get; set; } = 40;

    public List<string> RequestMarkers { get; set; } = [];

    public List<string> QuoteFocusMarkers { get; set; } = [];

    public List<string> SpeakerQuestionMarkers { get; set; } = [];

    public List<string> OriginalTextMarkers { get; set; } = [];

    public List<string> CountQuestionMarkers { get; set; } = [];

    public List<string> RankingQuestionMarkers { get; set; } = [];

    public List<string> MentionQuestionMarkers { get; set; } = [];

    public List<string> StopWords { get; set; } = [];

    public List<string> CommonPromptRules { get; set; } = [];

    public bool IsValid() =>
        !Enabled ||
        (RecentMessageLimit is >= 4 and <= 500 &&
         MaxEvidenceMessages is >= 1 and <= 30 &&
         MaxRankItems is >= 1 and <= 20 &&
         MaxExtractedTerms is >= 1 and <= 20 &&
         MinimumTermLength is >= 1 and <= 12 &&
         MaxTermLength >= MinimumTermLength &&
         AllMarkers().Any(marker => !string.IsNullOrWhiteSpace(marker)) &&
         CommonPromptRules.Any(rule => !string.IsNullOrWhiteSpace(rule)));

    public IEnumerable<string> AllMarkers() =>
        RequestMarkers
            .Concat(QuoteFocusMarkers)
            .Concat(SpeakerQuestionMarkers)
            .Concat(OriginalTextMarkers)
            .Concat(CountQuestionMarkers)
            .Concat(RankingQuestionMarkers)
            .Concat(MentionQuestionMarkers);
}

public sealed class GroupChatInvestigatorService(
    IGroupActivityService activities,
    IOptionsMonitor<GroupChatInvestigatorOptions> options)
{
    private static readonly Regex TokenRegex = new(
        @"[\p{L}\p{N}_#@]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string BuildPromptContext(
        long? groupId,
        long sourceMessageId,
        string? prompt,
        ConversationFocusDecision focus)
    {
        var current = options.CurrentValue;
        if (!current.Enabled || !groupId.HasValue)
            return string.Empty;

        var recent = activities
            .GetRecentMessages(groupId.Value, Math.Clamp(current.RecentMessageLimit, 4, 500))
            .OrderBy(message => message.Time)
            .ToArray();
        if (recent.Length == 0)
            return string.Empty;

        var currentMessage = sourceMessageId > 0
            ? recent.LastOrDefault(message => message.MessageId == sourceMessageId)
            : null;
        var probe = Normalize($"{prompt} {currentMessage?.QuotedText}");
        if (!ShouldInvestigate(probe, currentMessage, focus, current))
            return string.Empty;

        var queryTerms = ExtractTerms(probe, currentMessage?.QuotedText, current);
        var evidence = SelectEvidence(recent, currentMessage, queryTerms, current);
        var rankings = BuildSpeakerRanking(recent, current);
        var counts = BuildTermCounts(recent, queryTerms, current);

        if (evidence.Count == 0 &&
            rankings.Count == 0 &&
            counts.Count == 0 &&
            string.IsNullOrWhiteSpace(currentMessage?.QuotedText))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("<group_chat_investigation>");
        foreach (var rule in current.CommonPromptRules.Where(NotBlank).Take(10))
            builder.AppendLine($"- {Escape(rule.Trim())}");
        builder.AppendLine($"request: {Escape(Trim(probe, 260))}");
        if (!string.IsNullOrWhiteSpace(currentMessage?.QuotedText))
            builder.AppendLine($"quoted_text: {Escape(Trim(currentMessage.QuotedText, 320))}");
        if (currentMessage?.ReplyToMessageId is > 0)
            builder.AppendLine($"reply_to_message_id: {currentMessage.ReplyToMessageId.Value}");
        if (currentMessage?.ReplyToUserId is > 0)
            builder.AppendLine($"reply_to_user_id: {currentMessage.ReplyToUserId.Value}");
        if (queryTerms.Count > 0)
            builder.AppendLine($"query_terms: {string.Join(", ", queryTerms.Select(Escape))}");

        if (evidence.Count > 0)
        {
            builder.AppendLine("relevant_messages:");
            foreach (var item in evidence)
            {
                builder.Append("  - time=").Append(item.Message.Time.ToLocalTime().ToString("HH:mm:ss"))
                    .Append("; qq=").Append(item.Message.UserId)
                    .Append("; nickname=").Append(Escape(item.Message.Nickname))
                    .Append("; score=").Append(item.Score.ToString("0.00"))
                    .Append("; text=").Append(Escape(Trim(item.Message.Content, 280)));
                if (!string.IsNullOrWhiteSpace(item.Message.QuotedText))
                    builder.Append("; quoted=").Append(Escape(Trim(item.Message.QuotedText, 180)));
                if (item.Message.ReplyToUserId is > 0)
                    builder.Append("; reply_to_qq=").Append(item.Message.ReplyToUserId.Value);
                if (item.Message.MentionedUserIds.Count > 0)
                    builder.Append("; mentions=").Append(string.Join(",", item.Message.MentionedUserIds.Take(8)));
                builder.AppendLine();
            }
        }

        if (counts.Count > 0)
        {
            builder.AppendLine("phrase_counts:");
            foreach (var item in counts)
            {
                builder.Append("  - term=").Append(Escape(item.Term))
                    .Append("; total=").Append(item.Total)
                    .Append("; speakers=").Append(Escape(string.Join(", ", item.Speakers.Take(current.MaxRankItems))))
                    .AppendLine();
            }
        }

        if (rankings.Count > 0)
        {
            builder.AppendLine("speaker_activity_ranking:");
            foreach (var item in rankings)
            {
                builder.Append("  - qq=").Append(item.UserId)
                    .Append("; nickname=").Append(Escape(item.Nickname))
                    .Append("; messages=").Append(item.Count)
                    .AppendLine();
            }
        }

        builder.AppendLine("</group_chat_investigation>");
        return builder.ToString();
    }

    private static bool ShouldInvestigate(
        string probe,
        GroupActivityMessage? currentMessage,
        ConversationFocusDecision focus,
        GroupChatInvestigatorOptions options)
    {
        if (ContainsAny(probe, options.AllMarkers()))
            return true;

        if (currentMessage is not null &&
            (!string.IsNullOrWhiteSpace(currentMessage.QuotedText) ||
             currentMessage.ReplyToMessageId.HasValue ||
             currentMessage.MentionedUserIds.Count > 0) &&
            (focus.ReplyMode is FocusReplyMode.Answer or FocusReplyMode.Clarify ||
             ContainsAny(probe, options.QuoteFocusMarkers)))
        {
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> ExtractTerms(
        string probe,
        string? quotedText,
        GroupChatInvestigatorOptions options)
    {
        var terms = new List<string>();
        AddTerm(terms, Normalize(quotedText), options);
        var cleaned = options.AllMarkers()
            .Concat(options.StopWords)
            .Where(NotBlank)
            .Aggregate(probe, (text, marker) =>
                text.Replace(marker.Trim(), " ", StringComparison.OrdinalIgnoreCase));

        foreach (Match match in TokenRegex.Matches(cleaned))
        {
            AddTerm(terms, match.Value, options);
            if (terms.Count >= options.MaxExtractedTerms)
                break;
        }

        return terms
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(options.MaxExtractedTerms, 1, 20))
            .ToArray();
    }

    private static void AddTerm(
        ICollection<string> terms,
        string? value,
        GroupChatInvestigatorOptions options)
    {
        var normalized = TrimBoundaryNoise(Normalize(value));
        if (normalized.Length < options.MinimumTermLength ||
            normalized.Length > options.MaxTermLength ||
            options.StopWords.Any(stop => normalized.Equals(stop.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        terms.Add(normalized);
    }

    private static IReadOnlyList<ScoredMessage> SelectEvidence(
        IReadOnlyList<GroupActivityMessage> recent,
        GroupActivityMessage? currentMessage,
        IReadOnlyList<string> terms,
        GroupChatInvestigatorOptions options)
    {
        var result = new List<ScoredMessage>();
        foreach (var message in recent)
        {
            if (currentMessage is not null &&
                message.MessageId > 0 &&
                message.MessageId == currentMessage.MessageId)
            {
                continue;
            }

            var text = Normalize($"{message.Content} {message.QuotedText}");
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var score = 0.0;
            if (currentMessage?.ReplyToMessageId is > 0 &&
                message.MessageId == currentMessage.ReplyToMessageId.Value)
            {
                score += 9.0;
            }

            if (currentMessage?.ReplyToUserId is > 0 &&
                message.UserId == currentMessage.ReplyToUserId.Value)
            {
                score += 3.0;
            }

            if (!string.IsNullOrWhiteSpace(currentMessage?.QuotedText) &&
                text.Contains(Normalize(currentMessage.QuotedText), StringComparison.OrdinalIgnoreCase))
            {
                score += 8.0;
            }

            foreach (var term in terms)
            {
                if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score += 2.0 + Math.Min(2.0, term.Length / 12.0);
            }

            if (currentMessage?.MentionedUserIds.Contains(message.UserId) == true)
                score += 2.0;

            if (score > 0)
                result.Add(new ScoredMessage(message, score));
        }

        return result
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Message.Time)
            .Take(Math.Clamp(options.MaxEvidenceMessages, 1, 30))
            .OrderBy(item => item.Message.Time)
            .ToArray();
    }

    private static IReadOnlyList<PhraseCount> BuildTermCounts(
        IReadOnlyList<GroupActivityMessage> recent,
        IReadOnlyList<string> terms,
        GroupChatInvestigatorOptions options)
    {
        if (terms.Count == 0)
            return Array.Empty<PhraseCount>();

        return terms
            .Select(term =>
            {
                var matches = recent
                    .Where(message =>
                        Normalize($"{message.Content} {message.QuotedText}")
                            .Contains(term, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                return new PhraseCount(
                    term,
                    matches.Length,
                    matches
                        .GroupBy(message => new { message.UserId, message.Nickname })
                        .OrderByDescending(group => group.Count())
                        .Select(group => $"{group.Key.Nickname}({group.Key.UserId}) count={group.Count()}")
                        .ToArray());
            })
            .Where(item => item.Total > 0)
            .OrderByDescending(item => item.Total)
            .Take(Math.Clamp(options.MaxExtractedTerms, 1, 20))
            .ToArray();
    }

    private static IReadOnlyList<SpeakerRank> BuildSpeakerRanking(
        IReadOnlyList<GroupActivityMessage> recent,
        GroupChatInvestigatorOptions options)
    {
        if (recent.Count == 0)
            return Array.Empty<SpeakerRank>();

        return recent
            .Where(message => !message.IsBot && !string.IsNullOrWhiteSpace(message.Content))
            .GroupBy(message => new { message.UserId, message.Nickname })
            .Select(group => new SpeakerRank(group.Key.UserId, group.Key.Nickname, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Nickname, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(options.MaxRankItems, 1, 20))
            .ToArray();
    }

    private static bool ContainsAny(string text, IEnumerable<string> markers) =>
        markers.Where(NotBlank)
            .Any(marker => text.Contains(marker.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var previousWhitespace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace)
                    builder.Append(' ');
                previousWhitespace = true;
                continue;
            }

            builder.Append(ch);
            previousWhitespace = false;
        }

        return builder.ToString();
    }

    private static string Trim(string? value, int maximum)
    {
        var normalized = Normalize(value);
        return normalized.Length <= maximum
            ? normalized
            : normalized[..Math.Max(0, maximum - 1)] + "…";
    }

    private static string TrimBoundaryNoise(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var start = 0;
        var end = value.Length - 1;
        while (start <= end && IsBoundaryNoise(value[start]))
            start++;
        while (end >= start && IsBoundaryNoise(value[end]))
            end--;

        return start > end
            ? string.Empty
            : value[start..(end + 1)];
    }

    private static bool IsBoundaryNoise(char value) =>
        char.IsWhiteSpace(value) ||
        char.IsPunctuation(value) ||
        char.IsSymbol(value);

    private static string Escape(string? value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;

    private sealed record ScoredMessage(GroupActivityMessage Message, double Score);

    private sealed record PhraseCount(string Term, int Total, IReadOnlyList<string> Speakers);

    private sealed record SpeakerRank(long UserId, string Nickname, int Count);
}
