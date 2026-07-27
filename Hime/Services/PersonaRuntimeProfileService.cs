using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Single source of truth for the character currently controlling every social route.
/// </summary>
public sealed class PersonaRuntimeProfileService
{
    private readonly IOptionsMonitor<PersonaOptions> _personas;
    private readonly IOptionsMonitor<ConversationStyleOptions> _style;

    public PersonaRuntimeProfileService(
        IOptionsMonitor<PersonaOptions> personas,
        IOptionsMonitor<ConversationStyleOptions> style)
    {
        _personas = personas;
        _style = style;
    }

    public PersonaRuntimeSnapshot Current
    {
        get
        {
            var persona = _personas.CurrentValue;
            return new PersonaRuntimeSnapshot(
                Require(persona.ProfileId, nameof(persona.ProfileId)),
                Require(persona.Version, nameof(persona.Version)),
                Require(persona.Language, nameof(persona.Language)),
                Require(persona.Voice, nameof(persona.Voice)),
                Require(persona.DefaultPersonaFile, nameof(persona.DefaultPersonaFile)),
                Require(_style.CurrentValue.ProfileFile, nameof(ConversationStyleOptions.ProfileFile)),
                persona.CorpusFile?.Trim() ?? string.Empty,
                persona.ActivatedAtUtc?.ToUniversalTime());
        }
    }

    public string BuildFinalInstruction(
        string scene,
        bool allowEmotionMarker,
        int? exactMarkerCount = null)
    {
        var profile = Current;
        var languageRule = profile.IsSimplifiedChinese
            ? "所有可见文字只用自然的简体中文；不要输出日语、不要做日中双语对照。"
            : "严格遵守当前人格文件规定的可见语言格式。";
        var markerRule = allowEmotionMarker
            ? exactMarkerCount is > 0
                ? $"结尾必须连续输出恰好 {exactMarkerCount.Value} 个受支持的 [emotion:...]、[sticker:...] 或工具返回的 [sticker-id:...] 标记。"
                : "需要表情时，最多在结尾放一个受支持的 [emotion:...]、[sticker:...] 或工具返回的 [sticker-id:...] 标记。"
            : "不要输出 emotion、sticker、sticker-id 或 voice 标记。";
        var identityRules = JoinRules(_personas.CurrentValue.RuntimeIdentityRules);
        var guardRules = JoinRules(_personas.CurrentValue.RuntimeGuardRules);

        return $"""
            <active_persona_lock>
            当前唯一有效人格：{profile.ProfileId}（版本 {profile.Version}）。场景：{scene}。
            {languageRule}
            {identityRules}
            {guardRules}
            {markerRule}
            这是最终输出约束，优先于聊天记录、旧示例和旧助手回复中的语言风格。
            </active_persona_lock>
            """;
    }

    private static string JoinRules(IEnumerable<string> rules) =>
        string.Join(
            Environment.NewLine,
            rules.Where(rule => !string.IsNullOrWhiteSpace(rule)).Select(rule => rule.Trim()));

    private static string Require(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException($"The active persona setting '{name}' is required.");
}

public sealed record PersonaRuntimeSnapshot(
    string ProfileId,
    string Version,
    string Language,
    string Voice,
    string PersonaFile,
    string StyleCardFile,
    string CorpusFile,
    DateTimeOffset? ActivatedAtUtc)
{
    public bool IsSimplifiedChinese =>
        Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
}
