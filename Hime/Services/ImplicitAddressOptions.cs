namespace Hime.Services;

/// <summary>
/// Conservative rules for recognizing a group message that is addressed to Hime
/// without an explicit QQ @ mention.
/// </summary>
public sealed class ImplicitAddressOptions
{
    public bool Enabled { get; set; } = true;

    public List<string> BotAliases { get; set; } = ["\u5c0f\u771f\u5bfb", "\u771f\u5bfb", "Hime"];

    /// <summary>Semantic detection is only considered after a recent bot reply.</summary>
    public int RecentBotReplyMinutes { get; set; } = 15;

    public int MaxMessageCharacters { get; set; } = 300;

    public int ContextMessageLimit { get; set; } = 16;

    public bool UseSemanticClassifier { get; set; } = true;
}
