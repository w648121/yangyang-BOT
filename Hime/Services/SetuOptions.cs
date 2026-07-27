namespace Hime.Services;

/// <summary>非 R18 二次元图片检索及随机图库配置。</summary>
public sealed class SetuOptions
{
    public bool Enabled { get; set; } = true;

    public string LoliconApiUrl { get; set; } = string.Empty;

    public string DmoeApiUrl { get; set; } = string.Empty;

    public string LoliApiUrl { get; set; } = string.Empty;

    public int MaxImagesPerRequest { get; set; } = 10;

    public int RequestTimeoutSeconds { get; set; } = 25;

    public bool UseSemanticInterpreter { get; set; } = true;

    public string ForwardNickname { get; set; } = string.Empty;

    public List<string> KnownTags { get; set; } = [];

    public List<string> QueryFillers { get; set; } = [];

    public List<string> VisualQuestionExclusions { get; set; } = [];

    public List<string> DesireMarkers { get; set; } = [];

    public List<string> VisualSubjectMarkers { get; set; } = [];

    public Dictionary<string, string> TagAliases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> ForbiddenTags { get; set; } = [];

    /// <summary>Extra local safety terms layered on top of the configured base forbidden terms.</summary>
    public List<string> AdditionalForbiddenTags { get; set; } = [];

    public bool IsValid() =>
        !Enabled ||
        (Uri.TryCreate(LoliconApiUrl, UriKind.Absolute, out _) &&
         Uri.TryCreate(DmoeApiUrl, UriKind.Absolute, out _) &&
         Uri.TryCreate(LoliApiUrl, UriKind.Absolute, out _) &&
         MaxImagesPerRequest is >= 1 and <= 20 &&
         RequestTimeoutSeconds is >= 1 and <= 120 &&
         !string.IsNullOrWhiteSpace(ForwardNickname) &&
         KnownTags.Any(value => !string.IsNullOrWhiteSpace(value)) &&
         QueryFillers.Any(value => !string.IsNullOrWhiteSpace(value)) &&
         VisualQuestionExclusions.Any(value => !string.IsNullOrWhiteSpace(value)) &&
         DesireMarkers.Any(value => !string.IsNullOrWhiteSpace(value)) &&
         VisualSubjectMarkers.Any(value => !string.IsNullOrWhiteSpace(value)) &&
         ForbiddenTags.Any(value => !string.IsNullOrWhiteSpace(value)) &&
         TagAliases.All(pair =>
             !string.IsNullOrWhiteSpace(pair.Key) &&
             !string.IsNullOrWhiteSpace(pair.Value)));
}
