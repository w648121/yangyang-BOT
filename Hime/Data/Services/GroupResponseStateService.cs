using LiteDB;

namespace Hime.Data.Services;

/// <summary>
/// Database-backed switch for every group's conversational response state.
/// An unknown group is disabled by default; configuration files are never consulted.
/// </summary>
public sealed class GroupResponseStateService
{
    private readonly HimeDbContext _context;
    private readonly object _sync = new();

    public GroupResponseStateService(HimeDbContext context)
    {
        _context = context;
        States.EnsureIndex(item => item.Enabled);
        States.EnsureIndex(item => item.UpdatedAtUtc);
    }

    private ILiteCollection<GroupResponseStateRecord> States =>
        _context.Database.GetCollection<GroupResponseStateRecord>("group_response_states");

    public bool IsEnabled(long groupId)
    {
        if (groupId <= 0)
            return false;

        lock (_sync)
            return States.FindById(groupId)?.Enabled == true;
    }

    public GroupResponseLease? TryCapture(long groupId)
    {
        if (groupId <= 0)
            return null;

        lock (_sync)
        {
            var state = States.FindById(groupId);
            return state?.Enabled == true
                ? new GroupResponseLease(groupId, state.GenerationEpoch)
                : null;
        }
    }

    public bool CanDeliver(GroupResponseLease lease)
    {
        lock (_sync)
        {
            var state = States.FindById(lease.GroupId);
            return state?.Enabled == true &&
                   state.GenerationEpoch == lease.GenerationEpoch;
        }
    }

    public IReadOnlyList<long> GetEnabledGroupIds()
    {
        lock (_sync)
            return States.Query()
                .Where(item => item.Enabled)
                .OrderBy(item => item.GroupId)
                .ToList()
                .Select(item => item.GroupId)
                .ToArray();
    }

    public GroupResponseStateRecord SetEnabled(
        long groupId,
        bool enabled,
        long updatedByUserId,
        string? groupName = null,
        string source = "command")
    {
        if (groupId <= 0)
            throw new ArgumentOutOfRangeException(nameof(groupId));

        lock (_sync)
        {
            var existing = States.FindById(groupId);
            var record = new GroupResponseStateRecord
            {
                GroupId = groupId,
                Enabled = enabled,
                GroupName = string.IsNullOrWhiteSpace(groupName)
                    ? existing?.GroupName ?? string.Empty
                    : groupName.Trim(),
                UpdatedByUserId = updatedByUserId,
                UpdatedAtUtc = DateTime.UtcNow,
                Source = string.IsNullOrWhiteSpace(source) ? "command" : source.Trim(),
                GenerationEpoch = checked((existing?.GenerationEpoch ?? 0) + 1)
            };
            States.Upsert(record);
            _context.SaveChanges();
            return record;
        }
    }
}

public sealed class GroupResponseStateRecord
{
    [BsonId]
    public long GroupId { get; set; }
    public bool Enabled { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public long UpdatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string Source { get; set; } = "command";
    public long GenerationEpoch { get; set; }
}

public readonly record struct GroupResponseLease(long GroupId, long GenerationEpoch);
