using System.Security.Cryptography;
using System.Text;
using Hime.Data.Models;
using Hime.Messaging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Runtime-configurable boundaries for group focus and topic continuity.
/// Strong protocol relations are always authoritative; thresholds only affect
/// ambiguous, mention-free group conversation.
/// </summary>
public sealed class ConversationFocusOptions
{
    public bool Enabled { get; set; } = true;

    public string BotDisplayName { get; set; } = string.Empty;

    public List<string> BotAliases { get; set; } = [];

    public int ContextMessageLimit { get; set; } = 24;

    public int TopicContinuityMinutes { get; set; } = 12;

    public int DirectContinuationMinutes { get; set; } = 5;

    public int ShortTurnMaximumCharacters { get; set; } = 24;

    public int MaximumMessageCharacters { get; set; } = 500;

    public int MaximumParticipants { get; set; } = 16;

    public double TopicSimilarityThreshold { get; set; } = 0.24;

    public double BotContinuationThreshold { get; set; } = 0.56;

    public double ReactParticipationThreshold { get; set; } = 0.42;

    public bool SemanticFallbackEnabled { get; set; } = true;

    public double SemanticCandidateMinimum { get; set; } = 0.25;

    public double SemanticConfidenceThreshold { get; set; } = 0.72;

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(BotDisplayName) &&
        ContextMessageLimit is >= 4 and <= 100 &&
        TopicContinuityMinutes is >= 1 and <= 120 &&
        DirectContinuationMinutes is >= 1 and <= 60 &&
        ShortTurnMaximumCharacters is >= 1 and <= 200 &&
        MaximumMessageCharacters is >= 20 and <= 4000 &&
        MaximumParticipants is >= 2 and <= 100 &&
        IsProbability(TopicSimilarityThreshold) &&
        IsProbability(BotContinuationThreshold) &&
        IsProbability(ReactParticipationThreshold) &&
        IsProbability(SemanticCandidateMinimum) &&
        IsProbability(SemanticConfidenceThreshold);

    private static bool IsProbability(double value) => value is >= 0 and <= 1;

}

public sealed record GroupTopicResolution(
    string TopicId,
    IReadOnlyList<long> Participants,
    double ContinuationScore,
    double BotParticipationScore,
    bool HasRecentBotMessage,
    bool LatestMessageWasBot,
    long? StructuralTargetUserId,
    string Reason);

/// <summary>
/// Builds a bounded topic graph from reply edges, mentions, recent participants,
/// time distance and lexical continuity. It has no model dependency and does not
/// persist a second copy of chat data: topic metadata is stored with group activity.
/// </summary>
public sealed class ConversationTopicGraph(IOptionsMonitor<ConversationFocusOptions> options)
{
    public GroupTopicResolution Resolve(
        IncomingMessage message,
        IReadOnlyList<GroupActivityMessage> recentMessages,
        DateTime? nowUtc = null)
    {
        var current = options.CurrentValue;
        var now = nowUtc ?? DateTime.UtcNow;
        var continuityWindow = TimeSpan.FromMinutes(
            Math.Clamp(current.TopicContinuityMinutes, 1, 120));
        var directWindow = TimeSpan.FromMinutes(
            Math.Clamp(current.DirectContinuationMinutes, 1, 60));
        var bounded = (recentMessages ?? [])
            .Where(item => item.Time <= now && now - item.Time <= continuityWindow)
            .OrderBy(item => item.Time)
            .TakeLast(Math.Clamp(current.ContextMessageLimit, 4, 100))
            .ToArray();
        var latest = bounded.LastOrDefault();
        var replied = message.ReplyToMessageId.HasValue
            ? bounded.LastOrDefault(item => item.MessageId == message.ReplyToMessageId.Value)
            : null;
        replied ??= message.ReplyToUserId.HasValue
            ? bounded.LastOrDefault(item =>
                !item.IsBot && item.UserId == message.ReplyToUserId.Value)
            : null;

        var structuralTarget = message.ReplyToUserId;
        if (!structuralTarget.HasValue)
        {
            structuralTarget = message.MentionedUserIds
                .FirstOrDefault(userId => userId > 0 && userId != message.SelfId);
            if (structuralTarget == 0)
                structuralTarget = null;
        }

        var lexicalSimilarity = latest is null
            ? 0
            : Similarity(message.Text, latest.Content);
        var latestGap = latest is null ? TimeSpan.MaxValue : now - latest.Time;
        var senderWasParticipant = latest?.ConversationParticipants?.Contains(message.SenderId) == true ||
                                   bounded.Any(item =>
                                       !string.IsNullOrWhiteSpace(latest?.TopicId) &&
                                       string.Equals(item.TopicId, latest.TopicId, StringComparison.Ordinal) &&
                                       item.UserId == message.SenderId);
        var shortContinuation = message.Text.Trim().Length <=
                                Math.Clamp(current.ShortTurnMaximumCharacters, 1, 200);

        string topicId;
        string topicReason;
        if (replied is not null && !string.IsNullOrWhiteSpace(replied.TopicId))
        {
            topicId = replied.TopicId;
            topicReason = "reply-edge";
        }
        else if (latest is not null &&
                 !string.IsNullOrWhiteSpace(latest.TopicId) &&
                 latestGap <= continuityWindow &&
                 (lexicalSimilarity >= current.TopicSimilarityThreshold ||
                  (latest.IsBot && latestGap <= directWindow) ||
                  (senderWasParticipant && shortContinuation)))
        {
            topicId = latest.TopicId;
            topicReason = latest.IsBot && latestGap <= directWindow
                ? "recent-bot-continuation"
                : senderWasParticipant && shortContinuation
                    ? "participant-continuation"
                    : "lexical-continuation";
        }
        else
        {
            topicId = BuildTopicId(message);
            topicReason = "new-topic";
        }

        var sameTopic = bounded
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.TopicId) &&
                string.Equals(item.TopicId, topicId, StringComparison.Ordinal))
            .ToArray();
        var participants = sameTopic
            .SelectMany(item => item.ConversationParticipants ?? [])
            .Concat(sameTopic.Where(item => !item.IsBot).Select(item => item.UserId))
            .Append(message.SenderId)
            .Concat(message.MentionedUserIds)
            .Append(message.ReplyToUserId ?? 0)
            .Where(userId => userId > 0 && userId != message.SelfId)
            .Distinct()
            .Take(Math.Clamp(current.MaximumParticipants, 2, 100))
            .ToArray();

        var directReference = replied is not null ? 1d : 0d;
        var latestBotAddressedSender =
            latest?.IsBot == true &&
            latest.ReplyToUserId == message.SenderId;
        var timeScore = latest is null
            ? 0
            : 1 - Math.Clamp(latestGap.TotalSeconds / Math.Max(1, continuityWindow.TotalSeconds), 0, 1);
        var continuation = Math.Clamp(
            directReference * 0.65 +
            lexicalSimilarity * 0.35 +
            timeScore * 0.20 +
            (latestBotAddressedSender ? 0.35 : latest?.IsBot == true ? 0.10 : 0) +
            (senderWasParticipant ? 0.10 : 0),
            0,
            1);

        var topicForParticipation = sameTopic.Length > 0
            ? sameTopic
            : bounded.TakeLast(6).ToArray();
        var botMessages = topicForParticipation.Count(item => item.IsBot);
        var botShare = topicForParticipation.Length == 0
            ? 0
            : (double)botMessages / topicForParticipation.Length;
        var mostRecentBot = topicForParticipation.LastOrDefault(item => item.IsBot);
        var botRecency = mostRecentBot is null
            ? 0
            : 1 - Math.Clamp(
                (now - mostRecentBot.Time).TotalSeconds /
                Math.Max(1, continuityWindow.TotalSeconds),
                0,
                1);
        var botParticipation = Math.Clamp(
            botShare * 0.50 +
            botRecency * 0.30 +
            (latest?.IsBot == true ? 0.20 : 0),
            0,
            1);

        return new GroupTopicResolution(
            topicId,
            participants,
            continuation,
            botParticipation,
            mostRecentBot is not null,
            latest?.IsBot == true,
            structuralTarget,
            $"{topicReason}; lexical={lexicalSimilarity:F2}; time={timeScore:F2}");
    }

    private static string BuildTopicId(IncomingMessage message)
    {
        if (message.MessageId > 0)
            return $"g{message.GroupId ?? 0}:m{message.MessageId}";

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(message.CorrelationId));
        return $"g{message.GroupId ?? 0}:c{Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant()}";
    }

    internal static double Similarity(string? left, string? right)
    {
        var first = NGrams(left);
        var second = NGrams(right);
        if (first.Count == 0 || second.Count == 0)
            return 0;
        var intersection = first.Count(second.Contains);
        var union = first.Count + second.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> NGrams(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (normalized.Length == 1)
        {
            result.Add(normalized);
            return result;
        }

        for (var index = 0; index + 1 < normalized.Length; index++)
            result.Add(normalized.Substring(index, 2));
        return result;
    }
}
