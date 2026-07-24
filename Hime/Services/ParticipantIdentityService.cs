using Hime.Data;
using LiteDB;
using Microsoft.Extensions.Options;

namespace Hime.Services;

public enum ParticipantKind
{
    Human,
    Administrator,
    ExternalBot,
    SelfBot
}

/// <summary>
/// Seeds known non-human QQ participants. Runtime decisions are persisted in
/// LiteDB so model context never depends on a hard-coded nickname.
/// </summary>
public sealed class ParticipantIdentityOptions
{
    public List<long> ExternalBotUserIds { get; set; } = [];
}

public sealed class ParticipantProfileRecord
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string Platform { get; set; } = "qq";
    public long UserId { get; set; }
    public ParticipantKind Kind { get; set; } = ParticipantKind.Human;
    public string Source { get; set; } = "runtime-default";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Provides one authoritative participant classification for middleware and
/// context assembly. Unknown senders remain human until explicitly classified.
/// </summary>
public sealed class ParticipantIdentityService
{
    private readonly ILiteCollection<ParticipantProfileRecord> _profiles;
    private readonly HashSet<long> _configuredExternalBots;
    private readonly object _sync = new();

    public ParticipantIdentityService(
        HimeDbContext context,
        IOptions<ParticipantIdentityOptions> options)
    {
        _profiles = context.Database.GetCollection<ParticipantProfileRecord>("participant_profiles");
        _profiles.EnsureIndex(profile => profile.UserId);
        _profiles.EnsureIndex(profile => profile.Kind);
        _configuredExternalBots = options.Value.ExternalBotUserIds
            .Where(userId => userId > 0)
            .ToHashSet();

        foreach (var userId in _configuredExternalBots)
            Seed("qq", userId, ParticipantKind.ExternalBot, "configuration-seed");
    }

    public ParticipantKind GetKind(string platform, long userId, long selfId = 0)
    {
        if (userId <= 0)
            return ParticipantKind.Human;
        if (selfId > 0 && userId == selfId)
            return ParticipantKind.SelfBot;

        var id = BuildId(platform, userId);
        lock (_sync)
        {
            var stored = _profiles.FindById(id);
            if (stored is not null)
                return stored.Kind;
        }

        return _configuredExternalBots.Contains(userId)
            ? ParticipantKind.ExternalBot
            : ParticipantKind.Human;
    }

    public bool IsExternalBot(string platform, long userId) =>
        GetKind(platform, userId) == ParticipantKind.ExternalBot;

    public ParticipantProfileRecord SetKind(
        string platform,
        long userId,
        ParticipantKind kind,
        string source)
    {
        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId));

        var record = new ParticipantProfileRecord
        {
            Id = BuildId(platform, userId),
            Platform = NormalizePlatform(platform),
            UserId = userId,
            Kind = kind,
            Source = string.IsNullOrWhiteSpace(source) ? "runtime" : source.Trim(),
            UpdatedAtUtc = DateTime.UtcNow
        };
        lock (_sync)
            _profiles.Upsert(record);
        return record;
    }

    private void Seed(string platform, long userId, ParticipantKind kind, string source)
    {
        var id = BuildId(platform, userId);
        lock (_sync)
        {
            if (_profiles.Exists(profile => profile.Id == id))
                return;
            _profiles.Insert(new ParticipantProfileRecord
            {
                Id = id,
                Platform = NormalizePlatform(platform),
                UserId = userId,
                Kind = kind,
                Source = source,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
    }

    private static string BuildId(string platform, long userId) =>
        $"{NormalizePlatform(platform)}:{userId}";

    private static string NormalizePlatform(string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? "qq" : platform.Trim().ToLowerInvariant();
}
