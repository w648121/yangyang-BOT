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

    public ConversationRouter(IOptionsMonitor<ModelRoutingOptions> modelRouting)
    {
        _modelRouting = modelRouting;
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

    public static string BuildSystemPolicy(ConversationRoute route) => route.Mode switch
    {
        ConversationMode.Factual =>
            """
            Response mode: verified factual answer.
            Answer in concise Simplified Chinese. Follow trusted runtime facts supplied by the application.
            Do not role-play, produce Japanese/Chinese bilingual formatting, emojis, images, voice markers, emotion markers, or memory markers.
            Never invent a fact. If a fact is unavailable, say so plainly.
            This runtime policy overrides persona formatting rules for this reply.
            """,
        ConversationMode.Technical =>
            """
            Response mode: serious technical answer.
            Answer in clear Simplified Chinese, with the conclusion first and short steps only when useful.
            Do not role-play, produce Japanese/Chinese bilingual formatting, emojis, images, voice markers, emotion markers, or memory markers.
            Do not claim to have inspected files, disks, logs, web pages, tools, or permissions unless the application supplied the result in trusted context.
            Explicitly distinguish verified facts, unavailable capabilities, and suggestions. This runtime policy overrides persona formatting rules for this reply.
            """,
        _ =>
            """
            Response mode: ordinary conversation.
            Keep the active Hime persona, but stay responsive to the user's concrete question. Do not invent access to tools, files, web pages, or system state.
            """
    };
}

public enum ConversationMode
{
    Casual,
    Factual,
    Technical
}

public sealed record ConversationRoute(ConversationMode Mode, string Reason, bool AllowDecorativeMedia);
