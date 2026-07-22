using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Keeps messages from the same conversation in order while allowing unrelated
/// groups and private chats to make progress independently. The bounded queues
/// also prevent a burst of slow AI work from creating unlimited in-memory tasks.
/// </summary>
public sealed class ConversationMessageDispatcher : BackgroundService
{
    private readonly Channel<ConversationWorkItem>[] _partitions;
    private readonly ConcurrentDictionary<long, int> _pendingByConversation = new();
    private readonly ILogger<ConversationMessageDispatcher> _logger;
    private readonly RuntimeDiagnostics _diagnostics;
    private long _pendingTotal;

    public ConversationMessageDispatcher(
        IOptions<MessageDispatchOptions> options,
        ILogger<ConversationMessageDispatcher> logger,
        RuntimeDiagnostics diagnostics)
    {
        var partitionCount = Math.Clamp(options.Value.PartitionCount, 1, 32);
        var capacity = Math.Clamp(options.Value.CapacityPerPartition, 8, 512);
        _partitions = Enumerable.Range(0, partitionCount)
            .Select(_ => Channel.CreateBounded<ConversationWorkItem>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            }))
            .ToArray();
        _logger = logger;
        _diagnostics = diagnostics;
    }

    public async ValueTask EnqueueAsync(
        long conversationKey,
        string description,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var partition = GetPartition(conversationKey);
        var started = Stopwatch.GetTimestamp();
        _pendingByConversation.AddOrUpdate(conversationKey, 1, (_, count) => count + 1);
        Interlocked.Increment(ref _pendingTotal);
        _diagnostics.Increment("messages.queued");
        var waitOperation = _diagnostics.Begin("queue.message.wait");
        try
        {
            await _partitions[partition].Writer.WriteAsync(
                new ConversationWorkItem(conversationKey, description, action),
                cancellationToken);
        }
        catch
        {
            waitOperation.Fail();
            CompleteConversationWork(conversationKey);
            throw;
        }
        finally
        {
            waitOperation.Dispose();
        }
        var waited = Stopwatch.GetElapsedTime(started);
        if (waited >= TimeSpan.FromMilliseconds(250))
        {
            _logger.LogWarning(
                "Conversation queue applied backpressure for {ElapsedMs}ms (Key={Key}, Work={Work})",
                waited.TotalMilliseconds,
                conversationKey,
                description);
        }
    }

    public bool IsBusy(long conversationKey) =>
        _pendingByConversation.TryGetValue(conversationKey, out var count) && count > 0;

    public long PendingCount => Math.Max(0, Interlocked.Read(ref _pendingTotal));

    public int BusyConversationCount => _pendingByConversation.Count;

    public int PartitionCount => _partitions.Length;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = _partitions
            .Select((channel, index) => RunPartitionAsync(channel.Reader, index, stoppingToken))
            .ToArray();
        return Task.WhenAll(workers);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var partition in _partitions)
            partition.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private async Task RunPartitionAsync(
        ChannelReader<ConversationWorkItem> reader,
        int partition,
        CancellationToken cancellationToken)
    {
        await foreach (var item in reader.ReadAllAsync(cancellationToken))
        {
            using var operation = _diagnostics.Begin("message.process");
            try
            {
                await item.Action(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                operation.Fail();
                _logger.LogError(
                    ex,
                    "Conversation work failed (Partition={Partition}, Key={Key}, Work={Work})",
                    partition,
                    item.ConversationKey,
                    item.Description);
            }
            finally
            {
                CompleteConversationWork(item.ConversationKey);
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

    private int GetPartition(long conversationKey)
    {
        var hash = conversationKey.GetHashCode() & int.MaxValue;
        return hash % _partitions.Length;
    }

    private sealed record ConversationWorkItem(
        long ConversationKey,
        string Description,
        Func<CancellationToken, Task> Action);
}

public sealed class MessageDispatchOptions
{
    public int PartitionCount { get; set; } = 8;

    public int CapacityPerPartition { get; set; } = 64;
}
