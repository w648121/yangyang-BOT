using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Performs a bounded, local-only inspection of sticker files. This is deliberately
/// metadata and pixel-statistics only: QQ images are never uploaded to a cloud vision
/// service and no semantic description is invented from them.
/// </summary>
public sealed class LocalImageInspector
{
    private readonly StickerVisionOptions _options;
    private readonly ILogger<LocalImageInspector> _logger;

    public LocalImageInspector(
        IOptions<StickerVisionOptions> options,
        ILogger<LocalImageInspector> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public LocalImageInspection? Inspect(string imagePath)
    {
        if (!_options.Enabled || !OperatingSystem.IsWindowsVersionAtLeast(6, 1) || !File.Exists(imagePath))
            return null;

        try
        {
            var info = new FileInfo(imagePath);
            if (info.Length <= 0 || info.Length > Math.Clamp(_options.MaxInspectionBytes, 256 * 1024, 64 * 1024 * 1024))
                return null;

            using var image = Image.FromFile(imagePath, useEmbeddedColorManagement: false);
            var pixels = (long)image.Width * image.Height;
            if (image.Width <= 0 || image.Height <= 0 || pixels > Math.Clamp(_options.MaxInspectionPixels, 1_000_000, 80_000_000))
                return null;

            var frames = GetFrameCount(image);
            var tone = GetDominantTone(image);
            var shape = image.Width == image.Height
                ? "square"
                : image.Width > image.Height ? "landscape" : "portrait";
            return new LocalImageInspection(
                Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant(),
                image.Width,
                image.Height,
                Math.Max(1, frames),
                frames > 1,
                shape,
                tone);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Local image inspection skipped for {Path}", imagePath);
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int GetFrameCount(Image image)
    {
        if (image.FrameDimensionsList.Length == 0)
            return 1;

        try
        {
            return image.GetFrameCount(new FrameDimension(image.FrameDimensionsList[0]));
        }
        catch (ArgumentException)
        {
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string GetDominantTone(Image image)
    {
        using var sample = new Bitmap(12, 12, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(sample))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(image, new Rectangle(0, 0, sample.Width, sample.Height));
        }

        long red = 0;
        long green = 0;
        long blue = 0;
        for (var y = 0; y < sample.Height; y++)
        for (var x = 0; x < sample.Width; x++)
        {
            var pixel = sample.GetPixel(x, y);
            red += pixel.R;
            green += pixel.G;
            blue += pixel.B;
        }

        var count = sample.Width * sample.Height;
        var average = (red + green + blue) / (3d * count);
        if (average < 60)
            return "dark";
        if (average > 205)
            return "bright";
        if (red > blue * 1.18 && red > green * 1.08)
            return "warm";
        if (blue > red * 1.12)
            return "cool";
        return "neutral";
    }
}

public sealed record LocalImageInspection(
    string Format,
    int Width,
    int Height,
    int FrameCount,
    bool IsAnimated,
    string Shape,
    string DominantTone)
{
    public string ToCompactText() =>
        $"{Format} {Width}x{Height}, {Shape}, {(IsAnimated ? $"{FrameCount} frames" : "static")}, {DominantTone} tone";
}
