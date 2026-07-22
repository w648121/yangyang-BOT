using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Publishes a path-free, approved-only sticker snapshot for the restricted OpenCode
/// custom tool. The tool never reads Hime configuration or the actual image folders.
/// </summary>
public sealed class OpenCodeStickerCatalogPublisher
{
    private readonly ImageService _images;
    private readonly StickerTagOptions _options;
    private readonly ILogger<OpenCodeStickerCatalogPublisher> _logger;
    private readonly object _sync = new();

    public OpenCodeStickerCatalogPublisher(
        ImageService images,
        IOptions<StickerTagOptions> options,
        ILogger<OpenCodeStickerCatalogPublisher> logger)
    {
        _images = images;
        _options = options.Value;
        _logger = logger;
    }

    public string SnapshotPath => Path.IsPathRooted(_options.OpenCodeSnapshotPath)
        ? Path.GetFullPath(_options.OpenCodeSnapshotPath)
        : Path.GetFullPath(_options.OpenCodeSnapshotPath, AppContext.BaseDirectory);

    public void Publish()
    {
        lock (_sync)
        {
            try
            {
                _images.Refresh();
                var snapshot = _images.BuildOpenCodeSnapshot();
                var directory = Path.GetDirectoryName(SnapshotPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                var temporary = SnapshotPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
                File.Move(temporary, SnapshotPath, overwrite: true);
                _logger.LogInformation(
                    "Published {Count} approved stickers to the OpenCode tool snapshot.",
                    snapshot.Stickers.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not publish the OpenCode sticker tool snapshot.");
            }
        }
    }
}
