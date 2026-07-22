using System.Text.RegularExpressions;

namespace Hime.Services;

/// <summary>
/// Shared dynamic vocabulary for human sticker labels. A label may describe a
/// broad emotion, a visible expression/action, or the conversational intent.
/// </summary>
public static class StickerLabelVocabulary
{
    private static readonly Regex InputSeparator = new(
        @"[\s|｜,，、;；/\\+＋&＆]+",
        RegexOptions.Compiled);

    private static readonly IReadOnlyList<StickerLabelDefinition> Definitions =
    [
        // Broad emotions.
        E("happy", "happy", "开心", "开心、喜悦或积极满足", "快乐", "高兴", "喜悦", "愉快"),
        E("shy", "shy", "害羞", "脸红、羞涩或不敢直视", "羞涩", "腼腆"),
        E("surprised", "surprised", "惊讶", "意外、吃惊或被吓到", "震惊", "吃惊", "意外"),
        E("embarrassed", "embarrassed", "尴尬", "窘迫、慌乱或冒汗", "窘迫", "慌乱"),
        E("angry", "angry", "生气", "愤怒、不满或烦躁", "愤怒", "恼火", "烦躁"),
        E("sad", "sad", "悲伤", "难过、委屈或低落", "难过", "伤心", "委屈", "低落"),
        E("comforting", "comforting", "安慰", "安抚、治愈或给予陪伴", "治愈", "安抚", "陪伴"),
        E("serious", "serious", "认真", "严肃、专注或坚定", "严肃", "专注", "坚定"),
        E("proud", "proud", "得意", "骄傲、自信或小小炫耀", "骄傲", "自信", "炫耀"),
        E("neutral", "neutral", "平静", "中性、无明显情绪或放空", "普通", "中性", "淡定", "无表情"),

        // Visible semantics used by WDv3 and by human correction.
        S("smile", "happy", "微笑", "自然微笑"),
        S("laughing", "happy", "大笑", "明显笑出声", "笑哭"),
        S("grin", "happy", "咧嘴笑", "露齿或咧嘴开心"),
        S("joy", "happy", "喜悦", "强烈的开心"),
        S("excited", "happy", "兴奋", "激动、充满期待", "激动", "期待"),
        S("amused", "happy", "觉得好笑", "被逗乐或看热闹", "好笑", "乐"),
        S("relieved", "happy", "松一口气", "如释重负", "释然"),
        S("crying", "sad", "哭泣", "正在哭或泪流满面", "哭"),
        S("tears", "sad", "流泪", "眼泪明显", "眼泪"),
        S("teary_eyes", "sad", "泪眼", "眼中含泪", "含泪"),
        S("frown", "sad", "皱眉难过", "难过地皱眉"),
        S("gloom", "sad", "阴郁", "情绪低沉", "低沉"),
        S("despair", "sad", "绝望", "崩溃或绝望", "崩溃"),
        S("angry", "angry", "愤怒脸", "明显生气的表情"),
        S("annoyed", "angry", "不耐烦", "嫌烦或被打扰", "嫌烦"),
        S("pout", "angry", "撅嘴", "赌气、撒娇式不满", "赌气"),
        S("furrowed_brow", "angry", "紧皱眉", "眉头紧锁"),
        S("frustrated", "angry", "抓狂", "受挫或烦闷", "受挫"),
        S("disgust", "angry", "嫌弃", "厌恶或嫌弃", "厌恶"),
        S("surprised", "surprised", "惊讶脸", "张大眼睛或嘴巴"),
        S("shock", "surprised", "震惊脸", "强烈震惊"),
        S("wide_eyed", "surprised", "瞪大眼", "睁大眼睛"),
        S("confused", "surprised", "困惑", "没听懂或迷惑", "疑惑", "迷惑"),
        S("questioning", "surprised", "问号", "疑问或不理解", "疑问"),
        S("scared", "surprised", "害怕", "受惊或恐惧", "恐惧"),
        S("blush", "shy", "脸红", "脸颊泛红"),
        S("nervous", "shy", "紧张", "局促或忐忑", "忐忑"),
        S("flustered", "embarrassed", "手足无措", "慌张、手忙脚乱"),
        S("looking_away", "shy", "移开视线", "害羞地不看对方", "别过脸"),
        S("sweatdrop", "embarrassed", "冷汗", "尴尬冒汗"),
        S("smug", "proud", "坏笑得意", "得意或略带挑衅", "坏笑"),
        S("smirk", "proud", "斜笑", "自信或调侃的笑"),
        S("arrogant", "proud", "傲娇", "骄傲或傲慢", "傲慢"),
        S("triumphant", "proud", "胜利", "获胜后的得意", "胜利脸"),
        S("hug", "comforting", "拥抱", "抱住对方安慰", "抱抱"),
        S("patting_head", "comforting", "摸头", "摸头安慰", "摸摸头"),
        S("holding_hands", "comforting", "牵手", "牵手陪伴"),
        S("serious", "serious", "严肃脸", "认真严肃"),
        S("stern", "serious", "严厉", "严厉或郑重"),
        S("determined", "serious", "坚定", "下定决心"),
        S("bored", "neutral", "无聊", "百无聊赖"),
        S("sleepy", "neutral", "困倦", "想睡或没精神", "困", "想睡"),
        S("tired", "neutral", "疲惫", "劳累或没力气", "累"),
        S("expressionless", "neutral", "面无表情", "没有明显表情"),
        S("blank_stare", "neutral", "呆住", "放空或呆滞", "发呆", "放空"),

        // Conversational intent: why this sticker is sent.
        I("friendly", "happy", "友好", "友善回应或打招呼"),
        I("playful", "happy", "俏皮", "轻松玩闹、活泼回应", "活泼"),
        I("teasing", "proud", "调侃", "善意吐槽或逗对方", "吐槽", "逗你"),
        I("comforting", "comforting", "安慰意图", "安抚对方情绪"),
        I("caring", "comforting", "关心", "表达关怀和在意", "关怀"),
        I("gentle", "comforting", "温柔", "柔和、体贴地回应", "体贴"),
        I("encouraging", "comforting", "鼓励", "给对方加油或支持", "加油", "支持"),
        I("affectionate", "comforting", "亲昵", "亲近、有好感的互动", "亲近"),
        I("vulnerable", "sad", "脆弱", "示弱、委屈或需要陪伴", "示弱"),
        I("apologetic", "sad", "道歉", "表达歉意或认错", "抱歉", "认错"),
        I("calm", "neutral", "冷静", "平和、不激烈地回应", "平和"),
        I("rejecting", "angry", "拒绝", "明确拒绝或表示不满"),
    ];

    private static readonly IReadOnlyDictionary<string, StickerLabelDefinition> Lookup = BuildLookup();

    public static IReadOnlyList<StickerLabelDefinition> All => Definitions;

    public static bool TryResolve(string? value, out StickerLabelDefinition definition)
    {
        definition = null!;
        return !string.IsNullOrWhiteSpace(value) && Lookup.TryGetValue(Normalize(value), out definition!);
    }

    public static IReadOnlyList<StickerLabelDefinition> ResolveMany(IEnumerable<string>? values) =>
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
    public static IReadOnlyList<string> SplitInput(string? value) =>
        InputSeparator
            .Split(value?.Trim() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyDictionary<string, StickerLabelDefinition> BuildLookup()
    {
        var result = new Dictionary<string, StickerLabelDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Definitions)
        {
            foreach (var value in definition.Aliases.Append(definition.Canonical).Append(definition.ChineseName))
                result.TryAdd(Normalize(value), definition);
        }
        return result;
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    private static StickerLabelDefinition E(string canonical, string baseEmotion, string chinese, string description, params string[] aliases) =>
        new(canonical, baseEmotion, StickerLabelKind.Emotion, chinese, description, aliases);

    private static StickerLabelDefinition S(string canonical, string baseEmotion, string chinese, string description, params string[] aliases) =>
        new(canonical, baseEmotion, StickerLabelKind.Semantic, chinese, description, aliases);

    private static StickerLabelDefinition I(string canonical, string baseEmotion, string chinese, string description, params string[] aliases) =>
        new(canonical, baseEmotion, StickerLabelKind.Intent, chinese, description, aliases);
}

public enum StickerLabelKind { Emotion, Semantic, Intent }

public sealed record StickerLabelDefinition(
    string Canonical,
    string BaseEmotion,
    StickerLabelKind Kind,
    string ChineseName,
    string Description,
    IReadOnlyList<string> Aliases);
