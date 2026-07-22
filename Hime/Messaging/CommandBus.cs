using Microsoft.Extensions.DependencyInjection;

namespace Hime.Messaging;

public interface ICommand;

public interface ICommandHandler<in TCommand> where TCommand : ICommand
{
    Task HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface ICommandBus
{
    Task SendAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand;
}

/// <summary>
/// In-process command bus. Dispatch is resolved through DI without network or serialization overhead.
/// </summary>
public sealed class InProcessCommandBus(IServiceProvider services) : ICommandBus
{
    public Task SendAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(command);
        return services.GetRequiredService<ICommandHandler<TCommand>>()
            .HandleAsync(command, cancellationToken);
    }
}
