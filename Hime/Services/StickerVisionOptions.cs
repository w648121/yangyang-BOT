namespace Hime.Services;

/// <summary>本地 ONNX 表情视觉分析配置。</summary>
public sealed class StickerVisionOptions
{
    public bool Enabled { get; set; } = true;

    public string ModelPath { get; set; } = "models/emotion-ferplus-8.onnx";

    public double ConfidenceThreshold { get; set; } = 0.35;

    public long MaxInspectionBytes { get; set; } = 20 * 1024 * 1024;

    public int MaxInspectionPixels { get; set; } = 40_000_000;
}
