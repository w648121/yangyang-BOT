using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Hime.Services;

/// <summary>
/// Runs a WDv3/Danbooru-style ONNX tagger locally. Unlike a face-expression model,
/// this understands anime-style visual tags such as smile, crying, blush, or smug.
/// Only a small safe allow-list of expression tags is retained for the bot context.
/// </summary>
public sealed class AnimeStickerTagger : IDisposable
{
    private readonly AnimeTaggerOptions _options;
    private readonly ILogger<AnimeStickerTagger> _logger;
    private readonly object _sync = new();
    private InferenceSession? _session;
    private IReadOnlyList<WdTag> _tags = [];
    private long _lastReportedModelLength = -1;
    private bool _missingTagsReported;

    public AnimeStickerTagger(
        IOptions<AnimeTaggerOptions> options,
        ILogger<AnimeStickerTagger> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public AnimeTagResult? Analyze(string imagePath)
    {
        if (!_options.Enabled || !OperatingSystem.IsWindows() || !File.Exists(imagePath))
            return null;

        lock (_sync)
        {
            try
            {
                var session = GetSession();
                if (session is null || _tags.Count == 0)
                    return null;

                var inputName = session.InputMetadata.Keys.Single();
                var dimensions = session.InputMetadata.Values.Single().Dimensions;
#pragma warning disable CA1416
                using var source = Image.FromFile(imagePath);
                var totalFrames = GetFrameCount(source);
                var frameIndexes = SelectRepresentativeFrames(totalFrames);
                var frameScores = new List<double[]>(frameIndexes.Count);
                var frameDimension = source.FrameDimensionsList.Length > 0
                    ? new FrameDimension(source.FrameDimensionsList[0])
                    : null;

                foreach (var frameIndex in frameIndexes)
                {
                    if (frameDimension is not null && totalFrames > 1)
                        source.SelectActiveFrame(frameDimension, frameIndex);
                    using var frame = CopyActiveFrame(source);
                    var input = BuildInputTensor(frame, dimensions);
                    using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
                    var frameOutputScores = results.First().AsEnumerable<float>().Take(_tags.Count).Select(value => (double)value).ToArray();
                    if (frameOutputScores.Length != _tags.Count)
                        continue;

                    // WDv3 normally emits sigmoid probabilities. Keep compatibility with
                    // exported variants that expose logits instead.
                    if (frameOutputScores.Any(score => score is < 0 or > 1))
                        frameOutputScores = frameOutputScores.Select(Sigmoid).ToArray();
                    frameScores.Add(frameOutputScores);
                }
#pragma warning restore CA1416

                if (frameScores.Count == 0)
                    return null;

                // A short-lived expression should still be discoverable, while a tag
                // present throughout the animation receives a stability bonus.
                var scores = Enumerable.Range(0, _tags.Count)
                    .Select(index =>
                    {
                        var values = frameScores.Select(frame => frame[index]).ToList();
                        return Math.Clamp(values.Max() * 0.65 + values.Average() * 0.35, 0, 1);
                    })
                    .ToArray();

                var threshold = Math.Clamp(_options.TagConfidenceThreshold, 0.05, 0.95);
                var detected = _tags
                    .Zip(scores)
                    .Where(pair => pair.First.Category == 0 && pair.Second >= threshold)
                    .Select(pair => new ScoredTag(pair.First.Name, pair.Second))
                    .ToList();

                var emotionTags = NormalizeTagMap(_options.EmotionTagMap);
                var semantic = detected
                    .Where(item => emotionTags.Values.SelectMany(tags => tags)
                        .Contains(item.Name, StringComparer.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.Score)
                    .Take(Math.Clamp(_options.MaximumSemanticTags, 1, 12))
                    .ToList();
                if (semantic.Count == 0)
                    return null;

                // Multi-label probabilities are independent: a blushing smile can be
                // both shy and happy. They intentionally do not sum to one.
                var emotionScores = emotionTags
                    .Select(rule => new
                    {
                        Emotion = rule.Key,
                        Score = CombineIndependentScores(semantic
                            .Where(tag => rule.Value.Contains(tag.Name, StringComparer.OrdinalIgnoreCase))
                            .Select(tag => tag.Score))
                    })
                    .Where(item => item.Score > 0)
                    .ToDictionary(
                        item => item.Emotion,
                        item => Math.Round(item.Score, 4),
                        StringComparer.OrdinalIgnoreCase);
                var emotion = emotionScores
                    .OrderByDescending(item => item.Value)
                    .ThenBy(item => item.Key == "neutral" ? 1 : 0)
                    .First();
                if (emotion.Value <= 0)
                    return null;

                return new AnimeTagResult(
                    emotion.Key,
                    Math.Clamp(emotion.Value, 0, 1),
                    semantic.Select(item => item.Name).ToList(),
                    emotionScores,
                    InferIntentTags(semantic.Select(item => item.Name), _options.IntentTagMap),
                    frameScores.Count,
                    totalFrames);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WDv3 anime tagger failed for {Path}", imagePath);
                return null;
            }
        }
    }

    private InferenceSession? GetSession()
    {
        if (_session is not null)
            return _session;

        var modelPath = ResolvePath(_options.ModelPath);
        var tagsPath = ResolvePath(_options.TagsPath);
        var modelLength = File.Exists(modelPath) ? new FileInfo(modelPath).Length : 0;
        if (modelLength < _options.MinimumModelBytes)
        {
            if (_lastReportedModelLength != modelLength)
            {
                _lastReportedModelLength = modelLength;
                _logger.LogInformation(
                    "WDv3 anime tagger is waiting for a complete model file at {Path}; current size is {ModelLength} bytes",
                    modelPath,
                    modelLength);
            }
            return null;
        }
        if (!File.Exists(tagsPath))
        {
            if (!_missingTagsReported)
            {
                _missingTagsReported = true;
                _logger.LogWarning("WDv3 anime tagger tag table is missing at {Path}", tagsPath);
            }
            return null;
        }

        var tags = LoadTags(tagsPath);
        if (tags.Count == 0)
        {
            _logger.LogWarning("WDv3 anime tagger tag table is empty or invalid at {Path}", tagsPath);
            return null;
        }

        _session = new InferenceSession(modelPath);
        _tags = tags;
        _logger.LogInformation("Loaded WDv3 anime tagger: {TagCount} tags from {ModelPath}", tags.Count, modelPath);
        return _session;
    }

    private static IReadOnlyList<WdTag> LoadTags(string path)
    {
        var lines = File.ReadLines(path).ToList();
        if (lines.Count < 2)
            return [];

        var header = lines[0].Split(',');
        var nameIndex = Array.FindIndex(header, value => value.Equals("name", StringComparison.OrdinalIgnoreCase));
        var categoryIndex = Array.FindIndex(header, value => value.Equals("category", StringComparison.OrdinalIgnoreCase));
        if (nameIndex < 0 || categoryIndex < 0)
            return [];

        return lines.Skip(1)
            .Select(line => line.Split(','))
            .Where(columns => columns.Length > Math.Max(nameIndex, categoryIndex))
            .Where(columns => int.TryParse(columns[categoryIndex], out _))
            .Select(columns => new WdTag(NormalizeTag(columns[nameIndex]), int.Parse(columns[categoryIndex])))
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Name))
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static int GetFrameCount(Image image)
    {
        if (image.FrameDimensionsList.Length == 0)
            return 1;
        try
        {
            return Math.Max(1, image.GetFrameCount(new FrameDimension(image.FrameDimensionsList[0])));
        }
        catch (ArgumentException)
        {
            return 1;
        }
    }

    private static IReadOnlyList<int> SelectRepresentativeFrames(int totalFrames) =>
        new[] { 0, Math.Max(0, (totalFrames - 1) / 2), Math.Max(0, totalFrames - 1) }
            .Distinct()
            .ToList();

    [SupportedOSPlatform("windows")]
    private static Bitmap CopyActiveFrame(Image source)
    {
        var frame = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(frame);
        graphics.Clear(Color.Transparent);
        graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
        return frame;
    }

    [SupportedOSPlatform("windows")]
    private static DenseTensor<float> BuildInputTensor(Bitmap source, IReadOnlyList<int> dimensions)
    {
        // WDv3 export uses a 448x448 NHWC image tensor. The dimension lookup keeps
        // this compatible with equivalent tagger exports that retain that layout.
        var height = dimensions.Count >= 3 && dimensions[^3] > 0 ? dimensions[^3] : 448;
        var width = dimensions.Count >= 2 && dimensions[^2] > 0 ? dimensions[^2] : 448;
        using var canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.White);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var scale = Math.Min(width / (double)source.Width, height / (double)source.Height);
            var targetWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
            var x = (width - targetWidth) / 2;
            var y = (height - targetHeight) / 2;
            graphics.DrawImage(source, new Rectangle(x, y, targetWidth, targetHeight));
        }

        var tensor = new DenseTensor<float>([1, height, width, 3]);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var pixel = canvas.GetPixel(x, y);
            // WD tagger models conventionally expect BGR values in the 0..255 range.
            tensor[0, y, x, 0] = pixel.B;
            tensor[0, y, x, 1] = pixel.G;
            tensor[0, y, x, 2] = pixel.R;
        }
        return tensor;
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        // During local development the model can finish downloading after the
        // executable has started. Prefer the project working directory so the
        // tagger can pick up that completed file without another build/restart.
        var workingDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), path);
        return File.Exists(workingDirectoryPath)
            ? workingDirectoryPath
            : Path.Combine(AppContext.BaseDirectory, path);
    }

    private static string NormalizeTag(string value) =>
        value.Trim().Trim('"').ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    private static double Sigmoid(double value) => 1d / (1d + Math.Exp(-value));

    private static double CombineIndependentScores(IEnumerable<double> scores)
    {
        var values = scores.Select(score => Math.Clamp(score, 0, 1)).ToList();
        return values.Count == 0 ? 0 : 1d - values.Aggregate(1d, (remaining, score) => remaining * (1d - score));
    }

    private static IReadOnlyList<string> InferIntentTags(
        IEnumerable<string> semanticTags,
        IReadOnlyDictionary<string, List<string>> intentTagMap)
    {
        var tags = semanticTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NormalizeTagMap(intentTagMap)
            .Where(rule => tags.Overlaps(rule.Value))
            .Select(rule => rule.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static Dictionary<string, List<string>> NormalizeTagMap(
        IReadOnlyDictionary<string, List<string>> map) =>
        map
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(
                pair => NormalizeTag(pair.Key),
                pair => pair.Value
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(NormalizeTag)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

    public void Dispose() => _session?.Dispose();

    private sealed record WdTag(string Name, int Category);
    private sealed record ScoredTag(string Name, double Score);
}
