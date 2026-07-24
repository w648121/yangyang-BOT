namespace Hime.Services;

/// <summary>非 R18 二次元图片检索及随机图库配置。</summary>
public sealed class SetuOptions
{
    public bool Enabled { get; set; } = true;

    public string LoliconApiUrl { get; set; } = "https://api.lolicon.app/setu/v2";

    public string DmoeApiUrl { get; set; } = "https://www.dmoe.cc/random.php?return=json";

    public string LoliApiUrl { get; set; } = "https://www.loliapi.com/bg/?type=url";

    public int MaxImagesPerRequest { get; set; } = 10;

    public int RequestTimeoutSeconds { get; set; } = 25;

    public bool UseSemanticInterpreter { get; set; } = true;

    public string ForwardNickname { get; set; } = "秧秧";

    public List<string> KnownTags { get; set; } =
    [
        "鸣潮", "秧秧", "玄翎", "漂泊者", "萝莉", "白丝", "黑丝", "丝袜", "兽耳", "猫娘",
        "女仆", "制服", "泳装", "和服", "旗袍", "JK", "初音未来", "原神", "崩坏星穹铁道"
    ];

    public List<string> QueryFillers { get; set; } =
    [
        "一张", "几张", "一点", "点", "一些", "好看", "好看的", "漂亮", "漂亮的", "新的", "新",
        "不一样", "不一样的", "随机", "随机的", "有关", "有关的", "相关", "相关的", "作品", "同人作品"
    ];

    public List<string> VisualQuestionExclusions { get; set; } =
    [
        "这张图", "这幅图", "这个图", "那张图", "图里", "图片里", "识别图片", "图片内容", "图是什么"
    ];

    public List<string> DesireMarkers { get; set; } =
    [
        "想看", "想要", "看看", "欣赏", "找点", "找张", "发点", "发张", "来点", "来张", "给我看"
    ];

    public List<string> VisualSubjectMarkers { get; set; } =
    [
        "图", "插画", "壁纸", "同人", "作品", "立绘"
    ];

    /// <summary>
    /// Additional local safety terms. Built-in R18 terms remain mandatory and cannot be removed by configuration.
    /// </summary>
    public List<string> AdditionalForbiddenTags { get; set; } = [];
}
