using Microsoft.Extensions.DependencyInjection;

namespace Hime.Messaging;

public interface IDomainEvent;

public interface IEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}

public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken = default)
        where TEvent : IDomainEvent;
}

/// <summary>
/// In-process fan-out for business events. Handlers are isolated from message routing,
/// while remaining in the same process and sharing the same DI container.
/// </summary>
public sealed class InProcessEventBus(IServiceProvider services) : IEventBus
{
    public async Task PublishAsync<TEvent>(
        TEvent domainEvent,
        CancellationToken cancellationToken = default)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        foreach (var handler in services.GetServices<IEventHandler<TEvent>>())
            await handler.HandleAsync(domainEvent, cancellationToken);
    }
}

public sealed record MessageAcceptedEvent(
    string CorrelationId,
    string Platform,
    string ScopeKey,
    long SenderId,
    long? GroupId) : IDomainEvent;

public sealed record MessageCompletedEvent(
    string CorrelationId,
    string ScopeKey,
    string Outcome,
    double ElapsedMilliseconds) : IDomainEvent;
