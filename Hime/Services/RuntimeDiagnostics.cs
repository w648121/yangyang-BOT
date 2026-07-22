using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Lightweight in-process telemetry. It records timings and operational metadata,
/// never message text, prompts, API keys, file contents, or generated replies.
/// </summary>
public sealed class RuntimeDiagnostics
{
    private readonly ConcurrentDictionary<string, MetricBucket> _metrics = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<RuntimeDiagnosticEvent> _events = new();
    private readonly AsyncLocal<string?> _correlation = new();
    private readonly RuntimeDiagnosticsOptions _options;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public RuntimeDiagnostics(IOptions<RuntimeDiagnosticsOptions> options)
    {
        _options = options.Value;
    }

    public string? CurrentCorrelationId => _correlation.Value;

    public IDisposable PushCorrelation(string correlationId)
    {
        var previous = _correlation.Value;
        _correlation.Value = correlationId;
        return new DelegateDisposable(() => _correlation.Value = previous);
    }

    public RuntimeOperation Begin(string stage)
    {
        var bucket = _metrics.GetOrAdd(stage, _ => new MetricBucket());
        bucket.Start();
        return new RuntimeOperation(this, stage, bucket, Stopwatch.GetTimestamp());
    }

    public async Task<T> TrackAsync<T>(string stage, Func<Task<T>> action)
    {
        using var operation = Begin(stage);
        try
        {
            return await action();
        }
        catch
        {
            operation.Fail();
            throw;
        }
    }

    public async Task TrackAsync(string stage, Func<Task> action)
    {
        using var operation = Begin(stage);
        try
        {
            await action();
        }
        catch
        {
            operation.Fail();
            throw;
        }
    }

    public void Increment(string counter, long amount = 1) =>
        _counters.AddOrUpdate(counter, amount, (_, current) => current + amount);

    public void Event(string kind, string detail)
    {
        _events.Enqueue(new RuntimeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            kind,
            CurrentCorrelationId,
            detail.Length <= 160 ? detail : detail[..160]));
        TrimEvents();
    }

    public RuntimeDiagnosticsSnapshot Snapshot()
    {
        var sampleLimit = Math.Clamp(_options.SampleWindowSize, 32, 4096);
        var metrics = _metrics
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot(sampleLimit), StringComparer.OrdinalIgnoreCase);
        var counters = _counters
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var events = _events.TakeLast(Math.Clamp(_options.RecentEventLimit, 10, 200)).ToArray();
        return new RuntimeDiagnosticsSnapshot(_startedAt, metrics, counters, events);
    }

    private void Complete(string stage, MetricBucket bucket, long timestamp, bool failed)
    {
        var elapsed = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        bucket.Complete(elapsed, failed, Math.Clamp(_options.SampleWindowSize, 32, 4096));
        if (failed)
        {
            Increment("operations.failed");
            Event("failure", stage);
        }
    }

    private void TrimEvents()
    {
        var maximum = Math.Clamp(_options.RecentEventLimit, 10, 200);
        while (_events.Count > maximum && _events.TryDequeue(out _))
        {
        }
    }

    public sealed class RuntimeOperation : IDisposable
    {
        private readonly RuntimeDiagnostics _owner;
        private readonly string _stage;
        private readonly MetricBucket _bucket;
        private readonly long _started;
        private int _disposed;
        private bool _failed;

        internal RuntimeOperation(RuntimeDiagnostics owner, string stage, MetricBucket bucket, long started)
        {
            _owner = owner;
            _stage = stage;
            _bucket = bucket;
            _started = started;
        }

        public void Fail() => _failed = true;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _owner.Complete(_stage, _bucket, _started, _failed);
        }
    }

    internal sealed class MetricBucket
    {
        private readonly object _sync = new();
        private readonly Queue<double> _samples = new();
        private long _completed;
        private long _failed;
        private int _active;

        public void Start() => Interlocked.Increment(ref _active);

        public void Complete(double elapsedMilliseconds, bool failed, int sampleLimit)
        {
            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _completed);
            if (failed)
                Interlocked.Increment(ref _failed);

            lock (_sync)
            {
                _samples.Enqueue(Math.Max(0, elapsedMilliseconds));
                while (_samples.Count > sampleLimit)
                    _samples.Dequeue();
            }
        }

        public RuntimeMetricSnapshot Snapshot(int sampleLimit)
        {
            double[] samples;
            lock (_sync)
                samples = _samples.TakeLast(sampleLimit).Order().ToArray();

            return new RuntimeMetricSnapshot(
                Interlocked.Read(ref _completed),
                Interlocked.Read(ref _failed),
                Volatile.Read(ref _active),
                samples.Length == 0 ? 0 : samples.Average(),
                Percentile(samples, 0.50),
                Percentile(samples, 0.95),
                samples.Length == 0 ? 0 : samples[^1]);
        }

        private static double Percentile(IReadOnlyList<double> values, double percentile)
        {
            if (values.Count == 0)
                return 0;
            var index = (int)Math.Ceiling(percentile * values.Count) - 1;
            return values[Math.Clamp(index, 0, values.Count - 1)];
        }
    }

    private sealed class DelegateDisposable(Action action) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                action();
        }
    }
}

public sealed class RuntimeDiagnosticsOptions
{
    public int SampleWindowSize { get; set; } = 512;
    public int RecentEventLimit { get; set; } = 50;
}

public sealed record RuntimeDiagnosticsSnapshot(
    DateTimeOffset StartedAt,
    IReadOnlyDictionary<string, RuntimeMetricSnapshot> Metrics,
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyList<RuntimeDiagnosticEvent> RecentEvents);

public sealed record RuntimeMetricSnapshot(
    long Completed,
    long Failed,
    int Active,
    double AverageMs,
    double P50Ms,
    double P95Ms,
    double MaximumMs);

public sealed record RuntimeDiagnosticEvent(
    DateTimeOffset Time,
    string Kind,
    string? CorrelationId,
    string Detail);
