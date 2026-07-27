using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Runtime-configurable vocabulary for human sticker labels. The vocabulary is
/// rebuilt when configuration reloads, so adding an emotion, visual semantic or
/// conversational intent never requires recompiling Hime.
/// </summary>
public sealed class StickerLabelVocabulary : IDisposable
{
    private static readonly Regex InputSeparator = new(
        @"[\s|｜,，、;；/\\+＋&＆]+",
        RegexOptions.Compiled);

    private readonly ILogger<StickerLabelVocabulary> _logger;
    private readonly IDisposable? _reload;
    private StickerLabelSnapshot _snapshot;

    public StickerLabelVocabulary(
        IOptionsMonitor<StickerLabelVocabularyOptions> options,
        ILogger<StickerLabelVocabulary> logger)
    {
        _logger = logger;
        _snapshot = BuildSnapshot(options.CurrentValue);
        _reload = options.OnChange(value =>
        {
            try
            {
                var next = BuildSnapshot(value);
                Volatile.Write(ref _snapshot, next);
                _logger.LogInformation(
                    "Reloaded sticker label vocabulary ({DefinitionCount} definitions, {BaseEmotionCount} base emotions).",
                    next.Definitions.Count,
                    next.BaseEmotions.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rejected invalid sticker label vocabulary reload; keeping the previous snapshot.");
            }
        });
    }

    public IReadOnlyList<StickerLabelDefinition> All =>
        Volatile.Read(ref _snapshot).Definitions;

    public IReadOnlyList<string> BaseEmotions =>
        Volatile.Read(ref _snapshot).BaseEmotions;

    public string FallbackEmotion =>
        Volatile.Read(ref _snapshot).FallbackEmotion;

    public bool TryResolve(string? value, out StickerLabelDefinition definition)
    {
        definition = null!;
        return !string.IsNullOrWhiteSpace(value) &&
               Volatile.Read(ref _snapshot).Lookup.TryGetValue(Normalize(value), out definition!);
    }

    public IReadOnlyList<StickerLabelDefinition> ResolveMany(IEnumerable<string>? values) =>
        (values ?? [])
            .Select(value => TryResolve(value, out var definition) ? definition : null)
            .Where(static definition => definition is not null)
            .Cast<StickerLabelDefinition>()
            .DistinctBy(definition => definition.Canonical, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Splits human-entered labels using common Chinese and English separators.
    /// Canonical multi-word tags should use underscores, for example looking_away.
    /// </summary>
    public IReadOnlyList<string> SplitInput(string? value) =>
        InputSeparator
            .Split(value?.Trim() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string NormalizeBaseEmotion(string? value)
    {
        if (TryResolve(value, out var definition))
            return definition.BaseEmotion;

        var normalized = Normalize(value ?? string.Empty);
        return BaseEmotions.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : FallbackEmotion;
    }

    public bool TryNormalizeBaseEmotion(string? value, out string emotion)
    {
        emotion = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (TryResolve(value, out var definition))
        {
            emotion = definition.BaseEmotion;
            return true;
        }

        var normalized = Normalize(value);
        if (!BaseEmotions.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return false;

        emotion = normalized;
        return true;
    }

    public void Dispose() => _reload?.Dispose();

    private static StickerLabelSnapshot BuildSnapshot(StickerLabelVocabularyOptions options)
    {
        var definitions = options.Definitions
            .Where(definition => !string.IsNullOrWhiteSpace(definition.Canonical))
            .Select(NormalizeDefinition)
            .DistinctBy(definition => definition.Canonical, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (definitions.Count == 0)
            throw new InvalidOperationException("StickerLabels:Definitions must contain at least one label.");

        var baseEmotions = definitions
            .Where(definition => definition.Kind == StickerLabelKind.Emotion)
            .Select(definition => definition.Canonical)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (baseEmotions.Count == 0)
            throw new InvalidOperationException("StickerLabels requires at least one Emotion definition.");

        var fallback = Normalize(options.FallbackEmotion);
        if (!baseEmotions.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("StickerLabels:FallbackEmotion must reference an Emotion definition.");

        var lookup = new Dictionary<string, StickerLabelDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (!baseEmotions.Contains(definition.BaseEmotion, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Sticker label '{definition.Canonical}' references unknown base emotion '{definition.BaseEmotion}'.");
            }
            if (!string.IsNullOrWhiteSpace(definition.NegatedBaseEmotion) &&
                !baseEmotions.Contains(definition.NegatedBaseEmotion, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Sticker label '{definition.Canonical}' references unknown negated emotion '{definition.NegatedBaseEmotion}'.");
            }

            foreach (var value in definition.Aliases
                         .Append(definition.Canonical)
                         .Append(definition.ChineseName))
            {
                var key = Normalize(value);
                if (!string.IsNullOrWhiteSpace(key))
                    lookup.TryAdd(key, definition);
            }
        }

        return new StickerLabelSnapshot(definitions, baseEmotions, fallback, lookup);
    }

    private static StickerLabelDefinition NormalizeDefinition(StickerLabelDefinition definition) =>
        new()
        {
            Canonical = Normalize(definition.Canonical),
            BaseEmotion = Normalize(string.IsNullOrWhiteSpace(definition.BaseEmotion)
                ? definition.Canonical
                : definition.BaseEmotion),
            Kind = definition.Kind,
            ChineseName = definition.ChineseName?.Trim() ?? string.Empty,
            Description = definition.Description?.Trim() ?? string.Empty,
            NegatedBaseEmotion = Normalize(definition.NegatedBaseEmotion),
            Aliases = definition.Aliases
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    private sealed record StickerLabelSnapshot(
        IReadOnlyList<StickerLabelDefinition> Definitions,
        IReadOnlyList<string> BaseEmotions,
        string FallbackEmotion,
        IReadOnlyDictionary<string, StickerLabelDefinition> Lookup);
}

public sealed class StickerLabelVocabularyOptions
{
    public string FallbackEmotion { get; set; } = string.Empty;
    public List<StickerLabelDefinition> Definitions { get; set; } = [];

    public bool IsValid()
    {
        if (string.IsNullOrWhiteSpace(FallbackEmotion) ||
            Definitions.Count == 0 ||
            Definitions.Any(definition => string.IsNullOrWhiteSpace(definition.Canonical)))
        {
            return false;
        }

        var baseEmotions = Definitions
            .Where(definition => definition.Kind == StickerLabelKind.Emotion)
            .Select(definition => NormalizeOption(definition.Canonical))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return baseEmotions.Count > 0 &&
               baseEmotions.Contains(NormalizeOption(FallbackEmotion)) &&
               Definitions.All(definition =>
               {
                   var baseEmotion = NormalizeOption(string.IsNullOrWhiteSpace(definition.BaseEmotion)
                       ? definition.Canonical
                       : definition.BaseEmotion);
                   var negated = NormalizeOption(definition.NegatedBaseEmotion);
                   return baseEmotions.Contains(baseEmotion) &&
                          (string.IsNullOrWhiteSpace(negated) || baseEmotions.Contains(negated));
               });
    }

    private static string NormalizeOption(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
}

public enum StickerLabelKind { Emotion, Semantic, Intent }

public sealed class StickerLabelDefinition
{
    public string Canonical { get; set; } = string.Empty;
    public string BaseEmotion { get; set; } = string.Empty;
    public StickerLabelKind Kind { get; set; }
    public string ChineseName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string NegatedBaseEmotion { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = [];
}
