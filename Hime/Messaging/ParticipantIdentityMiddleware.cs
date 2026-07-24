using Hime.Services;
using Microsoft.Extensions.Logging;

namespace Hime.Messaging;

public static class MessageContextKeys
{
    public const string ParticipantKind = "participant.kind";
}

/// <summary>
/// Classifies every sender before commands or conversational services run.
/// External bots are observable in transport logs but cannot enter Hime's
/// relationship, natural-reply, image, or model-context pipelines.
/// </summary>
public sealed class ParticipantIdentityMiddleware(
    ParticipantIdentityService participants,
    ILogger<ParticipantIdentityMiddleware> logger) : IMessageMiddleware
{
    public int Order => 125;

    public async Task InvokeAsync(
        MessageContext context,
        MessageHandlerDelegate next,
        CancellationToken cancellationToken)
    {
        var message = context.Message;
        var kind = participants.GetKind(message.Platform, message.SenderId, message.SelfId);
        context.Items[MessageContextKeys.ParticipantKind] = kind;

        if (kind is ParticipantKind.ExternalBot or ParticipantKind.SelfBot)
        {
            context.MarkHandled(kind == ParticipantKind.ExternalBot
                ? "external-bot-observed"
                : "self-message-observed");
            logger.LogInformation(
                "Skipped non-human participant before business routing (Platform={Platform}, SenderId={SenderId}, Kind={Kind}, CorrelationId={CorrelationId})",
                message.Platform,
                message.SenderId,
                kind,
                message.CorrelationId);
            return;
        }

        await next(context, cancellationToken);
    }
}
