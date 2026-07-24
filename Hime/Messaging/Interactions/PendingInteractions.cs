using Hime.Commands;
using Hime.Data;
using Hime.Jobs;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace Hime.Messaging.Interactions;

public enum InteractionMode
{
    HardWait = 0,
    SoftExpectation = 1
}

public sealed record PendingInteraction(
    string Id,
    string ScopeKey,
    string Kind,
    InteractionMode Mode,
    long RequestedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? StateJson = null);

public interface IPendingInteractionStore
{
    IReadOnlyList<PendingInteraction> GetActive(string scopeKey, DateTimeOffset now);
    void Save(PendingInteraction interaction);
    void Remove(string id);
    int RemoveScope(string scopeKey, InteractionMode? mode = null);
}

public sealed class LiteDbPendingInteractionStore : IPendingInteractionStore
{
    private readonly ILiteCollection<PendingInteractionDocument> _collection;

    public LiteDbPendingInteractionStore(HimeDbContext db)
    {
        _collection = db.Database.GetCollection<PendingInteractionDocument>("pending_interactions");
        _collection.EnsureIndex(item => item.ScopeKey);
        _collection.EnsureIndex(item => item.ExpiresAtUnixMilliseconds);
    }

    public IReadOnlyList<PendingInteraction> GetActive(string scopeKey, DateTimeOffset now)
    {
        var nowValue = now.ToUnixTimeMilliseconds();
        _collection.DeleteMany(item => item.ExpiresAtUnixMilliseconds <= nowValue);
        return _collection.Query()
            .Where(item => item.ScopeKey == scopeKey && item.ExpiresAtUnixMilliseconds > nowValue)
            .OrderBy(item => item.CreatedAtUnixMilliseconds)
            .ToList()
            .Select(ToDomain)
            .ToArray();
    }

    public void Save(PendingInteraction interaction) =>
        _collection.Upsert(ToDocument(interaction));

    public void Remove(string id) => _collection.Delete(id);

    public int RemoveScope(string scopeKey, InteractionMode? mode = null) =>
        mode.HasValue
            ? _collection.DeleteMany(item => item.ScopeKey == scopeKey && item.Mode == (int)mode.Value)
            : _collection.DeleteMany(item => item.ScopeKey == scopeKey);

    private static PendingInteraction ToDomain(PendingInteractionDocument item) =>
        new(
            item.Id,
            item.ScopeKey,
            item.Kind,
            (InteractionMode)item.Mode,
            item.RequestedBy,
            DateTimeOffset.FromUnixTimeMilliseconds(item.CreatedAtUnixMilliseconds),
            DateTimeOffset.FromUnixTimeMilliseconds(item.ExpiresAtUnixMilliseconds),
            item.StateJson);

    private static PendingInteractionDocument ToDocument(PendingInteraction item) =>
        new()
        {
            Id = item.Id,
            ScopeKey = item.ScopeKey,
            Kind = item.Kind,
            Mode = (int)item.Mode,
            RequestedBy = item.RequestedBy,
            CreatedAtUnixMilliseconds = item.CreatedAt.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMilliseconds = item.ExpiresAt.ToUnixTimeMilliseconds(),
            StateJson = item.StateJson
        };

    private sealed class PendingInteractionDocument
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string ScopeKey { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public int Mode { get; set; }
        public long RequestedBy { get; set; }
        public long CreatedAtUnixMilliseconds { get; set; }
        public long ExpiresAtUnixMilliseconds { get; set; }
        public string? StateJson { get; set; }
    }
}

public interface IInteractionManager
{
    Task RegisterAsync(PendingInteraction interaction, CancellationToken cancellationToken = default);
    Task<PendingInteraction?> ClaimHardWaitAsync(string scopeKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingInteraction>> GetSoftExpectationsAsync(
        string scopeKey,
        CancellationToken cancellationToken = default);
    Task<int> CancelAsync(
        string scopeKey,
        InteractionMode? mode = null,
        CancellationToken cancellationToken = default);
}

public sealed class InteractionManager(IPendingInteractionStore store) : IInteractionManager
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RegisterAsync(
        PendingInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        if (interaction.ExpiresAt <= interaction.CreatedAt)
            throw new ArgumentException("Interaction expiration must be later than creation.", nameof(interaction));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (interaction.Mode == InteractionMode.HardWait)
                store.RemoveScope(interaction.ScopeKey, InteractionMode.HardWait);
            store.Save(interaction);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PendingInteraction?> ClaimHardWaitAsync(
        string scopeKey,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var interaction = store.GetActive(scopeKey, DateTimeOffset.UtcNow)
                .FirstOrDefault(item => item.Mode == InteractionMode.HardWait);
            if (interaction is not null)
                store.Remove(interaction.Id);
            return interaction;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PendingInteraction>> GetSoftExpectationsAsync(
        string scopeKey,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return store.GetActive(scopeKey, DateTimeOffset.UtcNow)
                .Where(item => item.Mode == InteractionMode.SoftExpectation)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CancelAsync(
        string scopeKey,
        InteractionMode? mode = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return store.RemoveScope(scopeKey, mode);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public interface IInteractionContinuationHandler
{
    string Kind { get; }

    Task ResumeAsync(
        MessageContext context,
        PendingInteraction interaction,
        CancellationToken cancellationToken);
}

public sealed class PendingInteractionMiddleware(
    IInteractionManager interactions,
    IEnumerable<IInteractionContinuationHandler> handlers,
    ILogger<PendingInteractionMiddleware> logger) : IMessageMiddleware
{
    private readonly IReadOnlyDictionary<string, IInteractionContinuationHandler> _handlers =
        handlers.ToDictionary(item => item.Kind, StringComparer.OrdinalIgnoreCase);

    public int Order => 200;

    public async Task InvokeAsync(
        MessageContext context,
        MessageHandlerDelegate next,
        CancellationToken cancellationToken)
    {
        var message = context.Message;
        var text = message.Text.Trim();
        var userScope = JobInteractionScopes.ForUser(message.ScopeKey, message.SenderId);
        var scopeKeys = new[] { userScope, message.ScopeKey }
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var softItems = new List<PendingInteraction>();
        foreach (var scopeKey in scopeKeys)
        {
            softItems.AddRange(
                await interactions.GetSoftExpectationsAsync(scopeKey, cancellationToken));
        }
        var soft = softItems
            .DistinctBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (soft.Length > 0)
            context.Items["soft-expectations"] = soft;

        if (IsCancellation(text))
        {
            var removed = 0;
            foreach (var scopeKey in scopeKeys)
                removed += await interactions.CancelAsync(scopeKey, null, cancellationToken);
            if (removed > 0)
            {
                await AiCommand.Reply(message.NativeEvent, "已取消当前等待。");
                context.MarkHandled("interaction-cancelled");
                return;
            }
        }

        // An explicit new command supersedes an older hard wait and is routed normally.
        if (IsExplicitCommand(text))
        {
            foreach (var scopeKey in scopeKeys)
            {
                await interactions.CancelAsync(
                    scopeKey,
                    InteractionMode.HardWait,
                    cancellationToken);
            }
            await next(context, cancellationToken);
            return;
        }

        PendingInteraction? pending = null;
        foreach (var scopeKey in scopeKeys)
        {
            pending = await interactions.ClaimHardWaitAsync(scopeKey, cancellationToken);
            if (pending is not null)
                break;
        }
        if (pending is null)
        {
            await next(context, cancellationToken);
            return;
        }

        if (!_handlers.TryGetValue(pending.Kind, out var handler))
        {
            logger.LogWarning(
                "No continuation handler is registered for interaction kind {Kind}; restoring the wait",
                pending.Kind);
            if (pending.ExpiresAt > DateTimeOffset.UtcNow)
                await interactions.RegisterAsync(pending, cancellationToken);
            await next(context, cancellationToken);
            return;
        }

        try
        {
            await handler.ResumeAsync(context, pending, cancellationToken);
            context.MarkHandled($"interaction:{pending.Kind}");
        }
        catch
        {
            if (pending.ExpiresAt > DateTimeOffset.UtcNow)
                await interactions.RegisterAsync(pending, CancellationToken.None);
            throw;
        }
    }

    private static bool IsCancellation(string text) =>
        text.Equals("取消", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("/取消", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("~取消", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("/cancel", StringComparison.OrdinalIgnoreCase);

    private static bool IsExplicitCommand(string text) =>
        text.StartsWith("/", StringComparison.Ordinal) ||
        text.StartsWith("~ai", StringComparison.OrdinalIgnoreCase);
}
