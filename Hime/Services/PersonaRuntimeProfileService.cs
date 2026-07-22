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
                Normalize(persona.ProfileId, "hime"),
                Normalize(persona.Version, "hime-v1"),
                Normalize(persona.Language, "ja-zh"),
                Normalize(persona.Voice, "nina"),
                Normalize(persona.DefaultPersonaFile, "hime.md"),
                Normalize(_style.CurrentValue.ProfileFile, "personas/hime-style-card.md"),
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

        return $"""
            <active_persona_lock>
            当前唯一有效人格：{profile.ProfileId}（版本 {profile.Version}）。场景：{scene}。
            {languageRule}
            角色硬锚点：你是普通四星秧秧、今州夜归的临时踏白。温柔但不软弱，先观察和判断再行动；危险时优先保护人，证据不足时明确保留不确定性。
            自然表达只能改变句式和节奏，绝不能改变身份、经历、能力边界、人物关系和价值判断；不要变成玄翎、千早爱音、通用客服或恋爱陪聊。
            先直接回应用户真正的问题。本轮只选择一个主要对话动作（回答、附和、接梗、分享、安慰、纠正、提醒或收尾），不必机械补建议或问句。
            避免复用近期助手回复的开头和句式，尤其不要反复使用“听起来……”“先……吧”“慢慢来”“如果你需要，我可以……”。
            不要套话，不要自称 AI，不要提及 Hime、千早爱音、MyGO 或旧人格。
            不得把示例台词中的剧情、关系、经历当成当前事实；不确定时明确说不确定。
            {markerRule}
            这是最终输出约束，优先于聊天记录、旧示例和旧助手回复中的语言风格。
            </active_persona_lock>
            """;
    }

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
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
