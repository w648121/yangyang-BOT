using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Keeps a sender's most recent ordinary image for the common QQ flow where a
/// user sends an image first and asks about it in the next message. QQ reply
/// cards do not include the original image segment in the follow-up event.
/// </summary>
public sealed class RecentVisualContextStore
{
    private readonly ConcurrentDictionary<VisualContextKey, RecentVisualContext> _contexts = new();
    private readonly object _persistenceGate = new();
    private readonly TimeSpan _retention;
    private readonly int _maxImages;
    private readonly IReadOnlyList<string> _referenceMarkers;
    private readonly string _indexPath;

    public RecentVisualContextStore(IOptions<RecentVisualContextOptions> options)
    {
        var value = options.Value;
        _retention = TimeSpan.FromMinutes(Math.Clamp(value.RetentionMinutes, 1, 60));
        _maxImages = Math.Clamp(value.MaxImagesPerMessage, 1, 3);
        _referenceMarkers = value.ReferenceMarkers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Select(marker => marker.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _indexPath = Path.GetFullPath(string.IsNullOrWhiteSpace(value.IndexPath)
            ? "data/recent-visual-context.json"
            : value.IndexPath);
        LoadPersistedContexts();
    }

    public void Remember(long? groupId, long userId, IReadOnlyList<string> imagePaths)
    {
        if (userId == 0 || imagePaths.Count == 0)
            return;

        var usablePaths = imagePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(_maxImages)
            .ToArray();
        if (usablePaths.Length == 0)
            return;

        lock (_persistenceGate)
        {
            _contexts[new VisualContextKey(groupId, userId)] = new RecentVisualContext(DateTime.UtcNow, usablePaths);
            PersistUnsafe();
        }
    }

    public bool TryGetForExplicitFollowUp(
        long? groupId,
        long userId,
        string prompt,
        out IReadOnlyList<string> imagePaths)
    {
        imagePaths = Array.Empty<string>();
        if (userId == 0 || !LooksLikeVisualQuestion(prompt))
            return false;

        var key = new VisualContextKey(groupId, userId);
        if (!_contexts.TryGetValue(key, out var context))
            return false;

        if (DateTime.UtcNow - context.RecordedAt > _retention)
        {
            RemoveAndPersist(key);
            return false;
        }

        var usablePaths = context.ImagePaths.Where(File.Exists).ToArray();
        if (usablePaths.Length == 0)
        {
            RemoveAndPersist(key);
            return false;
        }

        imagePaths = usablePaths;
        return true;
    }

    private bool LooksLikeVisualQuestion(string prompt)
    {
        return !string.IsNullOrWhiteSpace(prompt) &&
               _referenceMarkers.Any(token => prompt.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct VisualContextKey(long? GroupId, long UserId);

    private sealed record RecentVisualContext(DateTime RecordedAt, IReadOnlyList<string> ImagePaths);

    private sealed record PersistedVisualContext(long? GroupId, long UserId, DateTime RecordedAt, IReadOnlyList<string> ImagePaths);

    private void LoadPersistedContexts()
    {
        if (!File.Exists(_indexPath))
            return;

        try
        {
            var serialized = File.ReadAllText(_indexPath);
            var records = JsonSerializer.Deserialize<List<PersistedVisualContext>>(serialized) ?? [];
            var now = DateTime.UtcNow;
            var changed = false;
            foreach (var record in records)
            {
                var usablePaths = record.ImagePaths.Where(File.Exists).Take(_maxImages).ToArray();
                if (record.UserId == 0 || now - record.RecordedAt > _retention || usablePaths.Length == 0)
                {
                    changed = true;
                    continue;
                }

                _contexts[new VisualContextKey(record.GroupId, record.UserId)] =
                    new RecentVisualContext(record.RecordedAt, usablePaths);
            }

            if (changed)
            {
                lock (_persistenceGate)
                    PersistUnsafe();
            }
        }
        catch (IOException)
        {
            // A transient index failure must never block chat handling.
        }
        catch (UnauthorizedAccessException)
        {
            // A transient index failure must never block chat handling.
        }
        catch (JsonException)
        {
            // Ignore a damaged cache; it will be replaced by the next image.
        }
    }

    private void RemoveAndPersist(VisualContextKey key)
    {
        lock (_persistenceGate)
        {
            if (_contexts.TryRemove(key, out _))
                PersistUnsafe();
        }
    }

    private void PersistUnsafe()
    {
        try
        {
            var directory = Path.GetDirectoryName(_indexPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var records = _contexts
                .Select(pair => new PersistedVisualContext(
                    pair.Key.GroupId,
                    pair.Key.UserId,
                    pair.Value.RecordedAt,
                    pair.Value.ImagePaths))
                .ToArray();
            var temporaryPath = _indexPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records));
            File.Move(temporaryPath, _indexPath, overwrite: true);
        }
        catch (IOException)
        {
            // The in-memory context is still usable for this process lifetime.
        }
        catch (UnauthorizedAccessException)
        {
            // The in-memory context is still usable for this process lifetime.
        }
    }
}
