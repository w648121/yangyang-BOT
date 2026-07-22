namespace Hime.Services;

/// <summary>
/// A bounded, application-selected model override. It is never built from user
/// input, so a QQ message cannot select a provider or a more expensive model.
/// </summary>
public sealed record AiRequestProfile(string? ProviderId = null, string? ModelId = null)
{
    public static AiRequestProfile Default { get; } = new();

    public bool HasExplicitModel =>
        !string.IsNullOrWhiteSpace(ProviderId) && !string.IsNullOrWhiteSpace(ModelId);
}
