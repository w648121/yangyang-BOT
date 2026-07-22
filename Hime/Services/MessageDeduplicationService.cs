using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>Rejects replayed QQ events after reconnects without persisting message content.</summary>
public sealed class MessageDeduplicationService
{
    private readonly ConcurrentDictionary<string, long> _seen = new(StringComparer.Ordinal);
    private readonly MessageDeduplicationOptions _options;
    private long _calls;

    public MessageDeduplicationService(IOptions<MessageDeduplicationOptions> options)
    {
        _options = options.Value;
    }

    public int Count => _seen.Count;

    public bool TryAccept(string eventKey)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var lifetime = TimeSpan.FromSeconds(Math.Clamp(_options.RetentionSeconds, 30, 3600)).Ticks;
        while (true)
        {
            if (_seen.TryAdd(eventKey, now))
            {
                MaybeCleanup(now, lifetime);
                return true;
            }

            if (!_seen.TryGetValue(eventKey, out var previous))
                continue;
            if (now - previous <= lifetime)
                return false;
            if (_seen.TryUpdate(eventKey, now, previous))
            {
                MaybeCleanup(now, lifetime);
                return true;
            }
        }
    }

    private void MaybeCleanup(long now, long lifetime)
    {
        var maximum = Math.Clamp(_options.MaximumEntries, 1000, 200000);
        if (Interlocked.Increment(ref _calls) % 128 != 0 && _seen.Count < maximum)
            return;

        foreach (var pair in _seen)
        {
            if (now - pair.Value > lifetime)
                _seen.TryRemove(pair);
        }

        var excess = _seen.Count - maximum;
        if (excess <= 0)
            return;
        foreach (var pair in _seen.OrderBy(pair => pair.Value).Take(excess))
            _seen.TryRemove(pair);
    }
}

public sealed class MessageDeduplicationOptions
{
    public int RetentionSeconds { get; set; } = 600;
    public int MaximumEntries { get; set; } = 50000;
}
