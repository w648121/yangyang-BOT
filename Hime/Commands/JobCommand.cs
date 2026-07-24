using Hime.Jobs;
using Hime.Messaging;

namespace Hime.Commands;

public sealed class JobCommand(JobRequestService requests)
{
    public Task ReminderAsync(
        IncomingMessage message,
        string rawText,
        CancellationToken cancellationToken) =>
        requests.HandleReminderCommandAsync(message, rawText, cancellationToken);

    public Task TasksAsync(
        IncomingMessage message,
        string rawText,
        CancellationToken cancellationToken) =>
        requests.HandleTaskCommandAsync(message, rawText, cancellationToken);
}
