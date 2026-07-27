using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Configurable, high-confidence negative signals for replies that have collapsed
/// into a generic assistant voice. It intentionally does not require catchphrases:
/// natural concise Yangyang replies remain valid unless they echo, expose policy,
/// or consist only of configured boilerplate.
/// </summary>
public sealed class PersonaPresenceOptions
{
    public bool Enabled { get; set; } = true;

    public double EchoSimilarityThreshold { get; set; } = 0.84;

    public int MinimumEchoCharacters { get; set; } = 8;

    public List<string> GenericStandaloneReplies { get; set; } = [];

    public List<string> MetaPolicyMarkers { get; set; } = [];

    public bool IsValid() =>
        EchoSimilarityThreshold is >= 0 and <= 1 &&
        MinimumEchoCharacters is >= 2 and <= 200;
}

public sealed record PersonaPresenceAssessment(
    bool RequiresRewrite,
    IReadOnlyList<string> Reasons)
{
    public static readonly PersonaPresenceAssessment Natural = new(false, []);
}

public sealed class PersonaPresenceService(IOptionsMonitor<PersonaPresenceOptions> options)
{
    public PersonaPresenceAssessment Assess(
        string? reply,
        string? userPrompt,
        bool casual)
    {
        var current = options.CurrentValue;
        if (!current.Enabled || !casual)
            return PersonaPresenceAssessment.Natural;

        var visible = Normalize(reply);
        if (visible.Length == 0)
            return new PersonaPresenceAssessment(true, ["空回复没有人格表达"]);

        var reasons = new List<string>();
        var prompt = Normalize(userPrompt);
        if (prompt.Length >= current.MinimumEchoCharacters &&
            visible.Length >= current.MinimumEchoCharacters &&
            ConversationTopicGraph.Similarity(visible, prompt) >=
            current.EchoSimilarityThreshold)
        {
            reasons.Add("只是复述用户原话");
        }

        if (current.GenericStandaloneReplies
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .Any(value => value.Length > 0 &&
                          visible.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add("回复只有通用助手套话");
        }

        if (current.MetaPolicyMarkers
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(marker => visible.Contains(
                marker.Trim(),
                StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add("向用户解释内部规则或能力政策");
        }

        return reasons.Count == 0
            ? PersonaPresenceAssessment.Natural
            : new PersonaPresenceAssessment(true, reasons);
    }

    private static string Normalize(string? value) =>
        string.Join(
            ' ',
            (value ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim();
}
