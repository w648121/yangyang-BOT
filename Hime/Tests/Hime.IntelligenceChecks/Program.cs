using System.Reflection;
using System.Text.Json;
using Hime.Commands;
using Hime.Data;
using Hime.Data.Models;
using Hime.Data.Services;
using Hime.Hosting;
using Hime.Messaging;
using Hime.Messaging.Interactions;
using Hime.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

string[] configurationFiles =
[
    "config/accounts.json",
    "config/core.json",
    "config/ai.json",
    "config/conversation.json",
    "config/stickers.json",
    "config/gallery.json",
    "config/voice.engines.json",
    "config/voice.profiles.json",
    "config/integrations.json"
];

IConfigurationBuilder configurationBuilder = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false);
foreach (string configurationFile in configurationFiles)
    configurationBuilder.AddJsonFile(configurationFile, optional: false);
configurationBuilder.AddJsonFile("appsettings.Local.json", optional: true);

IConfigurationRoot configuration = configurationBuilder.Build();
string[] requiredSections =
[
    "BotAccounts", "AI", "OpenCodeAgent", "ModelRouting", "Admin", "Personas", "ChatHistory", "RelationshipTrajectory",
    "Images", "GroupStickers", "AnimeTagger", "StickerTags", "Gallery", "VoiceSynthesis",
    "OneBot", "Setu", "Music", "GsCore", "MessageDispatch", "ReplyScheduling"
];
Assert(requiredSections.All(section => configuration.GetSection(section).Exists()),
    "all required modular configuration sections should be present");
Assert(!configuration.AsEnumerable().Any(item =>
        item.Key.EndsWith(":AllowedGroupIds", StringComparison.OrdinalIgnoreCase)),
    "group response authorization must live in LiteDB instead of configuration allow lists");

var botAccounts = configuration.GetSection("BotAccounts").Get<BotAccountsOptions>()
    ?? throw new InvalidOperationException("BotAccounts configuration did not bind.");
var enabledBotAccounts = botAccounts.GetEnabledConnections();
Assert(enabledBotAccounts.Count > 0 && enabledBotAccounts.Count(account => account.IsPrimary) == 1,
    "bot account configuration should expose one enabled primary connection");
Assert(new BotAccountsOptions().GetEnabledConnections().Single().Port == 3010,
    "missing account configuration should preserve the legacy Milky 3010 fallback");

Assert(SetuIntentInterpreter.TryParseDeterministic("来3张萝莉 白丝涩图", 10, out var taggedSetu) &&
       taggedSetu.Count == 3 &&
       taggedSetu.Source == SetuSourceMode.Lolicon &&
       taggedSetu.Tags.Contains("萝莉") &&
       taggedSetu.Tags.Contains("白丝"),
    "tagged setu command should parse count and multiple tags");
Assert(SetuIntentInterpreter.TryParseDeterministic("来三张色图", 10, out var chineseCountSetu) &&
       chineseCountSetu.Count == 3 &&
       chineseCountSetu.Source == SetuSourceMode.Lolicon,
    "Chinese image count should be understood");
Assert(SetuIntentInterpreter.TryParseDeterministic("随机涩图", 10, out var randomSetu) &&
       randomSetu.Source == SetuSourceMode.Random,
    "explicit random request should use DMOE or LoliAPI");
Assert(SetuIntentInterpreter.TryParseDeterministic("要涩图", 10, out var genericRandomSetu) &&
       genericRandomSetu.Source == SetuSourceMode.Random,
    "generic 要涩图 request should use a random provider");
Assert(SetuIntentInterpreter.TryParseDeterministic("我想看鸣潮的图", 10, out var naturalSetu) &&
       naturalSetu.Source == SetuSourceMode.Lolicon &&
       naturalSetu.Tags.Contains("鸣潮"),
    "natural franchise image request should become a tagged Lolicon request");
Assert(!SetuIntentInterpreter.TryParseDeterministic("我想看这张图片里的内容", 10, out _),
    "ordinary visual questions must not trigger the image provider");
Assert(!SetuIntentInterpreter.TryParseDeterministic("为什么总发涩图", 10, out _),
    "complaints about prior images must not be treated as a new image request");
Assert(SetuIntentInterpreter.TryParseDeterministic("来张 R18 涩图", 10, out var rejectedUnsafeSetu) &&
       rejectedUnsafeSetu.RejectedUnsafe,
    "R18 image requests must be rejected before any external API call");

var tagRequestUri = SetuApiService.BuildLoliconUri(
    "https://api.lolicon.app/setu/v2",
    3,
    ["萝莉", "白丝"],
    useKeyword: false).AbsoluteUri;
Assert(tagRequestUri.Contains("r18=0", StringComparison.Ordinal) &&
       tagRequestUri.Contains("excludeAI=true", StringComparison.Ordinal) &&
       tagRequestUri.Contains("size=original", StringComparison.Ordinal) &&
       tagRequestUri.Split("tag=", StringSplitOptions.None).Length == 3,
    "Lolicon tag request must enforce SFW, non-AI original images and repeated AND tags");
var keywordRequestUri = SetuApiService.BuildLoliconUri(
    "https://api.lolicon.app/setu/v2",
    3,
    ["萝莉", "白丝"],
    useKeyword: true).AbsoluteUri;
Assert(keywordRequestUri.Contains("keyword=", StringComparison.Ordinal) &&
       !keywordRequestUri.Contains("tag=", StringComparison.Ordinal),
    "keyword fallback URI must not retain tag filters");

var fallbackHandler = new QueueHttpMessageHandler(
    """{"error":"","data":[]}""",
    """{"error":"","data":[{"pid":123,"title":"test","author":"tester","r18":false,"aiType":0,"tags":["萝莉"],"urls":{"original":"https://example.com/original.png"}}]}""");
var fallbackApi = new SetuApiService(
    new SingleHttpClientFactory(new HttpClient(fallbackHandler)),
    Options.Create(new SetuOptions()),
    NullLogger<SetuApiService>.Instance);
var fallbackResult = await fallbackApi.FetchLoliconAsync(1, ["萝莉"]);
Assert(fallbackResult.MatchMode == "keyword-fallback" &&
       fallbackResult.Images.Count == 1 &&
       fallbackHandler.RequestUris.Count == 2 &&
       fallbackHandler.RequestUris[0].Query.Contains("tag=", StringComparison.Ordinal) &&
       !fallbackHandler.RequestUris[0].Query.Contains("keyword=", StringComparison.Ordinal) &&
       fallbackHandler.RequestUris[1].Query.Contains("keyword=", StringComparison.Ordinal),
    "keyword request must happen only after the tag request returns no acceptable data");
Assert(configuration["AI:Protocol"] == "OpenAI" &&
       configuration["AI:BaseUrl"] == "https://api.minimaxi.com/v1" &&
       configuration["AI:Model"] == "MiniMax-M3",
    "the direct cloud fallback should use MiniMax M3");
using (var openCodeConfig = JsonDocument.Parse(File.ReadAllText("opencode.json")))
{
    var rootModel = openCodeConfig.RootElement.GetProperty("model").GetString();
    var agentModel = openCodeConfig.RootElement.GetProperty("agent").GetProperty("hime-qq").GetProperty("model").GetString();
    Assert(rootModel == "hime-minimax/MiniMax-M3" && agentModel == rootModel,
        "OpenCode root and hime-qq agent should both use MiniMax M3");
}

AnimeTaggerOptions configuredTagger =
    configuration.GetSection("AnimeTagger").Get<AnimeTaggerOptions>()
    ?? throw new InvalidOperationException("AnimeTagger configuration should bind");
StickerTagOptions configuredStickerTags =
    configuration.GetSection("StickerTags").Get<StickerTagOptions>()
    ?? throw new InvalidOperationException("StickerTags configuration should bind");
Assert(Path.IsPathRooted(configuredTagger.ModelPath) &&
       Math.Abs(configuredStickerTags.StrongMatchThreshold - 0.72) < 0.0001,
    "visual tagger settings should come from modular configuration instead of defaults");
Assert(!configuration.GetSection("VoiceSynthesis:Voices:yangyang-indextts2-faithful-a-original").Exists(),
    "duplicate faithful-a-original voice profile should stay removed");
Assert(configuration["VoiceSynthesis:IndexTts:BaseUrl"] == "http://127.0.0.1:9892" &&
       new VoiceSynthesisOptions().IndexTts.BaseUrl == "http://127.0.0.1:9892",
    "IndexTTS2 must use its dedicated port instead of the enterprise-WeChat occupied 9882 port");
Assert(!configuration.GetValue<bool>("TargetedInteraction:Enabled"),
    "targeted interaction without configured targets should remain disabled");
Assert(configuration["Personas:Version"] == "yangyang-v3-dynamic" &&
       configuration["RelationshipTrajectory:SchemaVersion"] == "v3" &&
       !configuration.GetValue<bool>("RelationshipTrajectory:ImportLegacyData"),
    "the clean persona generation must use an isolated v3 relationship trajectory without legacy import");

var router = new ConversationRouter(new TestOptionsMonitor<ModelRoutingOptions>(new ModelRoutingOptions
{
    Enabled = true,
    UseHighCapabilityForTechnical = true,
    ComplexPromptMinCharacters = 120,
    HighCapabilityProviderId = "hime-minimax",
    HighCapabilityModelId = "MiniMax-M3"
}));

Assert(router.Route("hello").Mode == ConversationMode.Casual, "ordinary chat should be casual");
Assert(router.Route("opencode configuration check").Mode == ConversationMode.Technical, "technical marker should select technical mode");
var technical = router.Route("opencode configuration check");
Assert(!technical.AllowDecorativeMedia, "technical mode must disable decorative media");
Assert(router.SelectModel(technical, "opencode configuration check").ModelId == "MiniMax-M3", "technical mode should select MiniMax M3");
Assert(!router.SelectModel(router.Route("hello"), "hello").HasExplicitModel, "ordinary chat must keep default model");
var nuancedSocialPrompt = "我和朋友因为一场误会吵架了，现在既难过又有点后悔，不知道该怎么把真正想说的话讲清楚。";
Assert(router.SelectModel(router.Route(nuancedSocialPrompt), nuancedSocialPrompt).ModelId == "MiniMax-M3",
    "nuanced emotional conversation should select the higher-capability model");

var styleOptions = new TestOptionsMonitor<ConversationStyleOptions>(new ConversationStyleOptions
{
    ProfileFile = Path.Combine(Directory.GetCurrentDirectory(), "personas", "yangyang-style-card.md"),
    MaxExamplesPerPrompt = 3
});
var personaOptions = new TestOptionsMonitor<PersonaOptions>(new PersonaOptions
{
    ProfileId = "yangyang",
    Version = "yangyang-v1",
    Language = "zh-CN",
    Voice = "yangyang",
    CorpusFile = Path.Combine(Directory.GetCurrentDirectory(), "data", "personas", "yangyang-lines.jsonl"),
    PlotKnowledgeFile = Path.Combine(Directory.GetCurrentDirectory(), "data", "personas", "yangyang-plot-events.jsonl"),
    ComplianceRewriteEnabled = true
});
var runtimeProfile = new PersonaRuntimeProfileService(personaOptions, styleOptions);
var styleCard = new ConversationStyleService(
    styleOptions,
    runtimeProfile,
    NullLogger<ConversationStyleService>.Instance);
var groupStyle = styleCard.BuildInstruction(router.Route("今天好困"), HimeStyleScene.GroupReply);
Assert(groupStyle.Contains("Simplified Chinese only", StringComparison.Ordinal) &&
       !groupStyle.Contains("Japanese on the first line", StringComparison.Ordinal),
    "active Yangyang group replies should receive a Chinese-only delivery card");
Assert(groupStyle.Contains("Natural dialogue choice", StringComparison.Ordinal),
    "social replies should receive one varied dialogue-act instruction");
var technicalStyle = styleCard.BuildInstruction(router.Route("检查 C 盘大小"), HimeStyleScene.PrivateReply);
Assert(technicalStyle.Contains("precision", StringComparison.OrdinalIgnoreCase) &&
       !technicalStyle.Contains("Scene examples", StringComparison.Ordinal),
    "technical replies must retain a concise non-decorative delivery policy");
Assert(styleCard.BuildProactivePlannerInstruction().Contains("JSON", StringComparison.Ordinal),
    "proactive planner should receive a JSON-safe delivery instruction");
Assert(styleCard.BuildProactivePlannerInstruction().Contains("Simplified Chinese only", StringComparison.Ordinal),
    "proactive planner should share the active Yangyang language contract");
var personaCorpus = new PersonaCorpusService(personaOptions, NullLogger<PersonaCorpusService>.Instance);
Assert(personaCorpus.Count >= 600, "verified Yangyang corpus should be available at runtime");
var plotKnowledge = new PersonaPlotKnowledgeService(personaOptions, NullLogger<PersonaPlotKnowledgeService>.Instance);
Assert(plotKnowledge.Count == 53, "the versioned Yangyang plot index should contain 53 verified events");
var cloudValley = plotKnowledge.Retrieve("秧秧还记得云灵谷的初见吗", 3);
Assert(cloudValley.Corrections.Any(item => item.Original == "云灵谷" && item.Canonical == "云陵谷") &&
       cloudValley.Events.Any(item => item.Id == "yangyang-plot-001"),
    "a typo in Cloud Tomb Valley should be corrected and retrieve the canonical first meeting");
var versionLetter = plotKnowledge.Retrieve("秧秧 2.3 版本来信写了什么", 3);
Assert(versionLetter.Events.Any(item => item.Id == "yangyang-plot-038"),
    "a version-specific letter question should retrieve the matching letter instead of a generic summary");
Assert(plotKnowledge.BuildInstruction("秧秧还记得玉星湖的任务吗", 3)
        .Contains("<plot_knowledge_guard>", StringComparison.Ordinal),
    "an unknown plot location should activate the anti-hallucination guard");
var compliance = new PersonaComplianceService(
    new TestAiClient(),
    personaOptions,
    runtimeProfile,
    personaCorpus,
    plotKnowledge,
    NullLogger<PersonaComplianceService>.Instance);
var policyReply = compliance.Evaluate(
    "与你同行是我的选择，但是否成为恋人或夫妻，也该由我自己确认，不能因为你这样叫我就算成立。",
    userPrompt: "秧秧做我老婆");
Assert(policyReply.Reasons.Contains("用规则说明代替自然关系回应"),
    "relationship policy prose should be rejected as unnatural");
var rewardReply = compliance.Evaluate(
    "先看你表现，乖一点我再考虑让你叫老婆。",
    userPrompt: "秧秧做我老婆");
Assert(rewardReply.Reasons.Contains("用奖励式调侃暗示关系许可"),
    "reward-style teasing should not imply future relationship acceptance");
var delayedAcceptanceReply = compliance.Evaluate(
    "嗯……这个称呼，我收下了。但老婆这个词的分量，我还需要一点时间来真正理解它。",
    userPrompt: "秧秧做我老婆");
Assert(delayedAcceptanceReply.Reasons.Contains("把关系确认延后包装成同意"),
    "accepting the title now while deferring its meaning must be rejected");
var appeasingReply = compliance.Evaluate(
    "你的直率让我有些措手不及，但同时也让我感到一丝温暖。",
    userPrompt: "秧秧做我老婆");
Assert(appeasingReply.Reasons.Contains("用被打动的情绪迎合关系要求"),
    "emotional praise must not replace Yangyang's own stance");
var harshRepeatedReply = compliance.Evaluate(
    "我听到了。不过再说多少次，我的回答也不会改变。今天先到这吧。",
    userPrompt: "秧秧做我老婆");
Assert(harshRepeatedReply.Reasons.Contains("把重复关系请求写成训话或终止对话"),
    "a repeated relationship request must not be answered as a reprimand or conversation shutdown");
Assert(compliance.Evaluate("……怎么又提起这个了？你今天是有什么话想和我说吗？",
        userPrompt: "秧秧做我老婆").Score == 100,
    "a gentle question that keeps the conversation open should remain valid");
var inventedSceneReply = compliance.Evaluate(
    "还来呀……我都听见了。走吧，陪我去吹吹风。",
    userPrompt: "秧秧做我老婆");
Assert(inventedSceneReply.Reasons.Contains("引入用户未提及的固定场景"),
    "a relationship reply must not invent a scenic activity absent from the user message");
var actionReply = compliance.Evaluate(
    "（微微歪头看你）你今天怎么一直惦记着这个称呼？",
    userPrompt: "秧秧做我老婆");
Assert(actionReply.Reasons.Contains("含动作或舞台描写"),
    "novel-style action narration must be rejected");
var roleLeakReply = compliance.Evaluate(
    "我已经听见了。\n\n[USER deOne]\n为什么不直接回答？",
    userPrompt: "秧秧做我老婆");
Assert(roleLeakReply.Reasons.Contains("泄露对话角色标签"),
    "serialized USER/ASSISTANT transport labels must never be visible");
var continuityCompliance = new PersonaComplianceService(
    new TestAiClient("……你今天怎么一直惦记着这个称呼？是有什么话想和我说吗？"),
    personaOptions,
    runtimeProfile,
    personaCorpus,
    plotKnowledge,
    NullLogger<PersonaComplianceService>.Instance);
var continuityRewrite = await continuityCompliance.RefineIfNeededAsync(
    "（轻轻叹了口气）你啊……又拿这两个字逗我。我们去花田看看吧。",
    "秧秧做我老婆",
    "私聊回复",
    10001,
    casual: true,
    requireEmotionMarker: false,
    recentAssistantReplies: ["真是拿你没办法……我陪你去散步吧。"],
    repeatedCurrentMessageCount: 2);
Assert(continuityRewrite.Contains("一直惦记着这个称呼", StringComparison.Ordinal) &&
       !continuityRewrite.Contains("花田", StringComparison.Ordinal) &&
       !continuityRewrite.Contains("散步", StringComparison.Ordinal),
    "a polluted second reply should be rewritten without inheriting assistant-created scenes");
var relationshipFallbackMethod = typeof(PersonaComplianceService).GetMethod(
    "BuildRelationshipBoundaryFallback", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("relationship fallback generator is missing");
Assert((string)relationshipFallbackMethod.Invoke(null, [string.Empty, false, 2])! ==
       "……你今天怎么一直惦记着这个称呼？是有什么话想和我说吗？",
    "the second-repeat fallback should notice the repetition without forcing a scene");
Assert((string)relationshipFallbackMethod.Invoke(null, [string.Empty, false, 3])! ==
       "还来呀……我已经听见了。",
    "later-repeat fallback should remain open without inventing an activity");
var careExamples = personaCorpus.Select("我有点难过，你能陪我聊聊吗", 4);
Assert(careExamples.Count == 4 && careExamples.Any(item => item.Scene == "care"),
    "persona corpus should retrieve scene-relevant cadence examples");
var relationshipExamples = personaCorpus.Select("秧秧做我老婆", 4);
Assert(relationshipExamples.Any(item => item.Scene == "relationship"),
    "direct partner and marriage wording should retrieve relationship cadence instead of casual-only examples");
Assert(relationshipExamples.All(item =>
        !item.Text.Contains("赏花", StringComparison.Ordinal) &&
        !item.Text.Contains("花田", StringComparison.Ordinal) &&
        !item.Text.Contains("散步", StringComparison.Ordinal) &&
        !item.Text.Contains("吹风", StringComparison.Ordinal)),
    "relationship-status cadence must not sample concrete scenes from unrelated official letters");
var prefixReplayDatabase = $"relationship-prefix-replay-{Guid.NewGuid():N}.db";
using (var prefixReplay = new RelationshipTrajectoryService(
           Options.Create(new RelationshipTrajectoryOptions
           {
               Enabled = true,
               DatabaseFileName = prefixReplayDatabase,
               AcceptEventsAfterUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
           }),
           NullLogger<RelationshipTrajectoryService>.Instance))
{
    prefixReplay.RecordUserMessage(91001, 10001, "tester", null, "~ai 秧秧做我老婆");
    var firstPrefixedPlan = prefixReplay.BuildPlan(91001, 10001, "tester", null, null, "秧秧做我老婆");
    Assert(firstPrefixedPlan.RepeatedCurrentMessageCount == 1,
        "the first prefixed private AI message should count once");
    prefixReplay.RecordUserMessage(91002, 10001, "tester", null, "~ai 秧秧做我老婆");
    var secondPrefixedPlan = prefixReplay.BuildPlan(91002, 10001, "tester", null, null, "秧秧做我老婆");
    Assert(secondPrefixedPlan.RepeatedCurrentMessageCount == 2,
        "event-layer ~ai text and command-layer stripped text must share the same repetition key");
}
var casualExamples = personaCorpus.Select("你好，今天想随便聊聊", 4);
Assert(casualExamples.All(item =>
        !item.Text.Contains("残象", StringComparison.Ordinal) &&
        !item.Text.Contains("无音区", StringComparison.Ordinal) &&
        !item.Text.Contains("频谱", StringComparison.Ordinal)),
    "ordinary chat cadence should not be polluted by plot exposition");
var personaCore = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "personas", "yangyang.md"));
Assert(personaCore.Contains("普通四星秧秧", StringComparison.Ordinal) &&
       personaCore.Contains("温柔但不软弱", StringComparison.Ordinal) &&
       personaCore.Contains("不是现实搜索", StringComparison.Ordinal),
    "compact persona must retain identity, behavior, and ability boundaries");

var stickerCountMethod = typeof(AiCommand).GetMethod("GetRequestedStickerCount", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("sticker batch detector is missing");
Assert((int)stickerCountMethod.Invoke(null, ["发3个表情包我看看"])! == 3, "three-sticker request should be detected");
Assert((int)stickerCountMethod.Invoke(null, ["发送三个情绪标签"])! == 3, "Chinese-number sticker request should be detected");

Assert((int)stickerCountMethod.Invoke(null, ["\u6765\u4e24\u5f20\u8868\u60c5\u5305"])! == 2, "Chinese two-sticker request should be detected");
Assert((int)stickerCountMethod.Invoke(null, ["\u7ed9\u6211\u4ec0\u4e2a\u8868\u60c5"])! == 3, "colloquial three-sticker request should be detected");
Assert((int)stickerCountMethod.Invoke(null, ["\u4e09\u8fde\u8868\u60c5\u5305"])! == 3, "three-in-a-row sticker request should be detected");
Assert((int)stickerCountMethod.Invoke(null, ["\u53d1\u4e00\u4e2a\u5fe7\u4f24\u7684\u8868\u60c5\u5305"])! == 1, "single sticker request should be detected");
var stickerEmotionMethod = typeof(AiCommand).GetMethod("GetRequestedStickerEmotion", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("sticker emotion detector is missing");
Assert((string?)stickerEmotionMethod.Invoke(null, ["\u53d1\u4e00\u4e2a\u5fe7\u4f24\u7684\u8868\u60c5\u5305"]) == "sad", "sad sticker request should select sad emotion");
Assert((string?)stickerEmotionMethod.Invoke(null, ["\u53d1\u4e00\u4e2a\u5f00\u5fc3\u7684\u8868\u60c5\u5305"]) == "happy", "happy sticker request should select happy emotion");
Assert(VisibleReplyTextSanitizer.Clean("你好呀 ``") == "你好呀",
    "a trailing double-backtick protocol artifact should be removed");
Assert(VisibleReplyTextSanitizer.Clean("你好呀 ``。") == "你好呀。",
    "a protocol artifact before Chinese punctuation should be removed without leaving a space");
Assert(VisibleReplyTextSanitizer.Clean("请使用 ``code`` 作为示例") == "请使用 ``code`` 作为示例",
    "paired inline Markdown delimiters should be preserved");
Assert(VisibleReplyTextSanitizer.Clean("```text\nhello\n```") == "```text\nhello\n```",
    "triple-backtick code fences should be preserved");
Assert(VisibleReplyTextSanitizer.Clean("（停下脚步，微微歪头看你）漂泊者，怎么了？") ==
       "漂泊者，怎么了？",
    "leading role-play actions should be removed from visible replies");
Assert(VisibleReplyTextSanitizer.Clean("我已经听见了。\n\n[USER deOne]\n为什么不回答？") ==
       "我已经听见了。",
    "model-generated USER continuations should be truncated before sending");
Assert(VisibleReplyTextSanitizer.Clean("你啊……（无奈地笑了一声）今天怎么了？") ==
       "你啊……今天怎么了？",
    "inline role-play actions should be removed without deleting spoken text");
var openAiTextExtractor = typeof(AnthropicClientWrapper).GetMethod(
    "ExtractOpenAiText", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("OpenAI response text extractor is missing");
var reasoningResponse = "{\"choices\":[{\"message\":{\"content\":\"<think>private reasoning</think>\\n\\n最终台词\"}}]}";
Assert((string)openAiTextExtractor.Invoke(null, [reasoningResponse])! == "最终台词",
    "MiniMax reasoning must never leak from content into the visible QQ reply");
Assert(StickerLabelVocabulary.TryResolve("脸红", out var blushLabel) &&
       blushLabel.Canonical == "blush" && blushLabel.BaseEmotion == "shy" &&
       blushLabel.Kind == StickerLabelKind.Semantic,
    "manual sticker labels should accept fine-grained Chinese visual semantics");
Assert(StickerLabelVocabulary.TryResolve("温柔", out var gentleLabel) &&
       gentleLabel.Canonical == "gentle" && gentleLabel.Kind == StickerLabelKind.Intent,
    "manual sticker labels should distinguish conversational intent from visible emotion");
Assert(StickerLabelVocabulary.All.Select(item => item.Canonical).Distinct(StringComparer.OrdinalIgnoreCase).Count() >
       ImageService.CanonicalEmotions.Count,
    "the human label vocabulary must remain dynamic and richer than the ten fallback emotions");
var supportedManualStickerMethod = typeof(StickerManagementService).GetMethod(
    "IsSupportedSticker", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("manual sticker format detector is missing");
var staticJpegSticker = Path.Combine(
    Directory.GetCurrentDirectory(), "resources", "path", "B7C4FEB56C9E5AA56BC67E75D4B33B1C.jpg");
Assert((bool)supportedManualStickerMethod.Invoke(null, [staticJpegSticker])!,
    "administrator sticker import should accept a real static JPEG, not only GIF");

var history = new ChatHistoryOptions();
Assert(history.RawContextDays == 3, "raw context must default to three days");
Assert(history.MaxRecentMessagesInPrompt == 24, "model context should keep a bounded recent window");
Assert(history.MaxSummaryItems > 0 && history.MaxSummaryCharacters > 0, "summary limits must be positive");
var legacyNullDatabase = $"hime-intelligence-check-null-history-{Guid.NewGuid():N}";
var legacyNullDatabasePath = Path.Combine(AppContext.BaseDirectory, "data", legacyNullDatabase + ".db");
using (var legacyNullContext = new HimeDbContext(legacyNullDatabase))
{
    const long legacyUserId = 99112233;
    var legacyHistoryOptions = new ChatHistoryOptions
    {
        Namespace = "legacy-null-test",
        ImportLegacySessions = false
    };
    legacyNullContext.Database.GetCollection<ChatSession>("chat_sessions").Upsert(new ChatSession
    {
        SessionId = $"legacy-null-test:p:{legacyUserId}",
        Messages = null!,
        HistoricalSummary = null!,
        ActivePersonaVersion = "legacy-persona"
    });
    var legacyChat = new ChatService(
        legacyNullContext,
        Options.Create(legacyHistoryOptions),
        personaOptions);
    Assert(legacyChat.GetHistory(legacyUserId).Count == 0,
        "legacy sessions with null messages or summary must not break the ~ai history path");
}
File.Delete(legacyNullDatabasePath);
Assert(new GroupStickerOptions().AllowedExtensions.SequenceEqual([".gif"], StringComparer.OrdinalIgnoreCase),
    "newly collected stickers must default to GIF-only");
Assert(new GroupStickerOptions().AnalysisQueueCapacity >= 8,
    "sticker vision work must use a bounded background queue");
Assert(new VoiceSynthesisOptions().AutoReplyQueueCapacity == 8 &&
       new VoiceSynthesisOptions().AutoReplyMaxQueueAgeSeconds == 30,
    "automatic voice delivery must be bounded and discard stale jobs");
Assert(new MessageDispatchOptions().PartitionCount >= 2 &&
       new MessageDispatchOptions().CapacityPerPartition >= 8,
    "incoming messages must use bounded partitioned conversation queues");
Assert(new LiteDbWriteBehindOptions().FlushIntervalMilliseconds <= 500 &&
       new LiteDbWriteBehindOptions().MaxBatchSize >= 32,
    "frequent group activity writes must be coalesced and flushed in bounded batches");
Assert(new ImageOptions().DownloadConcurrency is >= 2 and <= 8 &&
       new ImageOptions().SessionCacheLimit >= 256,
    "image archiving must allow bounded concurrency and retain a bounded dedup cache");
var privateConversation = new PrivateConversationOptions();
Assert(privateConversation.Allows(3073554911), "empty private-chat allow list should accept ordinary friend messages");
Assert(privateConversation.TriggerPrefix == "~ai", "private AI conversations must require the ~ai trigger by default");
privateConversation.AllowedUserIds = [1762889143];
Assert(privateConversation.Allows(1762889143) && !privateConversation.Allows(3073554911),
    "configured private-chat allow list should restrict automatic replies");
Assert(privateConversation.MergeWindowSeconds == 2 && privateConversation.MaxMergedMessages >= 2,
    "private conversations should use a bounded short merge window by default");
var privateTriggerMethod = typeof(HimeBotService).GetMethod(
    "TryExtractPrivateAiPrompt", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("private ~ai trigger parser is missing");
object?[] privateTriggered = ["~ai 你好", "~ai", null];
Assert((bool)privateTriggerMethod.Invoke(null, privateTriggered)! && (string?)privateTriggered[2] == "你好",
    "~ai followed by whitespace should enter private AI flow and strip the prefix");
object?[] privatePlain = ["你好", "~ai", null];
Assert(!(bool)privateTriggerMethod.Invoke(null, privatePlain)!,
    "ordinary private text must not enter AI flow");
object?[] privateLookalike = ["~aix test", "~ai", null];
Assert(!(bool)privateTriggerMethod.Invoke(null, privateLookalike)!,
    "a lookalike prefix must not trigger private AI flow");
var proactiveSimilarityMethod = typeof(ProactiveAgentService).GetMethod(
    "TextSimilarity", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("proactive similarity guard is missing");
Assert((double)proactiveSimilarityMethod.Invoke(null, [
           "今天也辛苦了，记得给自己留一点喘口气的时间。",
           "今天也辛苦啦，记得给自己留一点喘口气的时间！"])! >= 0.72,
    "near-identical proactive messages must be blocked even after trivial wording changes");
Assert((double)proactiveSimilarityMethod.Invoke(null, [
           "今天也辛苦了，记得给自己留一点喘口气的时间。",
           "刚才看到大家在聊新活动，那个配色确实挺有意思。 "])! < 0.72,
    "different proactive topics must remain sendable");
var separatedLabels = StickerLabelVocabulary.SplitInput("开心|脸红，俏皮；温柔+关心＆安慰 大笑");
Assert(separatedLabels.Count == 7 && separatedLabels.Contains("安慰"),
    "sticker labels should accept pipes, punctuation, plus, ampersand, and whitespace separators");
var explicitIntentMethod = typeof(StickerManagementService).GetMethod(
    "GetExplicitIntentTags", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("manual sticker intent selector is missing");
var angrySeriousDefinitions = StickerLabelVocabulary.ResolveMany(["angry", "serious"]);
var inferredManualIntents = (IReadOnlyList<string>)explicitIntentMethod.Invoke(
    null, [angrySeriousDefinitions])!;
Assert(inferredManualIntents.Count == 0,
    "manual angry + serious labels must not manufacture a calm intent");
var explicitCalmDefinitions = StickerLabelVocabulary.ResolveMany(["angry", "calm"]);
var explicitManualIntents = (IReadOnlyList<string>)explicitIntentMethod.Invoke(
    null, [explicitCalmDefinitions])!;
Assert(explicitManualIntents.SequenceEqual(["calm"], StringComparer.OrdinalIgnoreCase),
    "an explicitly supplied calm intent must still be retained");

var recentVisualIndexPath = Path.Combine(Path.GetTempPath(), $"hime-intelligence-check-visual-{Guid.NewGuid():N}.json");
var recentVisualOptions = Options.Create(new RecentVisualContextOptions
{
    IndexPath = recentVisualIndexPath,
    RetentionMinutes = 15,
    MaxImagesPerMessage = 1
});
var recentVisuals = new RecentVisualContextStore(recentVisualOptions);
var recentVisualSamplePath = Path.Combine(Directory.GetCurrentDirectory(), "resources", "images", "smile.gif");
recentVisuals.Remember(100, 42, [recentVisualSamplePath]);
Assert(recentVisuals.TryGetForExplicitFollowUp(100, 42, "这张图片里是什么内容？", out var followUpImages) &&
       followUpImages.Count == 1,
    "an explicit image follow-up should recover the sender's recent ordinary image");
var restoredVisuals = new RecentVisualContextStore(recentVisualOptions);
Assert(restoredVisuals.TryGetForExplicitFollowUp(100, 42, "图里有什么？", out var restoredImages) &&
       restoredImages.Count == 1,
    "the recent visual context should survive a bot restart");
Assert(!recentVisuals.TryGetForExplicitFollowUp(100, 42, "今天怎么样？", out _),
    "ordinary chat must not attach a recent image without an explicit visual question");
File.Delete(recentVisualIndexPath);

var stateOptions = new ProactiveAgentOptions
{
    ActiveConversationWindowMinutes = 8,
    CoolingConversationWindowMinutes = 25,
    LivelySpeakerThreshold = 3,
    AllowText = true,
    AllowSticker = true,
    AllowArticles = true,
    AllowVoice = true
};
Assert(stateOptions.InitialDelaySeconds >= 60 && stateOptions.MaximumGroupsPerScan == 1,
    "proactive scans must be staggered after restart and process one group at a time");
var stateMachine = new GroupConversationStateMachine();
var now = DateTime.UtcNow;
var livelyGroup = new GroupActivityRecord
{
    GroupId = 100,
    LastIncomingAt = now.AddMinutes(-1),
    RecentMessages =
    [
        new() { UserId = 1, Nickname = "A", Content = "在聊乐队", Time = now.AddMinutes(-3) },
        new() { UserId = 2, Nickname = "B", Content = "这个话题有意思", Time = now.AddMinutes(-2) },
        new() { UserId = 3, Nickname = "C", Content = "继续说说", Time = now.AddMinutes(-1) }
    ]
};
var livelyState = stateMachine.Analyze(livelyGroup, stateOptions, now);
Assert(livelyState.Phase == GroupConversationPhase.Lively, "three recent speakers should produce a lively group state");
var contentPlanner = new ProactiveContentPlanner(stateMachine);
var livelyPlan = contentPlanner.Plan(livelyGroup, stateOptions, canSendArticle: true, now);
Assert(livelyPlan.Action == ProactiveAction.Text && livelyPlan.Intent == ProactiveIntent.ConversationBridge,
    "lively conversation should receive a brief bridge, not a diary post");

var quietGroup = new GroupActivityRecord
{
    GroupId = 101,
    LastIncomingAt = now.AddMinutes(-40),
    RecentMessages = [new() { UserId = 1, Nickname = "A", Content = "晚安", Time = now.AddMinutes(-40) }]
};
var quietPlan = contentPlanner.Plan(quietGroup, stateOptions, canSendArticle: true, now);
Assert(quietPlan.Action == ProactiveAction.Article && quietPlan.Intent == ProactiveIntent.MiniDiary,
    "quiet group should prefer a self-contained mini diary when an article slot is available");

var memoryMarker = typeof(AiCommand).GetField("MemoryMarkerRegex", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as System.Text.RegularExpressions.Regex
    ?? throw new InvalidOperationException("memory marker regex is missing");
Assert(memoryMarker.IsMatch("[memory:user:address:call=小梨]"), "relationship-card address memory marker should be accepted");
var databaseName = $"hime-intelligence-check-persona-{Guid.NewGuid():N}";
var databasePath = Path.Combine(AppContext.BaseDirectory, "data", databaseName + ".db");
using (var context = new HimeDbContext(databaseName))
{
    var states = new PersonaStateService(
        context,
        Options.Create(new PersonaStateOptions { Enabled = true, ConfirmationsRequired = 1, MaxGroupMemberCards = 3 }),
        NullLogger<PersonaStateService>.Instance);
    states.ObserveConversation(42, "阿梨", 100, "测试群");
    states.ApplyMemoryProposals(42, 100, [new PersonaMemoryProposal("user", "address", "call", "小梨")]);
    var memberCards = states.BuildGroupPromptContext(100, "测试群", [42]);
    Assert(memberCards.Contains("小梨", StringComparison.Ordinal), "confirmed active-member relationship card should be included in group context");
}
File.Delete(databasePath);

var groupResponseDatabaseName = $"hime-intelligence-check-group-response-{Guid.NewGuid():N}";
var groupResponseDatabasePath = Path.Combine(AppContext.BaseDirectory, "data", groupResponseDatabaseName + ".db");
using (var context = new HimeDbContext(groupResponseDatabaseName))
{
    var groupResponses = new GroupResponseStateService(context);
    var responseChat = new ChatService(
        context,
        Options.Create(new ChatHistoryOptions
        {
            Namespace = "group-response-toggle-test",
            ImportLegacySessions = false
        }),
        personaOptions);
    responseChat.AppendTurn(
        31415926,
        "测试用户",
        "请记住我的约定",
        [],
        "我记住了。",
        [],
        assistantEmotion: "calm",
        groupId: 829269550);
    Assert(!groupResponses.IsEnabled(829269550),
        "an unknown group must be disabled by default");
    groupResponses.SetEnabled(829269550, true, 1928076256, "测试群");
    Assert(groupResponses.IsEnabled(829269550) &&
           groupResponses.GetEnabledGroupIds().SequenceEqual([829269550L]),
        "the response command state must persist in LiteDB");
    groupResponses.SetEnabled(829269550, false, 1928076256, "测试群");
    Assert(!groupResponses.IsEnabled(829269550),
        "stopping a group must persist a disabled state rather than falling back to configuration");
    groupResponses.SetEnabled(829269550, true, 1928076256, "测试群");
    var responseHistory = responseChat.GetHistory(31415926, 829269550);
    Assert(responseHistory.Count == 2 &&
           responseHistory.Any(message => message.Content.Contains("请记住我的约定", StringComparison.Ordinal)),
        "stopping and re-enabling a group must never clear its existing AI conversation history");
}
File.Delete(groupResponseDatabasePath);

Assert(GroupResponseGateMiddleware.IsControlCommand("/响应") &&
       GroupResponseGateMiddleware.IsControlCommand(" /停止 ") &&
       !GroupResponseGateMiddleware.IsControlCommand("/ai 你好"),
    "only group response control commands should bypass the disabled-group middleware gate");

var inspector = new LocalImageInspector(
    Options.Create(new StickerVisionOptions { Enabled = true }),
    NullLogger<LocalImageInspector>.Instance);
var samplePath = Path.Combine(Directory.GetCurrentDirectory(), "resources", "images", "smile.gif");
var inspection = inspector.Inspect(samplePath);
Assert(inspection is not null && inspection.Width > 0 && inspection.Height > 0, "local vision should inspect the bundled sticker");

using var animeTagger = new AnimeStickerTagger(
    Options.Create(new AnimeTaggerOptions
    {
        Enabled = true,
        ModelPath = "models/wd-vit-tagger-v3/model.onnx",
        TagsPath = "models/wd-vit-tagger-v3/selected_tags.csv"
    }),
    NullLogger<AnimeStickerTagger>.Instance);
var animeTags = animeTagger.Analyze(samplePath);
Assert(animeTags is not null && animeTags.SemanticTags.Count > 0,
    "WDv3 should return safe semantic tags for the bundled anime sticker");
var confirmedAnimeTags = animeTags!;
Assert(confirmedAnimeTags.AnalyzedFrames is >= 1 and <= 3 &&
       confirmedAnimeTags.TotalFrames >= confirmedAnimeTags.AnalyzedFrames,
    "anime sticker analysis should report representative frame coverage");
Console.WriteLine($"WDv3: {confirmedAnimeTags.Emotion} ({string.Join(", ", confirmedAnimeTags.SemanticTags)})");

var animatedStickerPath = Path.Combine(
    Directory.GetCurrentDirectory(), "resources", "images", "approved", "958354615",
    "happy_f36bd7d97ef7296b49e0e10e.gif");
var animatedTags = animeTagger.Analyze(animatedStickerPath);
Assert(animatedTags is not null && animatedTags.TotalFrames > 1 && animatedTags.AnalyzedFrames is 2 or 3,
    "the previously unindexed animated GIF should be classified from representative frames");
Console.WriteLine(
    $"WDv3 animated: {animatedTags!.Emotion} ({string.Join(", ", animatedTags.SemanticTags)}), " +
    $"frames {animatedTags.AnalyzedFrames}/{animatedTags.TotalFrames}");

var stickerCatalogPath = Path.Combine(Path.GetTempPath(), $"hime-intelligence-check-sticker-tags-{Guid.NewGuid():N}.json");
var tagCatalog = new StickerTagCatalog(
    Options.Create(new StickerTagOptions
    {
        CatalogPath = stickerCatalogPath
    }),
    NullLogger<StickerTagCatalog>.Instance);
tagCatalog.Upsert(
    samplePath,
    "shy",
    new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["shy"] = 0.86,
        ["happy"] = 0.72
    },
    ["blush", "smile"],
    ["friendly"]);

var neutralTestSticker = Path.Combine(
    Directory.GetCurrentDirectory(),
    "resources", "images", "approved", "829269550", "happy_535e68fcca4341cfd8ddd5f8.gif");
tagCatalog.Upsert(
    neutralTestSticker,
    "neutral",
    new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["neutral"] = 0.92 },
    ["expressionless"],
    ["calm"]);

var curatedImages = new ImageService(Options.Create(new ImageOptions
{
    Directory = Path.Combine(Directory.GetCurrentDirectory(), "resources", "images"),
    AdditionalDirectories = [],
    OnlyUseApprovedStickers = true,
    ApprovedStickerFileNames = ["smile.gif", "happy_535e68fcca4341cfd8ddd5f8.gif"],
    ApprovedStickerDirectories = []
}), tagCatalog);
Assert(curatedImages.AvailableImages.Count == 2, "only the two curated GIF stickers should be available");
Assert(curatedImages.Resolve("happy_5b1d9d08375a7b62b710b330.jpg") is null, "unapproved meme sticker must not be sendable");
Assert(curatedImages.ResolveEmotion("happy") is not null, "curated happy sticker should remain sendable");
Assert(curatedImages.ResolveSticker(["blush", "smile"]) is not null,
    "dynamic semantic tags should select a curated sticker");
var multiEmotionMatch = curatedImages.SearchStickers(new StickerSearchRequest
{
    Emotions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["shy"] = 0.8,
        ["happy"] = 0.7
    },
    SemanticTags = ["blush", "smile"],
    IntentTags = ["friendly"]
}).Single();
Assert(multiEmotionMatch.StickerId == "smile.gif" && multiEmotionMatch.MatchLevel == "strong",
    "multi-label search should select the sticker that covers both high-weight emotions");
var calmFallback = curatedImages.SearchStickers(new StickerSearchRequest
{
    Emotions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["angry"] = 1.0 },
    SemanticTags = ["disgust", "furrowed_brow"],
    IntentTags = ["warning"]
}).Single();
Assert(calmFallback.MatchLevel == "neutral-fallback" && calmFallback.StickerId == Path.GetFileName(neutralTestSticker),
    "low-confidence multi-label search should use a calm fallback instead of a wrong sticker");
Assert(curatedImages.GetDeclaredEmotion(neutralTestSticker) == "neutral" &&
       curatedImages.ResolveEmotion("neutral") == neutralTestSticker,
    "catalog emotion must override a misleading happy_ file-name prefix");
var toolSnapshot = curatedImages.BuildOpenCodeSnapshot();
Assert(toolSnapshot.Version == StickerTagCatalog.CurrentCatalogVersion,
    "OpenCode sticker snapshot should publish the current multi-frame catalog version");
Assert(toolSnapshot.Stickers.Any(item => item.StickerId == "smile.gif" && item.Emotions.Count >= 2),
    "OpenCode snapshot should expose multi-label emotion metadata for approved stickers");
var publishedSnapshotPath = Path.Combine(Path.GetTempPath(), $"hime-opencode-sticker-snapshot-{Guid.NewGuid():N}.json");
var publisher = new OpenCodeStickerCatalogPublisher(
    curatedImages,
    Options.Create(new StickerTagOptions { OpenCodeSnapshotPath = publishedSnapshotPath }),
    NullLogger<OpenCodeStickerCatalogPublisher>.Instance);
publisher.Publish();
var publishedSnapshot = File.ReadAllText(publishedSnapshotPath);
Assert(publishedSnapshot.Contains("\"stickerId\"", StringComparison.Ordinal) &&
       !publishedSnapshot.Contains(Directory.GetCurrentDirectory(), StringComparison.OrdinalIgnoreCase),
    "OpenCode tool snapshot should use camel-case IDs and never expose local paths");
File.Delete(publishedSnapshotPath);
var stickerProtocol = curatedImages.BuildPersonaImageList();
Assert(stickerProtocol.Contains("[sticker:tag1|tag2]", StringComparison.Ordinal) &&
       stickerProtocol.Contains("blush", StringComparison.Ordinal),
    "persona prompt should publish only real dynamic sticker tags");

var emotionDirectory = Path.Combine(Directory.GetCurrentDirectory(), "resources", "images");
var sadCatalogSticker = Path.Combine(
    emotionDirectory, "approved", "935869005", "sad_c045f2db5492b85c74b00328.gif");
tagCatalog.Upsert(
    sadCatalogSticker,
    "sad",
    new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["sad"] = 0.94 },
    ["crying"],
    ["vulnerable"]);
var emotionalCuratedImages = new ImageService(Options.Create(new ImageOptions
{
    Directory = emotionDirectory,
    AdditionalDirectories = [],
    OnlyUseApprovedStickers = true,
    ApprovedStickerFileNames = ["smile.gif"],
    ApprovedStickerDirectories = [Path.Combine(emotionDirectory, "approved")],
    EmotionMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["happy"] = "smile.gif"
    }
}), tagCatalog);
var sadSticker = emotionalCuratedImages.ResolveEmotion("sad");
var sadStickerEntry = string.IsNullOrWhiteSpace(sadSticker) ? null : tagCatalog.GetEntry(sadSticker);
Assert(sadStickerEntry?.EmotionScores.ContainsKey("sad") == true,
    "base emotion pools must resolve from catalog metadata rather than file-name prefixes");

var oversizedStickerDirectory = Path.Combine(
    Path.GetTempPath(), $"hime-oversized-sticker-{Guid.NewGuid():N}");
Directory.CreateDirectory(oversizedStickerDirectory);
var oversizedStickerPath = Path.Combine(oversizedStickerDirectory, "neutral_too_large.gif");
await using (var oversizedSticker = File.Create(oversizedStickerPath))
    oversizedSticker.SetLength(256 * 1024);
tagCatalog.Upsert(oversizedStickerPath, "neutral", ["expressionless"]);
var guardedImages = new ImageService(Options.Create(new ImageOptions
{
    Directory = oversizedStickerDirectory,
    AdditionalDirectories = [],
    OnlyUseApprovedStickers = true,
    ApprovedStickerDirectories = [oversizedStickerDirectory],
    MaxSendableStickerBytes = 128 * 1024
}), tagCatalog);
Assert(guardedImages.AvailableImages.Count == 0 &&
       !guardedImages.IsApprovedStickerPath(oversizedStickerPath),
    "oversized approved GIFs must never enter the sendable sticker catalog");
Directory.Delete(oversizedStickerDirectory, recursive: true);

File.Delete(stickerCatalogPath);

var buildSections = typeof(HelpCommand).GetMethod(
    "BuildSections", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("HelpCommand.BuildSections should exist");
var helpSections = buildSections.Invoke(null, null)
    ?? throw new InvalidOperationException("Help sections should be generated");
var helpRenderer = typeof(HelpCommand).Assembly.GetType("Hime.Commands.HelpMenuRenderer")
    ?? throw new InvalidOperationException("HelpMenuRenderer should exist");
var renderHelp = helpRenderer.GetMethod("Render", BindingFlags.Public | BindingFlags.Static)
    ?? throw new InvalidOperationException("HelpMenuRenderer.Render should exist");
var renderedHelpPath = renderHelp.Invoke(null, [helpSections]) as string;
Assert(renderedHelpPath is not null && File.Exists(renderedHelpPath) &&
       new FileInfo(renderedHelpPath).Length > 10 * 1024,
    "adaptive cyberpunk help menu should render to a non-empty cached PNG");

var commandServices = new ServiceCollection();
commandServices.AddSingleton<ProbeCommandHandler>();
commandServices.AddSingleton<ICommandHandler<ProbeCommand>>(provider =>
    provider.GetRequiredService<ProbeCommandHandler>());
await using (var commandProvider = commandServices.BuildServiceProvider())
{
    var probeHandler = commandProvider.GetRequiredService<ProbeCommandHandler>();
    var commandBus = new InProcessCommandBus(commandProvider);
    await commandBus.SendAsync(new ProbeCommand("scheme-b"));
    Assert(probeHandler.LastValue == "scheme-b",
        "in-process command bus should resolve and execute one typed handler through DI");
}

var interactionStore = new TestPendingInteractionStore();
var interactionManager = new InteractionManager(interactionStore);
var interactionNow = DateTimeOffset.UtcNow;
await interactionManager.RegisterAsync(new PendingInteraction(
    "hard-1", "qq:private:100", "first", InteractionMode.HardWait, 100,
    interactionNow, interactionNow.AddMinutes(1)));
await interactionManager.RegisterAsync(new PendingInteraction(
    "hard-2", "qq:private:100", "second", InteractionMode.HardWait, 100,
    interactionNow.AddSeconds(1), interactionNow.AddMinutes(1)));
await interactionManager.RegisterAsync(new PendingInteraction(
    "soft-1", "qq:private:100", "topic", InteractionMode.SoftExpectation, 100,
    interactionNow, interactionNow.AddMinutes(1)));
var claimedWait = await interactionManager.ClaimHardWaitAsync("qq:private:100");
Assert(claimedWait?.Id == "hard-2" &&
       await interactionManager.ClaimHardWaitAsync("qq:private:100") is null,
    "a new hard wait must replace the old wait and be claimed only once");
Assert((await interactionManager.GetSoftExpectationsAsync("qq:private:100")).Single().Id == "soft-1",
    "soft conversational expectations must coexist without consuming a hard wait");

var fixedMemoryNow = new DateTimeOffset(2026, 7, 23, 6, 0, 0, TimeSpan.Zero);
Assert(MemoryTimeRangeParser.TryParse("你还记得三天前我们聊过什么吗？", out var threeDayRange, fixedMemoryNow) &&
       threeDayRange.StartUtc == new DateTime(2026, 7, 19, 16, 0, 0, DateTimeKind.Utc) &&
       threeDayRange.EndUtcExclusive == new DateTime(2026, 7, 20, 16, 0, 0, DateTimeKind.Utc),
    "Chinese relative-day lookup must use Beijing natural-day boundaries");
Assert(MemoryTimeRangeParser.TryParse("上周我们说过什么", out var lastWeekRange, fixedMemoryNow) &&
       lastWeekRange.StartUtc == new DateTime(2026, 7, 12, 16, 0, 0, DateTimeKind.Utc) &&
       lastWeekRange.EndUtcExclusive == new DateTime(2026, 7, 19, 16, 0, 0, DateTimeKind.Utc),
    "last-week lookup must cover the previous Beijing Monday-to-Monday range");
Assert(MemoryTimeRangeParser.TryParse("一个月前的事情", out var monthRange, fixedMemoryNow) &&
       monthRange.Label == "1个月前",
    "Chinese month offsets must be recognized");

var architectureServices = new ServiceCollection();
architectureServices.AddLogging();
architectureServices.AddHttpClient();
architectureServices.AddHimeData();
architectureServices.AddSingleton<HimeBotService>();
architectureServices.AddSingleton<IGroupMessageSender>(provider =>
    provider.GetRequiredService<HimeBotService>());
architectureServices.AddSingleton<IAccountMessageSender>(provider =>
    provider.GetRequiredService<HimeBotService>());
await using (var architectureProvider = architectureServices.BuildServiceProvider(
                 new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
{
    Assert(architectureProvider.GetRequiredService<MessageCoordinator>() is not null &&
           architectureProvider.GetRequiredService<ICommandBus>() is not null &&
           architectureProvider.GetRequiredService<IInteractionManager>() is not null,
        "scheme B message coordinator, command bus, and interaction manager must resolve without DI cycles");

    var persistentInteractions = architectureProvider.GetRequiredService<IInteractionManager>();
    var persistentScope = $"test:private:{Guid.NewGuid():N}";
    await persistentInteractions.RegisterAsync(new PendingInteraction(
        Guid.NewGuid().ToString("N"),
        persistentScope,
        "persistence-probe",
        InteractionMode.SoftExpectation,
        1,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(1)));
    Assert((await persistentInteractions.GetSoftExpectationsAsync(persistentScope)).Count == 1,
        "LiteDB interaction store should persist and reload active waits");
    Assert(await persistentInteractions.CancelAsync(persistentScope) == 1,
        "LiteDB interaction state should be removable after completion");

    var participantProfiles = architectureProvider.GetRequiredService<ParticipantIdentityService>();
    var contextChat = architectureProvider.GetRequiredService<IChatService>();
    var contextAssembler = architectureProvider.GetRequiredService<ConversationContextAssembler>();
    var personaState = architectureProvider.GetRequiredService<IPersonaStateService>();
    var groupResponses = architectureProvider.GetRequiredService<GroupResponseStateService>();
    var contextGroupId = Math.Abs(Random.Shared.NextInt64(1_000_000_000, 8_000_000_000));
    var currentUserId = Math.Abs(Random.Shared.NextInt64(1_000_000_000, 8_000_000_000));
    var externalBotId = Math.Abs(Random.Shared.NextInt64(1_000_000_000, 8_000_000_000));
    participantProfiles.SetKind("qq", externalBotId, ParticipantKind.ExternalBot, "test");
    contextChat.AppendTurn(
        externalBotId,
        "测试外部机器人",
        "KFC污染消息",
        [],
        "不应进入当前用户上下文",
        [],
        null,
        contextGroupId);
    contextChat.AppendTurn(
        currentUserId,
        "当前测试用户",
        "这是当前用户的有效消息",
        [],
        "这是对应回复",
        [],
        null,
        contextGroupId);
    var assembled = contextAssembler.Build(
        currentUserId,
        "当前测试用户",
        contextGroupId,
        "还记得吗");
    var assembledText = string.Join('\n', assembled.Messages.Select(message => message.Content));
    Assert(assembled.Messages.Count == 1 &&
           assembled.Messages[0].Role == "system" &&
           assembledText.Contains($"qq={currentUserId}", StringComparison.Ordinal) &&
           assembledText.Contains("这是当前用户的有效消息", StringComparison.Ordinal) &&
           !assembledText.Contains("KFC污染消息", StringComparison.Ordinal),
        "group context must retain speaker identity and exclude external-bot turns");

    var contextDatabase = architectureProvider.GetRequiredService<HimeDbContext>();
    var sessions = contextDatabase.Database.GetCollection<ChatSession>("chat_sessions");
    var contextSession = sessions.FindOne(session => session.GroupId == contextGroupId)
        ?? throw new InvalidOperationException("context test session was not persisted");
    contextSession.Messages.AddRange(
    [
        new ChatMessage
        {
            Role = "user",
            UserId = currentUserId,
            Nickname = "当前测试用户",
            GroupId = contextGroupId,
            Content = "前天我们确认了近期原始消息也必须参与时间检索",
            Time = AtBeijingDaysAgo(2)
        },
        new ChatMessage
        {
            Role = "user",
            UserId = currentUserId,
            Nickname = "当前测试用户",
            GroupId = contextGroupId,
            Content = "三天前我们讨论了长期记忆检索的时间边界",
            Time = AtBeijingDaysAgo(3)
        },
        new ChatMessage
        {
            Role = "user",
            UserId = currentUserId,
            Nickname = "当前测试用户",
            GroupId = contextGroupId,
            Content = "七天前我们讨论了多平台适配器",
            Time = AtBeijingDaysAgo(7)
        },
        new ChatMessage
        {
            Role = "user",
            UserId = currentUserId,
            Nickname = "当前测试用户",
            GroupId = contextGroupId,
            Content = "三十天前我们决定保留模块化单体架构",
            Time = AtBeijingDaysAgo(30)
        }
    ]);
    sessions.Upsert(contextSession);

    var memories = contextDatabase.Database.GetCollection<LongTermMemoryRecord>("long_term_memories");
    var isolationGroupId = contextGroupId + 1;
    memories.Upsert(new LongTermMemoryRecord
    {
        Id = Guid.NewGuid().ToString("N"),
        SessionId = $"default:g:{isolationGroupId}",
        GroupId = isolationGroupId,
        UserId = currentUserId,
        Nickname = "当前测试用户",
        Content = "三天前另一个群的秘密内容",
        OccurredAtUtc = AtBeijingDaysAgo(3)
    });
    memories.Upsert(new LongTermMemoryRecord
    {
        Id = Guid.NewGuid().ToString("N"),
        SessionId = $"default:p:{currentUserId + 1}",
        GroupId = null,
        UserId = currentUserId + 1,
        Nickname = "其他私聊用户",
        Content = "三天前其他私聊用户的秘密内容",
        OccurredAtUtc = AtBeijingDaysAgo(3)
    });

    var threeDayContext = string.Join('\n', contextAssembler
        .Build(currentUserId, "当前测试用户", contextGroupId, "你还记得三天前我们聊过什么吗？")
        .Messages.Select(message => message.Content));
    Assert(threeDayContext.Contains("长期记忆检索的时间边界", StringComparison.Ordinal) &&
           !threeDayContext.Contains("近期原始消息也必须参与时间检索", StringComparison.Ordinal) &&
           !threeDayContext.Contains("多平台适配器", StringComparison.Ordinal) &&
           !threeDayContext.Contains("模块化单体架构", StringComparison.Ordinal) &&
           !threeDayContext.Contains("另一个群的秘密内容", StringComparison.Ordinal),
        "three-day recall must select only the requested Beijing day and preserve group isolation");

    var recentRawContext = string.Join('\n', contextAssembler
        .Build(currentUserId, "当前测试用户", contextGroupId, "前天我们聊过什么？")
        .Messages.Select(message => message.Content));
    Assert(recentRawContext.Contains("近期原始消息也必须参与时间检索", StringComparison.Ordinal) &&
           !recentRawContext.Contains("长期记忆检索的时间边界", StringComparison.Ordinal),
        "time lookup must search recent raw messages and archived memories through one path");

    var sevenDayContext = string.Join('\n', contextAssembler
        .Build(currentUserId, "当前测试用户", contextGroupId, "七天前我们聊过什么？")
        .Messages.Select(message => message.Content));
    Assert(sevenDayContext.Contains("多平台适配器", StringComparison.Ordinal) &&
           !sevenDayContext.Contains("长期记忆检索的时间边界", StringComparison.Ordinal),
        "seven-day recall must retrieve the matching archived memory without recent-memory leakage");

    var thirtyDayContext = string.Join('\n', contextAssembler
        .Build(currentUserId, "当前测试用户", contextGroupId, "三十天前我们聊过什么？")
        .Messages.Select(message => message.Content));
    Assert(thirtyDayContext.Contains("模块化单体架构", StringComparison.Ordinal) &&
           !thirtyDayContext.Contains("多平台适配器", StringComparison.Ordinal),
        "thirty-day recall must retrieve memories beyond the compact summary window");

    Assert(personaState.CaptureExplicitFacts(
               currentUserId,
               contextGroupId,
               "我晚上6点下班。对个暗号，我说天王盖地虎，你说啊对对对") == 2,
        "deterministic memory capture should recognize work time and call-response agreements");
    var explicitMemory = personaState.BuildPromptContext(
        currentUserId,
        "当前测试用户",
        contextGroupId,
        "测试群");
    Assert(explicitMemory.Contains("18点00分", StringComparison.Ordinal) &&
           explicitMemory.Contains("天王盖地虎", StringComparison.Ordinal) &&
           explicitMemory.Contains("啊对对对", StringComparison.Ordinal),
        "confirmed explicit facts must be present in the authoritative persona context");

    groupResponses.SetEnabled(contextGroupId, true, currentUserId, "测试群", "test");
    var responseLease = groupResponses.TryCapture(contextGroupId);
    Assert(responseLease.HasValue && groupResponses.CanDeliver(responseLease.Value),
        "an enabled group should issue a deliverable response generation lease");
    groupResponses.SetEnabled(contextGroupId, false, currentUserId, "测试群", "test");
    groupResponses.SetEnabled(contextGroupId, true, currentUserId, "测试群", "test");
    Assert(!groupResponses.CanDeliver(responseLease!.Value),
        "stop and re-enable must not revive work from an older response generation");
    contextChat.Clear(currentUserId, contextGroupId);
    memories.DeleteMany(memory =>
        memory.GroupId == isolationGroupId ||
        (memory.GroupId == null && memory.UserId == currentUserId + 1));
}

Console.WriteLine("PASS: intelligence routing, context limits, and local vision checks");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static DateTime AtBeijingDaysAgo(int days)
{
    var beijingNow = TimeZoneInfo.ConvertTime(
        DateTimeOffset.UtcNow,
        MemoryTimeRangeParser.BeijingTimeZone);
    var local = DateTime.SpecifyKind(
        beijingNow.Date.AddDays(-days),
        DateTimeKind.Unspecified);
    return TimeZoneInfo.ConvertTimeToUtc(local, MemoryTimeRangeParser.BeijingTimeZone);
}

file sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

file sealed class TestAiClient(string response = "") : IAiClient
{
    public Task<string> ChatAsync(
        IReadOnlyList<ChatMessage> history,
        long senderId,
        CancellationToken ct = default,
        bool applyBoundPersona = true,
        AiRequestProfile? requestProfile = null) => Task.FromResult(response);
}

file sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

file sealed class QueueHttpMessageHandler(params string[] responses) : HttpMessageHandler
{
    private readonly Queue<string> _responses = new(responses);

    public List<Uri> RequestUris { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUris.Add(request.RequestUri ?? throw new InvalidOperationException("Request URI is required"));
        var payload = _responses.Count > 0 ? _responses.Dequeue() : "{\"error\":\"no test response\",\"data\":[]}";
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        });
    }
}

file sealed record ProbeCommand(string Value) : ICommand;

file sealed class ProbeCommandHandler : ICommandHandler<ProbeCommand>
{
    public string? LastValue { get; private set; }

    public Task HandleAsync(ProbeCommand command, CancellationToken cancellationToken)
    {
        LastValue = command.Value;
        return Task.CompletedTask;
    }
}

file sealed class TestPendingInteractionStore : IPendingInteractionStore
{
    private readonly Dictionary<string, PendingInteraction> _items = new(StringComparer.Ordinal);

    public IReadOnlyList<PendingInteraction> GetActive(string scopeKey, DateTimeOffset now)
    {
        foreach (var expired in _items.Values.Where(item => item.ExpiresAt <= now).ToArray())
            _items.Remove(expired.Id);
        return _items.Values
            .Where(item => item.ScopeKey == scopeKey)
            .OrderBy(item => item.CreatedAt)
            .ToArray();
    }

    public void Save(PendingInteraction interaction) => _items[interaction.Id] = interaction;

    public void Remove(string id) => _items.Remove(id);

    public int RemoveScope(string scopeKey, InteractionMode? mode = null)
    {
        var matching = _items.Values
            .Where(item => item.ScopeKey == scopeKey && (!mode.HasValue || item.Mode == mode.Value))
            .Select(item => item.Id)
            .ToArray();
        foreach (var id in matching)
            _items.Remove(id);
        return matching.Length;
    }
}
