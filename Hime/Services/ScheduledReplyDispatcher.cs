using System.Threading.Channels;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Executes small delayed send operations outside the incoming-message pipeline.
/// It preserves a natural pause without holding a conversation worker or model slot.
/// </summary>
public sealed class ScheduledReplyDispatcher : BackgroundService
{
    private readonly Channel<ScheduledReplyJob> _jobs;
    private readonly ConcurrentDictionary<long, int> _pendingByConversation = new();
    private readonly int _workerCount;
    private readonly ILogger<ScheduledReplyDispatcher> _logger;
    private readonly RuntimeDiagnostics _diagnostics;
    private long _pendingTotal;

    public ScheduledReplyDispatcher(
        IOptions<ReplySchedulingOptions> options,
        ILogger<ScheduledReplyDispatcher> logger,
        RuntimeDiagnostics diagnostics)
    {
        var capacity = Math.Clamp(options.Value.Capacity, 8, 512);
        _workerCount = Math.Clamp(options.Value.WorkerCount, 1, 16);
        _jobs = Channel.CreateBounded<ScheduledReplyJob>(new BoundedChannelOptions(capacity)
        {
            SingleReader = _workerCount == 1,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _logger = logger;
        _diagnostics = diagnostics;
    }

    public bool TrySchedule(
        TimeSpan delay,
        long conversationKey,
        string description,
        Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var accepted = _jobs.Writer.TryWrite(new ScheduledReplyJob(
            delay < TimeSpan.Zero ? TimeSpan.Zero : delay,
            conversationKey,
            description,
            action,
            _diagnostics.CurrentCorrelationId));
        if (!accepted)
            _logger.LogWarning("Delayed reply queue is full; {Description} will be sent immediately", description);
        else
        {
            _pendingByConversation.AddOrUpdate(conversationKey, 1, (_, count) => count + 1);
            Interlocked.Increment(ref _pendingTotal);
        }
        return accepted;
    }

    public bool IsPending(long conversationKey) =>
        _pendingByConversation.TryGetValue(conversationKey, out var count) && count > 0;

    public long PendingCount => Math.Max(0, Interlocked.Read(ref _pendingTotal));

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _workerCount)
            .Select(index => RunWorkerAsync(index, stoppingToken))
            .ToArray();
        return Task.WhenAll(workers);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _jobs.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private async Task RunWorkerAsync(int worker, CancellationToken cancellationToken)
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync(cancellationToken))
        {
            using var correlation = _diagnostics.PushCorrelation(job.CorrelationId ?? $"scheduled-{job.ConversationKey}");
            using var operation = _diagnostics.Begin("reply.scheduled");
            try
            {
                if (job.Delay > TimeSpan.Zero)
                    await Task.Delay(job.Delay, cancellationToken);
                await job.Action(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                operation.Fail();
                _logger.LogWarning(
                    ex,
                    "Scheduled reply failed (Worker={Worker}, Work={Work})",
                    worker,
                    job.Description);
            }
            finally
            {
                CompleteConversationWork(job.ConversationKey);
            }
        }
    }

    private void CompleteConversationWork(long conversationKey)
    {
        Interlocked.Decrement(ref _pendingTotal);
        while (_pendingByConversation.TryGetValue(conversationKey, out var current))
        {
            if (current <= 1)
            {
                if (_pendingByConversation.TryRemove(
                        new KeyValuePair<long, int>(conversationKey, current)))
                    return;
            }
            else if (_pendingByConversation.TryUpdate(conversationKey, current - 1, current))
            {
                return;
            }
        }
    }

    private sealed record ScheduledReplyJob(
        TimeSpan Delay,
        long ConversationKey,
        string Description,
        Func<CancellationToken, Task> Action,
        string? CorrelationId);
}

public sealed class ReplySchedulingOptions
{
    public int Capacity { get; set; } = 64;

    public int WorkerCount { get; set; } = 4;
}
