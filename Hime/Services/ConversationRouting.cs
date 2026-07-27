using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Selects a response policy before a message reaches the persona model. This is
/// deliberately deterministic: a user cannot make a casual message become a
/// privileged or technical mode through prompt wording.
/// </summary>
public sealed class ConversationRouter
{
    private readonly IOptionsMonitor<ModelRoutingOptions> _modelRouting;
    private readonly IOptionsMonitor<ResponsePolicyOptions> _responsePolicies;

    public ConversationRouter(
        IOptionsMonitor<ModelRoutingOptions> modelRouting,
        IOptionsMonitor<ResponsePolicyOptions> responsePolicies)
    {
        _modelRouting = modelRouting;
        _responsePolicies = responsePolicies;
    }

    public ConversationRoute Route(string prompt)
    {
        var text = (prompt ?? string.Empty).Trim();
        var normalized = text.ToLowerInvariant();
        var options = _modelRouting.CurrentValue;

        if (options.TimeMarkers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Any(marker => normalized.Contains(marker.Trim(), StringComparison.Ordinal)))
            return new ConversationRoute(ConversationMode.Factual, "时间或日期查询", AllowDecorativeMedia: false);

        if (options.TechnicalMarkers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Any(marker => normalized.Contains(marker.Trim(), StringComparison.Ordinal)))
            return new ConversationRoute(ConversationMode.Technical, "程序、能力或技术问题", AllowDecorativeMedia: false);

        return new ConversationRoute(ConversationMode.Casual, "普通聊天", AllowDecorativeMedia: true);
    }

    /// <summary>
    /// Returns only a configured allow-list model. Ordinary conversation continues
    /// to use the OpenCode agent default (currently V4 Pro).
    /// </summary>
    public AiRequestProfile SelectModel(ConversationRoute route, string prompt)
    {
        var options = _modelRouting.CurrentValue;
        if (!options.Enabled ||
            string.IsNullOrWhiteSpace(options.HighCapabilityProviderId) ||
            string.IsNullOrWhiteSpace(options.HighCapabilityModelId))
        {
            return AiRequestProfile.Default;
        }

        var normalized = (prompt ?? string.Empty).Trim().ToLowerInvariant();
        var technical = options.UseHighCapabilityForTechnical && route.Mode == ConversationMode.Technical;
        var complex = normalized.Length >= Math.Clamp(options.ComplexPromptMinCharacters, 40, 2000) &&
                      options.ComplexMarkers
                          .Where(marker => !string.IsNullOrWhiteSpace(marker))
                          .Any(marker => normalized.Contains(marker.Trim(), StringComparison.Ordinal));
        var socialMarkerCount = options.SocialComplexityMarkers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Count(marker => normalized.Contains(marker.Trim(), StringComparison.Ordinal));
        var complexSocial = options.UseHighCapabilityForComplexSocial &&
                            route.Mode == ConversationMode.Casual &&
                            socialMarkerCount > 0 &&
                            (normalized.Length >= Math.Clamp(options.ComplexSocialPromptMinCharacters, 20, 500) ||
                             socialMarkerCount >= 2);
        return technical || complex || complexSocial
            ? new AiRequestProfile(options.HighCapabilityProviderId, options.HighCapabilityModelId)
            : AiRequestProfile.Default;
    }

    public string BuildSystemPolicy(ConversationRoute route)
    {
        var policies = _responsePolicies.CurrentValue;
        var lines = route.Mode switch
        {
            ConversationMode.Factual => policies.Factual,
            ConversationMode.Technical => policies.Technical,
            _ => policies.Casual
        };
        return string.Join(
            Environment.NewLine,
            lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()));
    }
}

/// <summary>
/// Hot-reloadable content constraints for each conversation route. Accuracy
/// changes what may be claimed, never which persona is speaking.
/// </summary>
public sealed class ResponsePolicyOptions
{
    public List<string> Casual { get; set; } = [];
    public List<string> Factual { get; set; } = [];
    public List<string> Technical { get; set; } = [];

    public bool IsValid() =>
        Casual.Any(line => !string.IsNullOrWhiteSpace(line)) &&
        Factual.Any(line => !string.IsNullOrWhiteSpace(line)) &&
        Technical.Any(line => !string.IsNullOrWhiteSpace(line));
}

public enum ConversationMode
{
    Casual,
    Factual,
    Technical
}

public sealed record ConversationRoute(ConversationMode Mode, string Reason, bool AllowDecorativeMedia);
