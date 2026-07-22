namespace Hime.Services;

/// <summary>
/// Settings for the local-only Ollama visual analysis service.
/// </summary>
public sealed class OllamaVisionOptions
{
    public bool Enabled { get; set; } = true;

    public string Endpoint { get; set; } = "http://127.0.0.1:11434";

    public string Model { get; set; } = "qwen3-vl:4b";

    public int TimeoutSeconds { get; set; } = 90;

    public int MaxImagesPerMessage { get; set; } = 1;

    public long MaxImageBytes { get; set; } = 8 * 1024 * 1024;

    public int MaxDescriptionCharacters { get; set; } = 900;
}
