using System.Security.Cryptography;
using System.Text;
using Hime.Data;
using LiteDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Durable outbox for the remaining voice part of a reply whose text was already
/// sent. It deliberately stores no API credentials or inbound message content.
/// </summary>
public sealed class VoiceOutboxStore
{
    private readonly ILiteCollection<VoiceOutboxEntry> _collection;
    private readonly Dictionary<string, VoiceOutboxEntry> _entries;
    private readonly VoiceOutboxOptions _options;
    private readonly ILogger<VoiceOutboxStore> _logger;
    private readonly object _sync = new();

    public VoiceOutboxStore(
        HimeDbContext context,
        IOptions<VoiceSynthesisOptions> options,
        ILogger<VoiceOutboxStore> logger)
    {
        _options = options.Value.Outbox;
        _logger = logger;
        _collection = context.Database.GetCollection<VoiceOutboxEntry>("voice_outbox");
        _collection.EnsureIndex(entry => entry.CreatedAt);
        _entries = _collection.FindAll().ToDictionary(entry => entry.Id, StringComparer.Ordinal);
    }

    public int PendingCount
    {
        get
        {
            lock (_sync)
                return _entries.Values.Count(entry => entry.Attempts < Math.Max(1, _options.MaximumAttempts));
        }
    }

    public int FailedCount
    {
        get
        {
            lock (_sync)
                return _entries.Values.Count(entry => entry.Attempts >= Math.Max(1, _options.MaximumAttempts));
        }
    }

    public VoiceOutboxEntry? TryCreate(
        string speechText,
        string voice,
        string? context,
        string? emotion,
        int priority,
        string? correlationId,
        string destinationKind,
        long destinationId,
        long? generationEpoch = null)
    {
        if (!_options.Enabled || destinationId <= 0)
            return null;

        var identity = string.Join('\n', correlationId ?? Guid.NewGuid().ToString("N"), context, speechText, voice, destinationKind, destinationId, generationEpoch);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
        lock (_sync)
        {
            if (_entries.ContainsKey(id))
                return null;

            var entry = new VoiceOutboxEntry
            {
                Id = id,
                SpeechText = speechText,
                Voice = voice,
                Context = context,
                Emotion = emotion,
                Priority = priority,
                CorrelationId = correlationId,
                DestinationKind = destinationKind,
                DestinationId = destinationId,
                GenerationEpoch = generationEpoch,
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            };
            _entries.Add(id, entry);
            _collection.Upsert(entry);
            return Clone(entry);
        }
    }

    public IReadOnlyList<VoiceOutboxEntry> GetRecoverable(DateTimeOffset now)
    {
        var maximumAge = TimeSpan.FromMinutes(Math.Clamp(_options.RecoveryMaximumAgeMinutes, 1, 60));
        var maximumAttempts = Math.Clamp(_options.MaximumAttempts, 1, 20);
        lock (_sync)
        {
            var expired = _entries.Values
                .Where(entry => now - entry.CreatedAt > maximumAge)
                .Select(entry => entry.Id)
                .ToList();
            foreach (var id in expired)
            {
                _entries.Remove(id);
                _collection.Delete(id);
            }
            if (expired.Count > 0)
                _logger.LogInformation("Discarded {Count} expired durable voice task(s)", expired.Count);

            return _entries.Values
                .Where(entry => entry.Attempts < maximumAttempts && entry.NextAttemptAt <= now)
                .OrderBy(entry => entry.Priority)
                .ThenBy(entry => entry.CreatedAt)
                .Select(Clone)
                .ToList();
        }
    }

    public void MarkFailure(string id, string? error)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var entry))
                return;
            entry.Attempts++;
            entry.LastError = Trim(error, 300);
            entry.LastAttemptAt = DateTimeOffset.UtcNow;
            entry.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(120, 10 * Math.Max(1, entry.Attempts)));
            _collection.Upsert(entry);
        }
    }

    public void Complete(string id)
    {
        lock (_sync)
        {
            _entries.Remove(id);
            _collection.Delete(id);
        }
    }

    public void Discard(string id, string reason)
    {
        lock (_sync)
        {
            if (!_entries.Remove(id))
                return;
            _collection.Delete(id);
            _logger.LogInformation("Discarded durable voice task {Id}: {Reason}", id, reason);
        }
    }

    private static VoiceOutboxEntry Clone(VoiceOutboxEntry entry) => new()
    {
        Id = entry.Id,
        SpeechText = entry.SpeechText,
        Voice = entry.Voice,
        Context = entry.Context,
        Emotion = entry.Emotion,
        Priority = entry.Priority,
        CorrelationId = entry.CorrelationId,
        DestinationKind = entry.DestinationKind,
        DestinationId = entry.DestinationId,
        GenerationEpoch = entry.GenerationEpoch,
        CreatedAt = entry.CreatedAt,
        Attempts = entry.Attempts,
        LastAttemptAt = entry.LastAttemptAt,
        NextAttemptAt = entry.NextAttemptAt,
        LastError = entry.LastError
    };

    private static string? Trim(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maximum ? value : value[..maximum];
}

public sealed class VoiceOutboxEntry
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string SpeechText { get; set; } = string.Empty;
    public string Voice { get; set; } = string.Empty;
    public string? Context { get; set; }
    public string? Emotion { get; set; }
    public int Priority { get; set; }
    public string? CorrelationId { get; set; }
    public string DestinationKind { get; set; } = "group";
    public long DestinationId { get; set; }
    public long? GenerationEpoch { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}
