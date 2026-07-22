using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Hime.Services;

/// <summary>使用 ONNX Model Zoo FER+ 在本机分析图片中心区域的面部表情。</summary>
public sealed class OnnxStickerEmotionClassifier : IDisposable
{
    private static readonly string[] Labels =
    [
        "neutral", "happy", "surprised", "sad",
        // FER+ has eight face-expression classes. The last three are normalized
        // to the closest supported reply emotions; they are weak hints only.
        "angry", "angry", "surprised", "serious"
    ];
    private static readonly object ResolverSync = new();
    private static bool _resolverConfigured;

    private readonly StickerVisionOptions _options;
    private readonly ILogger<OnnxStickerEmotionClassifier> _logger;
    private readonly object _sync = new();
    private InferenceSession? _session;
    private bool _initializationAttempted;

    public OnnxStickerEmotionClassifier(
        IOptions<StickerVisionOptions> options,
        ILogger<OnnxStickerEmotionClassifier> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public StickerVisionResult? Analyze(string imagePath)
    {
        if (!_options.Enabled || !OperatingSystem.IsWindows() || !File.Exists(imagePath))
            return null;

        lock (_sync)
        {
            try
            {
                var session = GetSession();
                if (session is null)
                    return null;

                var tensor = BuildInputTensor(imagePath);
                var inputName = session.InputMetadata.Keys.Single();
                using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
                var scores = results.First().AsEnumerable<float>().Take(Labels.Length).ToArray();
                if (scores.Length != Labels.Length)
                    return null;

                var probabilities = Softmax(scores);
                var bestIndex = Array.IndexOf(probabilities, probabilities.Max());
                var confidence = probabilities[bestIndex];
                if (confidence < Math.Clamp(_options.ConfidenceThreshold, 0, 1))
                    return null;

                return new StickerVisionResult(Labels[bestIndex], confidence);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "本地 ONNX 表情分析失败 (Path={Path})", imagePath);
                return null;
            }
        }
    }

    private InferenceSession? GetSession()
    {
        if (_session is not null || _initializationAttempted)
            return _session;

        _initializationAttempted = true;
        ConfigureNativeResolver();
        var modelPath = Path.IsPathRooted(_options.ModelPath)
            ? _options.ModelPath
            : Path.Combine(AppContext.BaseDirectory, _options.ModelPath);
        if (!File.Exists(modelPath))
        {
            _logger.LogWarning("本地表情模型不存在：{ModelPath}", modelPath);
            return null;
        }

        _session = new InferenceSession(modelPath);
        _logger.LogInformation("已加载本地 FER+ 表情模型：{ModelPath}", modelPath);
        return _session;
    }

    private static void ConfigureNativeResolver()
    {
        lock (ResolverSync)
        {
            if (_resolverConfigured)
                return;

            NativeLibrary.SetDllImportResolver(
                typeof(InferenceSession).Assembly,
                static (libraryName, assembly, searchPath) => ResolveOnnxRuntime(libraryName, assembly, searchPath));
            _resolverConfigured = true;
        }
    }

    private static nint ResolveOnnxRuntime(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("onnxruntime", StringComparison.OrdinalIgnoreCase) &&
            !libraryName.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
        {
            return nint.Zero;
        }

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException(
                $"不支持的 ONNX Runtime 进程架构：{RuntimeInformation.ProcessArchitecture}")
        };
        var nativePath = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            architecture,
            "native",
            "onnxruntime.dll");

        return File.Exists(nativePath)
            ? NativeLibrary.Load(nativePath)
            : NativeLibrary.Load(libraryName, assembly, searchPath);
    }

    [SupportedOSPlatform("windows")]
    private static DenseTensor<float> BuildInputTensor(string imagePath)
    {
        using var source = new Bitmap(imagePath);
        using var resized = new Bitmap(64, 64, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var side = Math.Min(source.Width, source.Height);
            var sourceRect = new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side);
            graphics.DrawImage(source, new Rectangle(0, 0, 64, 64), sourceRect, GraphicsUnit.Pixel);
        }

        var tensor = new DenseTensor<float>([1, 1, 64, 64]);
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var pixel = resized.GetPixel(x, y);
                tensor[0, 0, y, x] = 0.299f * pixel.R + 0.587f * pixel.G + 0.114f * pixel.B;
            }
        }
        return tensor;
    }

    private static double[] Softmax(IReadOnlyList<float> scores)
    {
        var max = scores.Max();
        var values = scores.Select(score => Math.Exp(score - max)).ToArray();
        var sum = values.Sum();
        return values.Select(value => value / sum).ToArray();
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}

public sealed record StickerVisionResult(string Emotion, double Confidence);
