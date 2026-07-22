namespace Hime.Services;

/// <summary>Persistent metadata settings for the curated sticker semantic catalog.</summary>
public sealed class StickerTagOptions
{
    /// <summary>Local-only metadata file. It stores no chat text or remote image URL.</summary>
    public string CatalogPath { get; set; } = "data/sticker-tags.json";

    /// <summary>Upper bound for semantic tags stored for a single sticker.</summary>
    public int MaxTagsPerSticker { get; set; } = 12;

    /// <summary>How many unindexed curated stickers are analyzed in one background pass.</summary>
    public int BackfillBatchSize { get; set; } = 96;

    /// <summary>Delay before checking the curated library again for newly approved stickers.</summary>
    public int RefreshMinutes { get; set; } = 20;

    /// <summary>Minimum score for an unambiguous multi-label sticker match.</summary>
    public double StrongMatchThreshold { get; set; } = 0.72;

    /// <summary>
    /// Minimum score for a partial match. Partial matches are accepted only when
    /// the highest-priority requested emotion is present and no contradiction exists.
    /// </summary>
    public double PartialMatchThreshold { get; set; } = 0.55;

    /// <summary>Safe fallback tags used when no candidate reaches the partial threshold.</summary>
    public List<string> NeutralFallbackTags { get; set; } =
        ["neutral", "calm", "gentle", "expressionless", "quiet"];

    /// <summary>Metadata snapshot consumed by the restricted OpenCode sticker tool.</summary>
    public string OpenCodeSnapshotPath { get; set; } = "runtime/opencode-sticker-catalog.json";
}
