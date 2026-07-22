using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>Gradually analyzes approved stickers so startup and message handling stay responsive.</summary>
public sealed class StickerTagBackfillService : BackgroundService
{
    private readonly ImageService _images;
    private readonly AnimeStickerTagger _tagger;
    private readonly StickerTagCatalog _catalog;
    private readonly StickerTagOptions _options;
    private readonly OpenCodeStickerCatalogPublisher _publisher;
    private readonly ILogger<StickerTagBackfillService> _logger;

    public StickerTagBackfillService(
        ImageService images,
        AnimeStickerTagger tagger,
        StickerTagCatalog catalog,
        IOptions<StickerTagOptions> options,
        OpenCodeStickerCatalogPublisher publisher,
        ILogger<StickerTagBackfillService> logger)
    {
        _images = images;
        _tagger = tagger;
        _catalog = catalog;
        _options = options.Value;
        _publisher = publisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _images.Refresh();
                var pending = _images.AvailableImages.Values
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(path => !_catalog.IsIndexed(path))
                    .Take(Math.Clamp(_options.BackfillBatchSize, 1, 300))
                    .ToList();

                var indexed = 0;
                var unrecognized = 0;
                foreach (var path in pending)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    var existing = _catalog.GetEntry(path);
                    if (existing?.LabelSource.Equals("manual", StringComparison.OrdinalIgnoreCase) == true)
                        continue;

                    var inferred = _tagger.Analyze(path);
                    if (inferred is null)
                    {
                        _catalog.Remove(path, preserveManual: true);
                        unrecognized++;
                        continue;
                    }

                    _catalog.Upsert(
                        path,
                        inferred.Emotion,
                        inferred.EmotionScores,
                        inferred.SemanticTags,
                        inferred.IntentTags,
                        labelSource: "model");
                    indexed++;
                    await Task.Delay(25, stoppingToken);
                }

                if (indexed > 0 || unrecognized > 0)
                {
                    _logger.LogInformation(
                        "Indexed {Count} curated stickers with multi-frame WDv3 tags; {Unrecognized} remain unrecognized",
                        indexed,
                        unrecognized);
                    _publisher.Publish();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Curated sticker semantic backfill failed; it will retry later");
            }

            var minutes = Math.Clamp(_options.RefreshMinutes, 2, 120);
            await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
        }
    }
}
