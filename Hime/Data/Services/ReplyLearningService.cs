using System.Security.Cryptography;
using System.Text;
using Hime.Data.Models;
using Hime.Services;
using LiteDB;
using Microsoft.Extensions.Options;

namespace Hime.Data.Services;

/// <summary>
/// Stores human feedback as searchable reply examples. This is the long-term
/// learning loop: examples can be added at runtime and immediately affect later
/// social turns without changing code or restarting.
/// </summary>
public sealed class ReplyLearningService
{
    private readonly HimeDbContext _context;
    private readonly IOptionsMonitor<SocialIntelligenceOptions> _options;
    private readonly object _sync = new();

    public ReplyLearningService(
        HimeDbContext context,
        IOptionsMonitor<SocialIntelligenceOptions> options)
    {
        _context = context;
        _options = options;
        Examples.EnsureIndex(record => record.PersonaId);
        Examples.EnsureIndex(record => record.Label);
        Examples.EnsureIndex(record => record.IntentId);
        Examples.EnsureIndex(record => record.CreatedAtUtc);
    }

    private ILiteCollection<ReplyLearningRecord> Examples =>
        _context.Database.GetCollection<ReplyLearningRecord>("reply_learning_examples");

    public ReplyLearningRecord AddExample(ReplyLearningExampleDraft draft)
    {
        var record = new ReplyLearningRecord
        {
            Id = BuildId(draft),
            PersonaId = NormalizeToken(draft.PersonaId, "yangyang"),
            Label = NormalizeLabel(draft.Label),
            IntentId = NormalizeToken(draft.IntentId, "ordinary_chat"),
            Scene = Trim(draft.Scene, 80),
            UserMessage = Trim(draft.UserMessage, 800),
            BotReply = VisibleReplyTextSanitizer.Clean(Trim(draft.BotReply, 1000)),
            Reason = Trim(draft.Reason, 300),
            CreatedByUserId = draft.CreatedByUserId,
            GroupId = draft.GroupId,
            Source = Trim(draft.Source, 80),
            Weight = 1.0d,
            CreatedAtUtc = DateTime.UtcNow
        };

        lock (_sync)
            Examples.Upsert(record);
        return record;
    }

    public IReadOnlyList<ReplyLearningExampleMatch> FindRelevant(
        string personaId,
        string intentId,
        string scene,
        string userMessage,
        string label,
        int maximum)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled || maximum <= 0)
            return Array.Empty<ReplyLearningExampleMatch>();

        var normalizedPersona = NormalizeToken(personaId, "yangyang");
        var normalizedIntent = NormalizeToken(intentId, options.DefaultIntentId);
        var normalizedLabel = NormalizeLabel(label);
        var limit = Math.Clamp(maximum, 1, 8);
        var scanLimit = Math.Clamp(options.MaxLearningExamplesToScan, 20, 2000);
        var minSimilarity = Math.Clamp(options.MinimumExampleSimilarity, 0, 1);

        List<ReplyLearningRecord> candidates;
        lock (_sync)
        {
            candidates = Examples
                .Find(record =>
                    record.PersonaId == normalizedPersona &&
                    record.Label == normalizedLabel)
                .OrderByDescending(record => record.CreatedAtUtc)
                .Take(scanLimit)
                .ToList();
        }

        var matches = candidates
            .Select(record => new ReplyLearningExampleMatch(
                record,
                Score(record, normalizedIntent, scene, userMessage)))
            .Where(match => match.Score >= minSimilarity)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Record.CreatedAtUtc)
            .Take(limit)
            .ToList();

        if (matches.Count > 0)
        {
            var now = DateTime.UtcNow;
            lock (_sync)
            {
                foreach (var match in matches)
                {
                    match.Record.UsedCount++;
                    match.Record.LastUsedAtUtc = now;
                    Examples.Update(match.Record);
                }
            }
        }

        return matches;
    }

    public ReplyLearningRecord? GetLatest(long? groupId, string label = "")
    {
        var normalized = string.IsNullOrWhiteSpace(label) ? string.Empty : NormalizeLabel(label);
        lock (_sync)
        {
            return Examples
                .Find(record =>
                    (!groupId.HasValue || record.GroupId == groupId) &&
                    (string.IsNullOrWhiteSpace(normalized) || record.Label == normalized))
                .OrderByDescending(record => record.CreatedAtUtc)
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<ReplyLearningRecord> ListExamples(
        long? groupId,
        string label = "",
        int maximum = 10)
    {
        var normalized = string.IsNullOrWhiteSpace(label) ? string.Empty : NormalizeLabel(label);
        var limit = Math.Clamp(maximum, 1, 30);
        lock (_sync)
        {
            return Examples
                .Find(record =>
                    (!groupId.HasValue || record.GroupId == groupId) &&
                    (string.IsNullOrWhiteSpace(normalized) || record.Label == normalized))
                .OrderByDescending(record => record.CreatedAtUtc)
                .Take(limit)
                .Select(Clone)
                .ToList()
                .AsReadOnly();
        }
    }

    public ReplyLearningRecord? FindById(string id, long? groupId = null)
    {
        var normalized = NormalizeLookupId(id);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        lock (_sync)
        {
            var candidates = Examples
                .Find(record => !groupId.HasValue || record.GroupId == groupId)
                .ToList();
            return ResolveById(candidates, normalized) is { } record
                ? Clone(record)
                : null;
        }
    }

    public bool DeleteExample(string id, long? groupId, out ReplyLearningRecord? deleted)
    {
        var normalized = NormalizeLookupId(id);
        deleted = null;
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        lock (_sync)
        {
            var candidates = Examples
                .Find(record => !groupId.HasValue || record.GroupId == groupId)
                .ToList();
            var record = ResolveById(candidates, normalized);
            if (record is null)
                return false;

            deleted = Clone(record);
            return Examples.Delete(record.Id);
        }
    }

    public ReplyLearningRecord? SetWeight(string id, long? groupId, double weight)
    {
        var normalized = NormalizeLookupId(id);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var safeWeight = Math.Clamp(weight, 0.1d, 5.0d);
        lock (_sync)
        {
            var candidates = Examples
                .Find(record => !groupId.HasValue || record.GroupId == groupId)
                .ToList();
            var record = ResolveById(candidates, normalized);
            if (record is null)
                return null;

            record.Weight = safeWeight;
            Examples.Update(record);
            return Clone(record);
        }
    }

    public ReplyLearningStats GetStats(long? groupId = null)
    {
        lock (_sync)
        {
            var records = Examples
                .Find(record => !groupId.HasValue || record.GroupId == groupId)
                .ToList();
            var total = records.Count;
            return new ReplyLearningStats(
                total,
                records.Count(record => record.Label == "good"),
                records.Count(record => record.Label == "bad"),
                records.Count(record => record.GroupId.HasValue),
                records.Count(record => !record.GroupId.HasValue),
                total == 0 ? 0 : records.Average(record => NormalizeWeight(record.Weight)));
        }
    }

    private static double Score(
        ReplyLearningRecord record,
        string intentId,
        string scene,
        string userMessage)
    {
        var score = 0d;
        if (record.IntentId.Equals(intentId, StringComparison.OrdinalIgnoreCase))
            score += 0.45d;
        if (!string.IsNullOrWhiteSpace(scene) &&
            record.Scene.Equals(scene, StringComparison.OrdinalIgnoreCase))
            score += 0.10d;

        score += ConversationTopicGraph.Similarity(record.UserMessage, userMessage) * 0.40d;
        score += ConversationTopicGraph.Similarity(record.BotReply, userMessage) * 0.05d;
        return Math.Clamp(score * NormalizeWeight(record.Weight), 0, 1);
    }

    private static string NormalizeLabel(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "bad" or "坏" or "差" or "反例" or "negative"
            ? "bad"
            : "good";
    }

    private static string NormalizeToken(string? value, string fallback)
    {
        var normalized = new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
            .ToArray());
        return normalized.Length == 0 ? fallback : normalized;
    }

    private static string Trim(string? value, int maximum)
    {
        var normalized = new string((value ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }

    private static string BuildId(ReplyLearningExampleDraft draft)
    {
        var payload = string.Join(
            "|",
            NormalizeToken(draft.PersonaId, "yangyang"),
            NormalizeLabel(draft.Label),
            NormalizeToken(draft.IntentId, "ordinary_chat"),
            draft.Scene,
            draft.UserMessage,
            draft.BotReply,
            draft.Reason);
        return $"learn:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16].ToLowerInvariant()}";
    }

    private static ReplyLearningRecord Clone(ReplyLearningRecord source) => new()
    {
        Id = source.Id,
        PersonaId = source.PersonaId,
        Label = source.Label,
        IntentId = source.IntentId,
        Scene = source.Scene,
        UserMessage = source.UserMessage,
        BotReply = source.BotReply,
        Reason = source.Reason,
        CreatedByUserId = source.CreatedByUserId,
        GroupId = source.GroupId,
        Source = source.Source,
        Weight = NormalizeWeight(source.Weight),
        CreatedAtUtc = source.CreatedAtUtc,
        UsedCount = source.UsedCount,
        LastUsedAtUtc = source.LastUsedAtUtc
    };

    private static ReplyLearningRecord? ResolveById(
        IEnumerable<ReplyLearningRecord> records,
        string normalized)
    {
        var withPrefix = normalized.StartsWith("learn:", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"learn:{normalized}";
        return records
            .Where(record =>
                record.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                record.Id.Equals(withPrefix, StringComparison.OrdinalIgnoreCase) ||
                record.Id.StartsWith(withPrefix, StringComparison.OrdinalIgnoreCase) ||
                record.Id.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.CreatedAtUtc)
            .FirstOrDefault();
    }

    private static string NormalizeLookupId(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return new string(normalized
            .Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is ':' or '-' or '_' or '.')
            .ToArray());
    }

    private static double NormalizeWeight(double weight) =>
        double.IsFinite(weight) && weight > 0
            ? Math.Clamp(weight, 0.1d, 5.0d)
            : 1.0d;
}
