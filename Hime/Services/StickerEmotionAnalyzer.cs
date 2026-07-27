namespace Hime.Services;

/// <summary>
/// 本地表情情绪分析器。不会上传群图片或聊天文本；综合已有文件名、
/// ONNX 视觉结果，以及图片随附文字/Emoji 做稳定分类。
/// </summary>
public sealed class StickerEmotionAnalyzer
{
    private readonly AnimeStickerTagger _animeTagger;
    private readonly OnnxStickerEmotionClassifier _visionClassifier;
    private readonly LocalImageInspector _imageInspector;
    private readonly StickerLabelVocabulary _labels;

    public StickerEmotionAnalyzer(
        AnimeStickerTagger animeTagger,
        OnnxStickerEmotionClassifier visionClassifier,
        LocalImageInspector imageInspector,
        StickerLabelVocabulary labels)
    {
        _animeTagger = animeTagger;
        _visionClassifier = visionClassifier;
        _imageInspector = imageInspector;
        _labels = labels;
    }

    public StickerEmotionEvidence Analyze(string imagePath, string? contextText)
    {
        var inspection = _imageInspector.Inspect(imagePath);
        var fromName = TryReadEmotionFromFileName(imagePath);
        if (fromName is not null)
            return new StickerEmotionEvidence(
                fromName,
                5,
                "filename",
                null,
                inspection,
                EmotionScores: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [fromName] = 1.0 });

        var fromAnime = _animeTagger.Analyze(imagePath);
        if (fromAnime is not null)
        {
            var weight = Math.Clamp((int)Math.Round(fromAnime.Confidence * 5), 3, 5);
            return new StickerEmotionEvidence(
                fromAnime.Emotion,
                weight,
                "wdv3",
                fromAnime.Confidence,
                inspection,
                fromAnime.SemanticTags,
                fromAnime.EmotionScores,
                fromAnime.IntentTags);
        }

        var fromContext = AnalyzeContext(contextText);
        var fromVision = _visionClassifier.Analyze(imagePath);
        if (fromVision is not null)
        {
            if (fromContext != _labels.FallbackEmotion)
                return new StickerEmotionEvidence(
                    fromContext,
                    fromContext == fromVision.Emotion ? 5 : 3,
                    "context-over-ferplus",
                    fromVision.Confidence,
                    inspection,
                    EmotionScores: MergeEmotionScores(fromContext, fromVision));

            var weight = Math.Clamp((int)Math.Round(fromVision.Confidence * 2), 1, 2);
            return new StickerEmotionEvidence(
                fromVision.Emotion,
                weight,
                "weak-ferplus",
                fromVision.Confidence,
                inspection,
                EmotionScores: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    [fromVision.Emotion] = Math.Clamp(fromVision.Confidence, 0, 1)
                });
        }

        return fromContext == _labels.FallbackEmotion
            ? new StickerEmotionEvidence(
                _labels.FallbackEmotion,
                1,
                "fallback",
                null,
                inspection,
                EmotionScores: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [_labels.FallbackEmotion] = 1.0 })
            : new StickerEmotionEvidence(
                fromContext,
                2,
                "context",
                null,
                inspection,
                EmotionScores: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [fromContext] = 0.65 });
    }

    private string AnalyzeContext(string? contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
            return _labels.FallbackEmotion;

        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in _labels.All)
        {
            var terms = definition.Aliases
                .Append(definition.ChineseName)
                .Append(definition.Canonical)
                .Where(term => !string.IsNullOrWhiteSpace(term));
            foreach (var keyword in terms)
            {
                if (contextText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    scores[definition.BaseEmotion] =
                        scores.GetValueOrDefault(definition.BaseEmotion) + (keyword.Length == 1 ? 1 : 2);
                }
            }
        }

        return scores.Count == 0
            ? _labels.FallbackEmotion
            : scores.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
    }

    private string? TryReadEmotionFromFileName(string imagePath)
    {
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        var prefix = stem.Split(['_', '-'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return _labels.TryNormalizeBaseEmotion(prefix, out var emotion) ? emotion : null;
    }

    private static IReadOnlyDictionary<string, double> MergeEmotionScores(
        string contextEmotion,
        StickerVisionResult vision)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [contextEmotion] = 0.75
        };
        scores[vision.Emotion] = Math.Max(scores.GetValueOrDefault(vision.Emotion), Math.Clamp(vision.Confidence, 0, 1) * 0.55);
        return scores;
    }
}

public sealed record StickerEmotionEvidence(
    string Emotion,
    int Weight,
    string Source,
    double? VisualConfidence,
    LocalImageInspection? Inspection = null,
    IReadOnlyList<string>? SemanticTags = null,
    IReadOnlyDictionary<string, double>? EmotionScores = null,
    IReadOnlyList<string>? IntentTags = null);
