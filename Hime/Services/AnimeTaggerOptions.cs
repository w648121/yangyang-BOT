namespace Hime.Services;

/// <summary>Configuration for the local WDv3 anime-image tagger.</summary>
public sealed class AnimeTaggerOptions
{
    public bool Enabled { get; set; } = true;

    public string ModelPath { get; set; } = "models/wd-vit-tagger-v3/model.onnx";

    public string TagsPath { get; set; } = "models/wd-vit-tagger-v3/selected_tags.csv";

    /// <summary>Rejects interrupted downloads before ONNX Runtime tries to load them.</summary>
    public long MinimumModelBytes { get; set; } = 50L * 1024 * 1024;

    public double TagConfidenceThreshold { get; set; } = 0.30;

    public int MaximumSemanticTags { get; set; } = 6;

    public Dictionary<string, List<string>> EmotionTagMap { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> IntentTagMap { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsValid() =>
        !Enabled ||
        (!string.IsNullOrWhiteSpace(ModelPath) &&
         !string.IsNullOrWhiteSpace(TagsPath) &&
         MinimumModelBytes >= 0 &&
         TagConfidenceThreshold is > 0 and <= 1 &&
         MaximumSemanticTags > 0 &&
         EmotionTagMap.Count > 0 &&
         EmotionTagMap.All(pair =>
             !string.IsNullOrWhiteSpace(pair.Key) &&
             pair.Value.Any(tag => !string.IsNullOrWhiteSpace(tag))) &&
         IntentTagMap.All(pair =>
             !string.IsNullOrWhiteSpace(pair.Key) &&
             pair.Value.Any(tag => !string.IsNullOrWhiteSpace(tag))));
}

public sealed record AnimeTagResult(
    string Emotion,
    double Confidence,
    IReadOnlyList<string> SemanticTags,
    IReadOnlyDictionary<string, double> EmotionScores,
    IReadOnlyList<string> IntentTags,
    int AnalyzedFrames = 1,
    int TotalFrames = 1);
