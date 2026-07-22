using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Core.Enums;
using Sora.Entities.Message;
using Sora.Entities.Segments;

namespace Hime.Services;

/// <summary>
/// 把 Milky 消息中的临时图片下载到本地，并返回可长期持久化的绝对路径。
/// </summary>
public sealed class IncomingImageStore
{
    private readonly ImageOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<IncomingImageStore> _logger;
    private readonly string _rootDirectory;
    private readonly SemaphoreSlim _downloadSlots;
    private readonly object _contentStoreSync = new();
    private readonly ConcurrentDictionary<string, string> _sessionCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new(StringComparer.Ordinal);
    private DateTime _lastStickerCleanupAt = DateTime.MinValue;

    public IncomingImageStore(
        IOptions<ImageOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<IncomingImageStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = httpClientFactory.CreateClient("ImageDownloader");
        _downloadSlots = new SemaphoreSlim(Math.Clamp(_options.DownloadConcurrency, 1, 8));
        _rootDirectory = Path.IsPathRooted(_options.IncomingDirectory)
            ? _options.IncomingDirectory
            : Path.Combine(AppContext.BaseDirectory, _options.IncomingDirectory);
    }

    public async Task<IReadOnlyList<string>> ArchiveAsync(
        MessageBody? body,
        long userId,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        if (body is null)
            return Array.Empty<string>();

        var images = body.OfType<ImageSegment>().ToList();
        return await ArchiveImagesAsync(images, userId, groupId, "messages", cancellationToken);
    }

    /// <summary>
    /// Archives only regular pictures and screenshots. Stickers stay on their
    /// separate collection path, so ordinary image understanding never affects
    /// the sticker statistics or automatic sticker replies.
    /// </summary>
    public async Task<IReadOnlyList<string>> ArchiveOrdinaryImagesAsync(
        MessageBody? body,
        long userId,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        if (body is null)
            return Array.Empty<string>();

        var images = body.OfType<ImageSegment>()
            .Where(image => image.SubType != ImageSubType.Sticker)
            .ToList();
        return await ArchiveImagesAsync(images, userId, groupId, "messages", cancellationToken);
    }

    /// <summary>
    /// 仅归档 QQ 协议明确标记为 Sticker 的图片。群表情统计不得收集普通照片或截图。
    /// </summary>
    public async Task<IReadOnlyList<string>> ArchiveStickersAsync(
        MessageBody? body,
        long userId,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        var stickers = body?.OfType<ImageSegment>()
            .Where(image => image.SubType == ImageSubType.Sticker)
            .ToList()
            ?? [];
        var paths = await ArchiveImagesAsync(stickers, userId, groupId, "stickers", cancellationToken);
        CleanupExpiredStickerArchives();
        return paths;
    }

    private async Task<IReadOnlyList<string>> ArchiveImagesAsync(
        IReadOnlyList<ImageSegment> images,
        long userId,
        long? groupId,
        string category,
        CancellationToken cancellationToken)
    {
        if (images.Count == 0)
            return Array.Empty<string>();

        var tasks = images.Select(async image =>
        {
            try
            {
                return await ArchiveOneAsync(image, userId, groupId, category, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "归档聊天图片失败 (UserId={UserId}, GroupId={GroupId}, ResourceId={ResourceId})",
                    userId,
                    groupId,
                    image.ResourceId);
                return null;
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        return results.Where(path => path is not null).Select(path => path!).ToList();
    }

    private async Task<string?> ArchiveOneAsync(
        ImageSegment image,
        long userId,
        long? groupId,
        string category,
        CancellationToken cancellationToken)
    {
        var source = !string.IsNullOrWhiteSpace(image.Url) ? image.Url : image.FileUri;
        if (string.IsNullOrWhiteSpace(source))
            return null;

        var cacheKey = $"{image.ResourceId}:{source}";
        if (_sessionCache.TryGetValue(cacheKey, out var cached) && File.Exists(cached))
            return cached;

        var lazy = new Lazy<Task<string?>>(
            () => ArchiveOneCoreAsync(source, cacheKey, category, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var operation = _inflight.GetOrAdd(cacheKey, lazy);
        try
        {
            return await operation.Value;
        }
        finally
        {
            _inflight.TryRemove(cacheKey, out _);
        }
    }

    private async Task<string?> ArchiveOneCoreAsync(
        string source,
        string cacheKey,
        string category,
        CancellationToken cancellationToken)
    {
        await _downloadSlots.WaitAsync(cancellationToken);
        try
        {
            if (_sessionCache.TryGetValue(cacheKey, out var cached) && File.Exists(cached))
                return cached;

            // QQ resource IDs and download URLs change when the same image is
            // resent. Stage the bytes first, then use their SHA-256 as the
            // canonical name so duplicate messages share one stored file.
            var stagingDirectory = Path.Combine(_rootDirectory, category, ".incoming");
            Directory.CreateDirectory(stagingDirectory);
            string temporaryPath;
            string extension;

            if (TryGetLocalPath(source, out var localPath))
            {
                if (new FileInfo(localPath).Length > _options.MaxDownloadBytes)
                    throw new InvalidOperationException($"图片超过限制 {_options.MaxDownloadBytes} 字节。");
                extension = GetExtensionFromSource(source) ?? ".jpg";
                temporaryPath = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}{extension}.download");
                File.Copy(localPath, temporaryPath, overwrite: false);
            }
            else
            {
                var download = await DownloadToTemporaryAsync(source, stagingDirectory, cancellationToken);
                temporaryPath = download.Path;
                extension = download.Extension;
            }

            string destination;
            lock (_contentStoreSync)
                destination = StoreContentAddressedFile(temporaryPath, category, extension);

            _sessionCache[cacheKey] = destination;
            TrimSessionCache();
            return destination;
        }
        finally
        {
            _downloadSlots.Release();
        }
    }

    private void TrimSessionCache()
    {
        var limit = Math.Clamp(_options.SessionCacheLimit, 64, 16384);
        var remove = _sessionCache.Count - limit;
        if (remove <= 0)
            return;

        foreach (var key in _sessionCache.Keys.Take(remove))
            _sessionCache.TryRemove(key, out _);
    }

    private void CleanupExpiredStickerArchives()
    {
        if (_options.StickerArchiveRetentionDays <= 0 ||
            DateTime.UtcNow - _lastStickerCleanupAt < TimeSpan.FromHours(1))
            return;

        _lastStickerCleanupAt = DateTime.UtcNow;
        var stickerRoot = Path.Combine(_rootDirectory, "stickers");
        if (!Directory.Exists(stickerRoot))
            return;

        var cutoff = DateTime.UtcNow.AddDays(-_options.StickerArchiveRetentionDays);
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(stickerRoot, "*.*", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(path) >= cutoff)
                continue;

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Unable to expire raw sticker cache file {Path}", path);
            }
        }

        if (removed > 0)
            _logger.LogInformation("Expired {Count} raw sticker cache files", removed);
    }

    private async Task<(string Path, string Extension)> DownloadToTemporaryAsync(
        string source,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("图片地址不是受支持的 HTTP(S) URL。");
        }

        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > _options.MaxDownloadBytes)
            throw new InvalidOperationException($"图片大小 {contentLength} 超过限制 {_options.MaxDownloadBytes} 字节。");

        var extension = GetExtensionFromContentType(response.Content.Headers.ContentType?.MediaType)
            ?? GetExtensionFromSource(source)
            ?? ".jpg";
        var temporary = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}{extension}.download");

        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                total += read;
                if (total > _options.MaxDownloadBytes)
                    throw new InvalidOperationException($"图片超过限制 {_options.MaxDownloadBytes} 字节。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            output.Close();
            return (temporary, extension);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }
    }

    private string StoreContentAddressedFile(string temporaryPath, string category, string extension)
    {
        try
        {
            var contentHash = ComputeSha256(temporaryPath).ToLowerInvariant();
            var blobDirectory = Path.Combine(_rootDirectory, category, "blobs");
            Directory.CreateDirectory(blobDirectory);

            // A malformed URL suffix or a different MIME header must not create
            // another copy of the same bytes. The hash is authoritative.
            var existing = Directory.EnumerateFiles(blobDirectory, contentHash + ".*")
                .FirstOrDefault();
            if (existing is not null)
            {
                File.Delete(temporaryPath);
                return existing;
            }

            var destination = Path.Combine(blobDirectory, contentHash + extension.ToLowerInvariant());
            File.Move(temporaryPath, destination, overwrite: false);
            _logger.LogDebug("Archived image as content-addressed blob {Path}", destination);
            return destination;
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private string? GetExtensionFromSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return NormalizeExtension(Path.GetExtension(source));
        return NormalizeExtension(Path.GetExtension(uri.IsFile ? uri.LocalPath : uri.AbsolutePath));
    }

    private string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;
        return _options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? extension.ToLowerInvariant()
            : null;
    }

    private static string? GetExtensionFromContentType(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => null
    };

    private static bool TryGetLocalPath(string source, out string localPath)
    {
        localPath = string.Empty;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
            localPath = uri.LocalPath;
        else if (Path.IsPathRooted(source))
            localPath = source;

        return !string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath);
    }
}
