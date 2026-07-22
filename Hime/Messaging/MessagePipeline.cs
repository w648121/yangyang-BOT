using System.Diagnostics;
using Hime.Services;
using Microsoft.Extensions.Logging;

namespace Hime.Messaging;

public delegate Task MessageHandlerDelegate(MessageContext context, CancellationToken cancellationToken);

public interface IMessageMiddleware
{
    int Order { get; }

    Task InvokeAsync(
        MessageContext context,
        MessageHandlerDelegate next,
        CancellationToken cancellationToken);
}

public sealed class MessageMiddlewarePipeline(IEnumerable<IMessageMiddleware> middleware)
{
    private readonly IMessageMiddleware[] _middleware = middleware.OrderBy(item => item.Order).ToArray();

    public Task InvokeAsync(
        MessageContext context,
        MessageHandlerDelegate terminal,
        CancellationToken cancellationToken = default)
    {
        MessageHandlerDelegate current = terminal;
        for (var index = _middleware.Length - 1; index >= 0; index--)
        {
            var component = _middleware[index];
            var next = current;
            current = (ctx, ct) => component.InvokeAsync(ctx, next, ct);
        }

        return current(context, cancellationToken);
    }
}

public sealed class MessageCoordinator(
    MessageMiddlewarePipeline pipeline,
    IEventBus eventBus,
    RuntimeDiagnostics diagnostics)
{
    public async Task DispatchAsync(
        IncomingMessage message,
        MessageHandlerDelegate terminal,
        CancellationToken cancellationToken = default)
    {
        var context = new MessageContext(message);
        var started = Stopwatch.GetTimestamp();
        await eventBus.PublishAsync(
            new MessageAcceptedEvent(
                message.CorrelationId,
                message.Platform,
                message.ScopeKey,
                message.SenderId,
                message.GroupId),
            cancellationToken);

        using var operation = diagnostics.Begin("message.pipeline");
        try
        {
            await pipeline.InvokeAsync(context, terminal, cancellationToken);
            diagnostics.Increment(context.Handled
                ? "messages.pipeline.handled"
                : "messages.pipeline.continued");
        }
        catch
        {
            operation.Fail();
            throw;
        }
        finally
        {
            await eventBus.PublishAsync(
                new MessageCompletedEvent(
                    message.CorrelationId,
                    message.ScopeKey,
                    context.Outcome,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds),
                CancellationToken.None);
        }
    }
}

public sealed class BusinessLoggingMiddleware(
    ILogger<BusinessLoggingMiddleware> logger) : IMessageMiddleware
{
    public int Order => 100;

    public async Task InvokeAsync(
        MessageContext context,
        MessageHandlerDelegate next,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context, cancellationToken);
        }
        finally
        {
            logger.LogInformation(
                "Business message completed (CorrelationId={CorrelationId}, Scope={Scope}, Outcome={Outcome}, ElapsedMs={ElapsedMs:F1})",
                context.Message.CorrelationId,
                context.Message.ScopeKey,
                context.Outcome,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}

public sealed class MessageDiagnosticsEventHandler(RuntimeDiagnostics diagnostics) :
    IEventHandler<MessageAcceptedEvent>,
    IEventHandler<MessageCompletedEvent>
{
    public Task HandleAsync(MessageAcceptedEvent domainEvent, CancellationToken cancellationToken)
    {
        diagnostics.Event("business-message", domainEvent.ScopeKey);
        return Task.CompletedTask;
    }

    public Task HandleAsync(MessageCompletedEvent domainEvent, CancellationToken cancellationToken)
    {
        diagnostics.Event("business-result", $"{domainEvent.ScopeKey}:{domainEvent.Outcome}");
        return Task.CompletedTask;
    }
}
