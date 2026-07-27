using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Preserves strict ordering inside one conversation without tying unrelated
/// conversations to the same fixed partition. A global capacity and concurrency
/// limit keep memory and slow model work bounded.
/// </summary>
public sealed class ConversationMessageDispatcher : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<long, ConversationLane> _lanes = new();
    private readonly ConcurrentDictionary<long, int> _pendingByConversation = new();
    private readonly SemaphoreSlim _parallelism;
    private readonly SemaphoreSlim _globalCapacity;
    private readonly int _capacityPerConversation;
    private readonly int _maximumConcurrency;
    private readonly ILogger<ConversationMessageDispatcher> _logger;
    private readonly RuntimeDiagnostics _diagnostics;
    private readonly CancellationTokenSource _shutdown = new();
    private long _pendingTotal;
    private volatile bool _accepting;

    public ConversationMessageDispatcher(
        IOptions<MessageDispatchOptions> options,
        ILogger<ConversationMessageDispatcher> logger,
        RuntimeDiagnostics diagnostics)
    {
        _maximumConcurrency = Math.Clamp(options.Value.PartitionCount, 1, 32);
        _capacityPerConversation = Math.Clamp(options.Value.CapacityPerPartition, 8, 512);
        _parallelism = new SemaphoreSlim(_maximumConcurrency, _maximumConcurrency);
        _globalCapacity = new SemaphoreSlim(
            checked(_maximumConcurrency * _capacityPerConversation),
            checked(_maximumConcurrency * _capacityPerConversation));
        _logger = logger;
        _diagnostics = diagnostics;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _accepting = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _accepting = false;
        await _shutdown.CancelAsync();

        var runners = _lanes.Values
            .Select(lane => lane.RunnerTask)
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (runners.Length == 0)
            return;

        try
        {
            await Task.WhenAll(runners).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Normal host shutdown or caller timeout.
        }
    }

    public async ValueTask EnqueueAsync(
        long conversationKey,
        string description,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_accepting)
            throw new InvalidOperationException("The conversation dispatcher is not accepting messages.");

        var started = Stopwatch.GetTimestamp();
        _pendingByConversation.AddOrUpdate(conversationKey, 1, (_, count) => count + 1);
        Interlocked.Increment(ref _pendingTotal);
        _diagnostics.Increment("messages.queued");
        var waitOperation = _diagnostics.Begin("queue.message.wait");
        var queued = false;
        ConversationLane? lane = null;
        var laneSlotHeld = false;
        var globalSlotHeld = false;

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        var token = linkedCancellation.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lane = _lanes.GetOrAdd(
                    conversationKey,
                    key => new ConversationLane(key, _capacityPerConversation));

                await lane.Capacity.WaitAsync(token);
                laneSlotHeld = true;
                await _globalCapacity.WaitAsync(token);
                globalSlotHeld = true;

                lock (lane.Sync)
                {
                    if (lane.Retired)
                    {
                        lane.Capacity.Release();
                        _globalCapacity.Release();
                        laneSlotHeld = false;
                        globalSlotHeld = false;
                        continue;
                    }

                    if (!_accepting)
                        throw new OperationCanceledException("The conversation dispatcher is stopping.", token);

                    lane.Queue.Enqueue(new ConversationWorkItem(conversationKey, description, action));
                    queued = true;
                    laneSlotHeld = false;
                    globalSlotHeld = false;
                    if (!lane.IsRunning)
                    {
                        lane.IsRunning = true;
                        lane.RunnerTask = Task.Run(
                            () => RunLaneAsync(lane),
                            CancellationToken.None);
                    }
                }

                break;
            }
        }
        catch
        {
            if (laneSlotHeld)
                lane?.Capacity.Release();
            if (globalSlotHeld)
                _globalCapacity.Release();
            if (lane is not null)
                TryRetireIdleLane(lane);
            if (!queued)
                CompleteConversationWork(conversationKey);
            waitOperation.Fail();
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

    /// <summary>
    /// Retained for diagnostics/configuration compatibility. It now represents the
    /// maximum number of conversations that may execute concurrently.
    /// </summary>
    public int PartitionCount => _maximumConcurrency;

    public void Dispose()
    {
        _shutdown.Dispose();
        _parallelism.Dispose();
        _globalCapacity.Dispose();
        foreach (var lane in _lanes.Values)
            lane.Capacity.Dispose();
    }

    private async Task RunLaneAsync(ConversationLane lane)
    {
        try
        {
            while (true)
            {
                ConversationWorkItem? item;
                lock (lane.Sync)
                {
                    if (lane.Queue.Count == 0)
                    {
                        lane.Retired = true;
                        lane.IsRunning = false;
                        _lanes.TryRemove(new KeyValuePair<long, ConversationLane>(lane.Key, lane));
                        return;
                    }

                    item = lane.Queue.Dequeue();
                }

                var enteredParallelSection = false;
                using var operation = _diagnostics.Begin("message.process");
                try
                {
                    await _parallelism.WaitAsync(_shutdown.Token);
                    enteredParallelSection = true;
                    await item.Action(_shutdown.Token);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    operation.Fail();
                }
                catch (Exception ex)
                {
                    operation.Fail();
                    _logger.LogError(
                        ex,
                        "Conversation work failed (Key={Key}, Work={Work})",
                        item.ConversationKey,
                        item.Description);
                }
                finally
                {
                    if (enteredParallelSection)
                        _parallelism.Release();
                    lane.Capacity.Release();
                    _globalCapacity.Release();
                    CompleteConversationWork(item.ConversationKey);
                }
            }
        }
        finally
        {
            DrainAbandonedLane(lane);
        }
    }

    private void DrainAbandonedLane(ConversationLane lane)
    {
        ConversationWorkItem[] abandoned;
        lock (lane.Sync)
        {
            lane.Retired = true;
            lane.IsRunning = false;
            abandoned = lane.Queue.ToArray();
            lane.Queue.Clear();
            _lanes.TryRemove(new KeyValuePair<long, ConversationLane>(lane.Key, lane));
        }

        foreach (var item in abandoned)
        {
            lane.Capacity.Release();
            _globalCapacity.Release();
            CompleteConversationWork(item.ConversationKey);
        }
    }

    private void TryRetireIdleLane(ConversationLane lane)
    {
        lock (lane.Sync)
        {
            if (lane.IsRunning || lane.Queue.Count > 0 || lane.Retired)
                return;
            lane.Retired = true;
            _lanes.TryRemove(new KeyValuePair<long, ConversationLane>(lane.Key, lane));
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

    private sealed class ConversationLane(long key, int capacity)
    {
        public long Key { get; } = key;
        public object Sync { get; } = new();
        public Queue<ConversationWorkItem> Queue { get; } = new();
        public SemaphoreSlim Capacity { get; } = new(capacity, capacity);
        public bool IsRunning { get; set; }
        public bool Retired { get; set; }
        public Task? RunnerTask { get; set; }
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
