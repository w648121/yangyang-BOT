using Hime.Data;
using Hime.Data.Models;
using Hime.Data.Services;
using Hime.Messaging;
using Microsoft.Extensions.Logging;

namespace Hime.Services;

/// <summary>
/// Single persistence boundary for replies that were successfully delivered.
/// Generators may prepare content, but only this component is allowed to publish
/// that content into chat history, group activity and relationship state.
/// </summary>
public interface IConversationTurnRecorder
{
    void RecordDelivered(TurnContext turn, DeliveredTurn delivered);
}

public sealed class ConversationTurnRecorder(
    HimeDbContext database,
    IChatService chat,
    IGroupActivityService groupActivities,
    IRelationshipTrajectoryService relationshipTrajectory,
    IPersonaStateService personaStates,
    ILogger<ConversationTurnRecorder> logger) : IConversationTurnRecorder
{
    public void RecordDelivered(TurnContext turn, DeliveredTurn delivered)
    {
        var text = (delivered.Text ?? string.Empty).Trim();
        var imagePaths = delivered.ImagePaths ?? Array.Empty<string>();
        if (text.Length == 0 && imagePaths.Count == 0)
        {
            logger.LogDebug("Skipped empty delivered turn {TurnId}", turn.TurnId);
            return;
        }

        TryProject("turn-ledger", turn, () =>
        {
            var turns = database.Database.GetCollection<ConversationTurnRecord>("conversation_turns");
            turns.Upsert(new ConversationTurnRecord
            {
                TurnId = turn.TurnId,
                Platform = turn.Platform,
                AccountId = turn.AccountId,
                CorrelationId = turn.CorrelationId,
                ScopeKey = turn.ScopeKey,
                SourceMessageId = turn.SourceMessageId,
                UserId = turn.UserId,
                Nickname = turn.Nickname,
                GroupId = turn.GroupId,
                TopicId = turn.TopicId,
                ConversationParticipants = turn.ConversationParticipants.ToList(),
                UserText = turn.UserText,
                UserImagePaths = turn.UserImagePaths.ToList(),
                Trigger = turn.Trigger.ToString(),
                AssistantText = text,
                AssistantImagePaths = imagePaths.ToList(),
                Emotion = delivered.Emotion,
                Source = delivered.Source,
                AssistantMessageId = delivered.PlatformMessageId,
                SocialIntentId = Trim(delivered.SocialIntentId, 80),
                DialogueAct = Trim(delivered.DialogueAct, 40),
                CandidateSummary = Trim(delivered.CandidateSummary, 240),
                StartedAtUtc = turn.StartedAt.UtcDateTime,
                DeliveredAtUtc = DateTime.UtcNow
            });
        });

        TryProject("chat-history", turn, () =>
        {
            if (turn.IsProactive)
            {
                if (turn.GroupId is not { } proactiveGroupId)
                    throw new InvalidOperationException("A proactive turn must target a group.");

                chat.AppendAssistantMessage(
                    proactiveGroupId,
                    text,
                    imagePaths,
                    delivered.Emotion,
                    turn.TurnId,
                    delivered.Source,
                    turn.AccountId);
            }
            else if (turn.UserId > 0)
            {
                chat.AppendTurn(
                    turn.UserId,
                    turn.Nickname,
                    turn.UserText,
                    turn.UserImagePaths,
                    text,
                    imagePaths,
                    delivered.Emotion,
                    turn.GroupId,
                    turn.TurnId,
                    delivered.Source,
                    turn.AccountId,
                    turn.SourceMessageId);
            }
        });

        if (delivered.RecordGroupActivity && turn.GroupId is { } groupId)
        {
            TryProject(
                "group-activity",
                turn,
                () => groupActivities.RecordBotReply(
                    groupId,
                    text.Length > 0 ? text : BuildMediaSummary(imagePaths.Count),
                    replyToMessageId: long.TryParse(turn.SourceMessageId, out var replyMessageId)
                        ? replyMessageId
                        : null,
                    replyToUserId: turn.UserId > 0 ? turn.UserId : null,
                    topicId: turn.TopicId,
                    conversationParticipants: turn.ConversationParticipants,
                    messageId: delivered.PlatformMessageId ?? 0));
        }

        var sourceMessageId = long.TryParse(turn.SourceMessageId, out var parsedMessageId)
            ? parsedMessageId
            : 0;
        TryProject(
            "relationship",
            turn,
            () => relationshipTrajectory.RecordAssistantReply(
                sourceMessageId,
                turn.UserId,
                turn.GroupId,
                text,
                delivered.Emotion,
                delivered.Source));

        if (turn.UserId > 0)
        {
            TryProject(
                "persona-state",
                turn,
                () => personaStates.RecordAssistantReply(
                    turn.UserId,
                    turn.GroupId,
                    delivered.Emotion ?? "neutral"));
        }

        logger.LogDebug(
            "Recorded delivered turn {TurnId} ({Trigger}, UserId={UserId}, GroupId={GroupId}, Source={Source})",
            turn.TurnId,
            turn.Trigger,
            turn.UserId,
            turn.GroupId,
            delivered.Source);
    }

    private static string BuildMediaSummary(int imageCount) =>
        imageCount > 0 ? $"[已发送图片 ×{imageCount}]" : "[已发送媒体]";

    private static string Trim(string? value, int maximum)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }

    private void TryProject(string projection, TurnContext turn, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to persist delivered turn projection {Projection} (TurnId={TurnId})",
                projection,
                turn.TurnId);
        }
    }
}
