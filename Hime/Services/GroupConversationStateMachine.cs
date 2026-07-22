using Hime.Data.Models;

namespace Hime.Services;

/// <summary>
/// Converts the bounded group activity window into a small, explainable state.
/// It never generates text or sends messages; it only gives other services a
/// reliable answer to "is this a live conversation or a quiet room?".
/// </summary>
public sealed class GroupConversationStateMachine
{
    public GroupConversationSnapshot Analyze(
        GroupActivityRecord group,
        ProactiveAgentOptions options,
        DateTime utcNow)
    {
        var activeMinutes = Math.Clamp(options.ActiveConversationWindowMinutes, 1, 60);
        var coolingMinutes = Math.Max(activeMinutes + 1, Math.Clamp(options.CoolingConversationWindowMinutes, 2, 240));
        var recentMessages = (group.RecentMessages ?? [])
            .Where(message => !message.IsBot)
            .OrderBy(message => message.Time)
            .TakeLast(Math.Clamp(options.ContextMessageLimit, 4, 100))
            .ToList();
        var lastIncoming = group.LastIncomingAt == default
            ? recentMessages.LastOrDefault()?.Time
            : group.LastIncomingAt;
        var age = lastIncoming is { } time ? utcNow - time : TimeSpan.MaxValue;
        var recent = recentMessages
            .Where(message => utcNow - message.Time <= TimeSpan.FromMinutes(activeMinutes))
            .ToList();
        var participantIds = recent
            .Select(message => message.UserId)
            .Where(userId => userId != 0)
            .Distinct()
            .ToList();
        var speakerThreshold = Math.Clamp(options.LivelySpeakerThreshold, 2, 10);
        var phase = age > TimeSpan.FromMinutes(coolingMinutes)
            ? GroupConversationPhase.Quiet
            : age > TimeSpan.FromMinutes(activeMinutes)
                ? GroupConversationPhase.Cooling
                : participantIds.Count >= speakerThreshold || recent.Count >= speakerThreshold + 1
                    ? GroupConversationPhase.Lively
                    : GroupConversationPhase.Focused;
        var lastMessage = recentMessages.LastOrDefault();
        var topicHint = string.Join(" / ", recentMessages
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .TakeLast(2)
            .Select(message => Trim(message.Content, 96)));

        return new GroupConversationSnapshot(
            phase,
            lastIncoming,
            lastMessage?.UserId,
            lastMessage?.Nickname ?? string.Empty,
            participantIds,
            lastMessage?.ImagePaths.Count > 0,
            topicHint);
    }

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";
}

public enum GroupConversationPhase
{
    Quiet,
    Cooling,
    Focused,
    Lively
}

public sealed record GroupConversationSnapshot(
    GroupConversationPhase Phase,
    DateTime? LastIncomingAt,
    long? LastSpeakerId,
    string LastSpeakerName,
    IReadOnlyList<long> ActiveMemberIds,
    bool LastMessageHasVisual,
    string TopicHint)
{
    public string ToPromptHint()
    {
        var ageText = LastIncomingAt is { } time
            ? $"last human message {Math.Max(0, (int)(DateTime.UtcNow - time).TotalMinutes)} minutes ago"
            : "no recorded human message";
        var topic = string.IsNullOrWhiteSpace(TopicHint) ? "no reliable topic hint" : TopicHint;
        return $"Conversation state: {Phase}; {ageText}; active members: {ActiveMemberIds.Count}; recent topic hint: {topic}.";
    }
}
