using System.Text.Json.Serialization;

namespace Hime.Services;

/// <summary>A dynamic, multi-label sticker query produced by the conversation agent.</summary>
public sealed record StickerSearchRequest
{
    public Dictionary<string, double> Emotions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SemanticTags { get; init; } = [];
    public List<string> IntentTags { get; init; } = [];
    public int Count { get; init; } = 1;
}

/// <summary>Safe search result. Local paths are never serialized to OpenCode.</summary>
public sealed record StickerSearchCandidate
{
    public string StickerId { get; init; } = string.Empty;
    public double Score { get; init; }
    public string MatchLevel { get; init; } = "none";
    public List<string> MatchedEmotions { get; init; } = [];
    public List<string> MatchedTags { get; init; } = [];
    public List<string> MatchedIntents { get; init; } = [];

    [JsonIgnore]
    public string Path { get; init; } = string.Empty;
}

public sealed record OpenCodeStickerCatalogSnapshot
{
    public int Version { get; init; } = StickerTagCatalog.CurrentCatalogVersion;
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public double StrongMatchThreshold { get; init; }
    public double PartialMatchThreshold { get; init; }
    public List<string> NeutralFallbackTags { get; init; } = [];
    public List<OpenCodeStickerCatalogItem> Stickers { get; init; } = [];
}

public sealed record OpenCodeStickerCatalogItem
{
    public string StickerId { get; init; } = string.Empty;
    public Dictionary<string, double> Emotions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SemanticTags { get; init; } = [];
    public List<string> IntentTags { get; init; } = [];
}
