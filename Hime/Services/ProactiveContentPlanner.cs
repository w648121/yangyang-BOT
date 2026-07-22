using Hime.Data.Models;

namespace Hime.Services;

/// <summary>
/// Selects a socially appropriate proactive format before the LLM writes it.
/// This makes lively groups receive a brief continuation while quiet groups can
/// receive a self-contained diary-style post instead of a random interruption.
/// </summary>
public sealed class ProactiveContentPlanner
{
    private readonly GroupConversationStateMachine _stateMachine;

    public ProactiveContentPlanner(GroupConversationStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
    }

    public ProactiveContentPlan Plan(
        GroupActivityRecord group,
        ProactiveAgentOptions options,
        bool canSendArticle,
        DateTime utcNow)
    {
        var state = _stateMachine.Analyze(group, options, utcNow);
        if (state.Phase is GroupConversationPhase.Lively or GroupConversationPhase.Focused)
        {
            if (state.LastMessageHasVisual && options.AllowSticker)
                return new(ProactiveAction.Sticker, ProactiveIntent.VisualReaction, state);
            return new(ProactiveAction.Text, ProactiveIntent.ConversationBridge, state);
        }

        if (state.Phase == GroupConversationPhase.Cooling)
            return new(ProactiveAction.Text, ProactiveIntent.LightObservation, state);

        if (options.AllowArticles && canSendArticle)
            return new(ProactiveAction.Article, ProactiveIntent.MiniDiary, state);

        if (options.AllowVoice && Random.Shared.NextDouble() < 0.25d)
            return new(ProactiveAction.Voice, ProactiveIntent.VoiceAside, state);

        if (options.AllowSticker && Random.Shared.NextDouble() < 0.20d)
            return new(ProactiveAction.Sticker, ProactiveIntent.MoodSticker, state);

        return new(ProactiveAction.Text, ProactiveIntent.GentleCheckIn, state);
    }
}

public enum ProactiveIntent
{
    ConversationBridge,
    VisualReaction,
    LightObservation,
    MiniDiary,
    VoiceAside,
    MoodSticker,
    GentleCheckIn
}

public sealed record ProactiveContentPlan(
    ProactiveAction Action,
    ProactiveIntent Intent,
    GroupConversationSnapshot State);
