using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Data.Services;

/// <summary>
/// Coalesces frequent updates by logical record key and writes only the latest
/// snapshot on a background thread. This keeps LiteDB I/O off the QQ receive path.
/// </summary>
public sealed class LiteDbWriteBehindService : BackgroundService
{
    private readonly ConcurrentDictionary<string, Action> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly LiteDbWriteBehindOptions _options;
    private readonly ILogger<LiteDbWriteBehindService> _logger;
    private int _warningIssued;

    public LiteDbWriteBehindService(
        IOptions<LiteDbWriteBehindOptions> options,
        ILogger<LiteDbWriteBehindService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public int PendingCount => _pending.Count;

    public void Enqueue(string key, Action writeLatestSnapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(writeLatestSnapshot);

        var added = _pending.TryAdd(key, writeLatestSnapshot);
        if (!added)
            _pending[key] = writeLatestSnapshot;
        else
            _signal.Release();

        var warningThreshold = Math.Max(32, _options.PendingKeyWarningThreshold);
        if (_pending.Count >= warningThreshold && Interlocked.Exchange(ref _warningIssued, 1) == 0)
        {
            _logger.LogWarning(
                "LiteDB write-behind has {Count} pending record keys; receive processing remains non-blocking",
                _pending.Count);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.FlushIntervalMilliseconds, 25, 5000));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(interval, stoppingToken);
                FlushBatch(Math.Clamp(_options.MaxBatchSize, 1, 2048));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A final synchronous drain below preserves the newest snapshots on graceful shutdown.
        }
        finally
        {
            FlushAll();
        }
    }

    private void FlushBatch(int maximum)
    {
        var processed = 0;
        foreach (var key in _pending.Keys)
        {
            if (processed >= maximum)
                break;
            if (!_pending.TryRemove(key, out var write))
                continue;

            ExecuteWrite(key, write);
            processed++;
        }

        if (_pending.Count == 0)
            Interlocked.Exchange(ref _warningIssued, 0);
        else
            _signal.Release();
    }

    private void FlushAll()
    {
        while (!_pending.IsEmpty)
            FlushBatch(int.MaxValue);
    }

    private void ExecuteWrite(string key, Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background LiteDB write failed for {Key}", key);
        }
    }
}
