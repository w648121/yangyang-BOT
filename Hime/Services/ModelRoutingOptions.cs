namespace Hime.Services;

/// <summary>
/// Keeps ordinary chat on the low-latency model and reserves the stronger model
/// for technical or genuinely multi-step requests.
/// </summary>
public sealed class ModelRoutingOptions
{
    public bool Enabled { get; set; } = true;

    public bool UseHighCapabilityForTechnical { get; set; } = true;

    /// <summary>Use the stronger model for nuanced social or emotional context, not for every greeting.</summary>
    public bool UseHighCapabilityForComplexSocial { get; set; } = true;

    public int ComplexSocialPromptMinCharacters { get; set; } = 70;

    public int ComplexPromptMinCharacters { get; set; } = 120;

    public string HighCapabilityProviderId { get; set; } = "hime-minimax";

    public string HighCapabilityModelId { get; set; } = "MiniMax-M3";
}
