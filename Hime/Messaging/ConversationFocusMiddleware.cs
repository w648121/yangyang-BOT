using Hime.Services;
using Microsoft.Extensions.Logging;

namespace Hime.Messaging;

public static class ConversationFocusContextKeys
{
    public const string Decision = "conversation.focus";
}

/// <summary>
/// Resolves conversational ownership once, after participant and group gates but
/// before pending interactions, commands or AI features. Later handlers consume
/// the same decision and cannot independently reinterpret the message target.
/// </summary>
public sealed class ConversationFocusMiddleware(
    ConversationFocusResolver resolver,
    ILogger<ConversationFocusMiddleware> logger) : IMessageMiddleware
{
    public int Order => 175;

    public async Task InvokeAsync(
        MessageContext context,
        MessageHandlerDelegate next,
        CancellationToken cancellationToken)
    {
        var decision = await resolver.ResolveAsync(context.Message, cancellationToken);
        context.ReplaceMessage(decision.Enrich(context.Message));
        context.Items[ConversationFocusContextKeys.Decision] = decision;
        logger.LogDebug(
            "Conversation focus resolved (Scope={Scope}, Topic={Topic}, Target={Target}, Mode={Mode}, Confidence={Confidence:F2}, Reason={Reason})",
            context.Message.ScopeKey,
            decision.TopicId,
            decision.Target,
            decision.ReplyMode,
            decision.Confidence,
            decision.Reason);
        await next(context, cancellationToken);
    }
}
