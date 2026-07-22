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
    "config/core.json",
    "config/ai.json",
    "config/conversation.json",
    "config/stickers.json",
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
    "AI", "OpenCodeAgent", "ModelRouting", "Admin", "Personas", "ChatHistory", "RelationshipTrajectory",
    "Images", "GroupStickers", "AnimeTagger", "StickerTags", "VoiceSynthesis",
    "Music", "GsCore", "MessageDispatch", "ReplyScheduling"
];
Assert(requiredSections.All(section => configuration.GetSection(section).Exists()),
    "all required modular configuration sections should be present");
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
Assert(!configuration.GetValue<bool>("TargetedInteraction:Enabled"),
    "targeted interaction without configured targets should remain disabled");
Assert(configuration["Personas:Version"] == "yangyang-v3-dynamic" &&
       configuration["RelationshipTrajectory:SchemaVersion"] == "v3" &&
       !configuration.GetValue<bool>("RelationshipTrajectory:ImportLegacyData"),
    "the clean persona generation must use an isolated v3 relationship trajectory without legacy import");

var router = new ConversationRouter(Options.Create(new ModelRoutingOptions
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
Assert(sadSticker == sadCatalogSticker,
    "base emotion pools must resolve from catalog metadata rather than file-name prefixes");

File.Delete(stickerCatalogPath);

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

var architectureServices = new ServiceCollection();
architectureServices.AddLogging();
architectureServices.AddHttpClient();
architectureServices.AddHimeData();
architectureServices.AddSingleton<HimeBotService>();
architectureServices.AddSingleton<IGroupMessageSender>(provider =>
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
}

Console.WriteLine("PASS: intelligence routing, context limits, and local vision checks");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
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
