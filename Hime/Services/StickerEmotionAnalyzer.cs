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

    public StickerEmotionAnalyzer(
        AnimeStickerTagger animeTagger,
        OnnxStickerEmotionClassifier visionClassifier,
        LocalImageInspector imageInspector)
    {
        _animeTagger = animeTagger;
        _visionClassifier = visionClassifier;
        _imageInspector = imageInspector;
    }

    private static readonly IReadOnlyDictionary<string, string[]> Keywords =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["happy"] = ["哈哈", "笑死", "笑", "好耶", "开心", "乐", "可爱", "喜欢", "牛", "草", "😂", "🤣", "😆", "😁", "😊", "😄"],
            ["shy"] = ["害羞", "脸红", "羞", "捂脸", "心动", "🥰", "😘", "😳", "🙈"],
            ["surprised"] = ["震惊", "卧槽", "我超", "什么", "不会吧", "竟然", "😱", "🤯", "😮", "😲", "!?", "？！"],
            ["embarrassed"] = ["尴尬", "绷不住", "裂开", "汗流", "脚趾", "蚌埠住", "😅", "🙃"],
            ["angry"] = ["生气", "气死", "愤怒", "恼火", "滚", "烦死", "无语", "😡", "🤬", "💢"],
            ["sad"] = ["难过", "悲伤", "哭", "呜呜", "寄了", "完了", "心碎", "😭", "😢", "🥲", "💔"],
            ["comforting"] = ["摸摸", "抱抱", "没事", "别怕", "加油", "安慰", "辛苦", "🫂", "🤗"],
            ["serious"] = ["认真", "严肃", "警告", "注意", "正经", "重要"],
            ["proud"] = ["得意", "骄傲", "厉害吧", "我真棒", "就这", "😎", "🏆"]
        };

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
            if (fromContext != "neutral")
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

        return fromContext == "neutral"
            ? new StickerEmotionEvidence(
                "neutral",
                1,
                "fallback",
                null,
                inspection,
                EmotionScores: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["neutral"] = 1.0 })
            : new StickerEmotionEvidence(
                fromContext,
                2,
                "context",
                null,
                inspection,
                EmotionScores: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [fromContext] = 0.65 });
    }

    private static string AnalyzeContext(string? contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
            return "neutral";

        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (emotion, keywords) in Keywords)
        {
            foreach (var keyword in keywords)
            {
                if (contextText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    scores[emotion] = scores.GetValueOrDefault(emotion) + (keyword.Length == 1 ? 1 : 2);
            }
        }

        return scores.Count == 0
            ? "neutral"
            : scores.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
    }

    private static string? TryReadEmotionFromFileName(string imagePath)
    {
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        var prefix = stem.Split(['_', '-'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return prefix is not null && ImageService.CanonicalEmotions.Contains(prefix, StringComparer.OrdinalIgnoreCase)
            ? prefix.ToLowerInvariant()
            : null;
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
