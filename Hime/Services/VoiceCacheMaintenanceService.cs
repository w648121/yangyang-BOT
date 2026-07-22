using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>Keeps the generated WAV cache bounded without touching configured reference audio.</summary>
public sealed class VoiceCacheMaintenanceService : BackgroundService
{
    private readonly VoiceSynthesisOptions _options;
    private readonly RuntimeDiagnostics _diagnostics;
    private readonly ILogger<VoiceCacheMaintenanceService> _logger;
    private long _cacheBytes;
    private int _cacheFileCount;

    public VoiceCacheMaintenanceService(
        IOptions<VoiceSynthesisOptions> options,
        RuntimeDiagnostics diagnostics,
        ILogger<VoiceCacheMaintenanceService> logger)
    {
        _options = options.Value;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    public long CacheBytes => Interlocked.Read(ref _cacheBytes);
    public int CacheFileCount => Volatile.Read(ref _cacheFileCount);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            RunMaintenance();
            var minutes = Math.Clamp(_options.Cache.MaintenanceIntervalMinutes, 5, 24 * 60);
            await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
        }
    }

    public void RunMaintenance()
    {
        var directory = ResolveCacheDirectory();
        if (!Directory.Exists(directory))
        {
            Interlocked.Exchange(ref _cacheBytes, 0);
            Volatile.Write(ref _cacheFileCount, 0);
            return;
        }

        try
        {
            RemoveStaleWorkFiles(directory);
            var retention = DateTime.UtcNow.AddDays(-Math.Clamp(_options.Cache.RetentionDays, 1, 365));
            var files = new DirectoryInfo(directory)
                .EnumerateFiles("*.wav", SearchOption.TopDirectoryOnly)
                .Select(file => new CacheFile(file, LastUse(file)))
                .OrderBy(item => item.LastUseUtc)
                .ToList();

            foreach (var item in files.Where(item => item.LastUseUtc < retention).ToList())
            {
                if (TryDelete(item.File))
                    files.Remove(item);
            }

            var maximumBytes = Math.Max(64L, _options.Cache.MaximumMegabytes) * 1024 * 1024;
            var totalBytes = files.Sum(item => item.File.Exists ? item.File.Length : 0L);
            foreach (var item in files.ToList())
            {
                if (totalBytes <= maximumBytes)
                    break;
                var size = item.File.Exists ? item.File.Length : 0L;
                if (!TryDelete(item.File))
                    continue;
                totalBytes -= size;
                files.Remove(item);
            }

            Interlocked.Exchange(ref _cacheBytes, Math.Max(0, totalBytes));
            Volatile.Write(ref _cacheFileCount, files.Count);
        }
        catch (Exception ex)
        {
            _diagnostics.Increment("voice.cache.maintenance.failed");
            _logger.LogWarning(ex, "Voice cache maintenance failed");
        }
    }

    private void RemoveStaleWorkFiles(string directory)
    {
        var threshold = DateTime.UtcNow.AddDays(-1);
        foreach (var pattern in new[] { "*.tmp", "*.partial" })
        {
            foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                var file = new FileInfo(path);
                if (file.LastWriteTimeUtc < threshold)
                    TryDelete(file);
            }
        }

        var work = Path.Combine(directory, ".work");
        if (!Directory.Exists(work))
            return;
        foreach (var child in new DirectoryInfo(work).EnumerateDirectories())
        {
            if (child.LastWriteTimeUtc >= threshold)
                continue;
            try
            {
                child.Delete(recursive: true);
                _diagnostics.Increment("voice.cache.evicted");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Skipped busy voice work directory {Path}", child.FullName);
            }
        }
    }

    private bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            _diagnostics.Increment("voice.cache.evicted");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Skipped busy voice cache file {Path}", file.FullName);
            return false;
        }
    }

    private string ResolveCacheDirectory()
    {
        if (Path.IsPathRooted(_options.CacheDirectory))
            return Path.GetFullPath(_options.CacheDirectory);
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _options.CacheDirectory));
    }

    private static DateTime LastUse(FileInfo file) =>
        file.LastAccessTimeUtc > file.LastWriteTimeUtc ? file.LastAccessTimeUtc : file.LastWriteTimeUtc;

    private sealed record CacheFile(FileInfo File, DateTime LastUseUtc);
}
