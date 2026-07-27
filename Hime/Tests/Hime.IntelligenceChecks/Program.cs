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
using Sora.Core.Enums;

string[] configurationFiles =
[
    "config/accounts.json",
    "config/core.json",
    "config/ai.json",
    "config/tools.json",
    "config/conversation.json",
    "config/social-intelligence.json",
    "config/stickers.json",
    "config/sticker-labels.json",
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
    "BotAccounts", "AI", "OpenCodeAgent", "AgentTools", "ModelRouting", "ResponsePolicies", "DialoguePlanning", "EmotionalPragmatics", "SocialIntelligence", "GroupSceneAwareness", "GroupChatInvestigator", "ForwardMessageIngest", "Admin", "Personas", "PersonaCorpusRouting", "PersonaPresence", "PersonaComplianceRules", "ChatHistory", "RelationshipTrajectory", "RelationshipLanguage", "ConversationFocus",
    "Images", "GroupStickers", "AnimeTagger", "StickerTags", "StickerLabels", "Gallery", "VoiceSynthesis",
    "OneBot", "Setu", "Music", "GsCore", "MessageDispatch", "ReplyScheduling"
];
Assert(requiredSections.All(section => configuration.GetSection(section).Exists()),
    "all required modular configuration sections should be present");
Assert(!configuration.AsEnumerable().Any(item =>
        item.Key.EndsWith(":AllowedGroupIds", StringComparison.OrdinalIgnoreCase)),
    "group response authorization must live in LiteDB instead of configuration allow lists");
Assert(PlatformSendResultInspector.TryGetMessageId(new
    {
        Data = new
        {
            MessageId = 987654321L
        }
    }) == 987654321L,
    "platform send result inspector should extract nested message ids");

var botAccounts = configuration.GetSection("BotAccounts").Get<BotAccountsOptions>()
    ?? throw new InvalidOperationException("BotAccounts configuration did not bind.");
var enabledBotAccounts = botAccounts.GetEnabledConnections();
Assert(enabledBotAccounts.Count > 0 && enabledBotAccounts.Count(account => account.IsPrimary) == 1,
    "bot account configuration should expose one enabled primary connection");
Assert(new BotAccountsOptions().GetEnabledConnections().Single().Port == 3010,
    "missing account configuration should preserve the legacy Milky 3010 fallback");

var configuredSetu = configuration.GetSection("Setu").Get<SetuOptions>()
    ?? throw new InvalidOperationException("Setu configuration should bind.");
Assert(configuredSetu.IsValid(), "Setu runtime behavior must be fully configuration-backed.");
Assert(SetuIntentInterpreter.TryParseDeterministic("来3张萝莉 白丝涩图", 10, configuredSetu, out var taggedSetu) &&
       taggedSetu.Count == 3 &&
       taggedSetu.Source == SetuSourceMode.Lolicon &&
       taggedSetu.Tags.Contains("萝莉") &&
       taggedSetu.Tags.Contains("白丝"),
    "tagged setu command should parse count and multiple tags");
Assert(SetuIntentInterpreter.TryParseDeterministic("来三张色图", 10, configuredSetu, out var chineseCountSetu) &&
       chineseCountSetu.Count == 3 &&
       chineseCountSetu.Source == SetuSourceMode.Lolicon,
    "Chinese image count should be understood");
Assert(SetuIntentInterpreter.TryParseDeterministic("随机涩图", 10, configuredSetu, out var randomSetu) &&
       randomSetu.Source == SetuSourceMode.Random,
    "explicit random request should use DMOE or LoliAPI");
Assert(SetuIntentInterpreter.TryParseDeterministic("想看随机色图", 10, configuredSetu, out var naturalRandomSetu) &&
       naturalRandomSetu.Source == SetuSourceMode.Random,
    "natural random image requests should still use a random provider");
Assert(SetuIntentInterpreter.TryParseDeterministic("想看不一样的涩图", 10, configuredSetu, out var differentRandomSetu) &&
       differentRandomSetu.Source == SetuSourceMode.Random,
    "generic variety image requests should not become empty Lolicon tag searches");
Assert(SetuIntentInterpreter.TryParseDeterministic("要涩图", 10, configuredSetu, out var genericRandomSetu) &&
       genericRandomSetu.Source == SetuSourceMode.Random,
    "generic 要涩图 request should use a random provider");
Assert(SetuIntentInterpreter.TryParseDeterministic("我想看鸣潮的图", 10, configuredSetu, out var naturalSetu) &&
       naturalSetu.Source == SetuSourceMode.Lolicon &&
       naturalSetu.Tags.Contains("鸣潮"),
    "natural franchise image request should become a tagged Lolicon request");
Assert(!SetuIntentInterpreter.TryParseDeterministic("我想看这张图片里的内容", 10, configuredSetu, out _),
    "ordinary visual questions must not trigger the image provider");
Assert(!SetuIntentInterpreter.TryParseDeterministic("为什么总发涩图", 10, configuredSetu, out _),
    "complaints about prior images must not be treated as a new image request");
Assert(SetuIntentInterpreter.TryParseDeterministic("来张 R18 涩图", 10, configuredSetu, out var rejectedUnsafeSetu) &&
       rejectedUnsafeSetu.RejectedUnsafe,
    "R18 image requests must be rejected before any external API call");
var configuredForwardIngest = configuration.GetSection("ForwardMessageIngest").Get<ForwardMessageIngestOptions>()
    ?? throw new InvalidOperationException("ForwardMessageIngest configuration should bind.");
Assert(configuredForwardIngest.IsValid() &&
       configuredForwardIngest.MaxNodesPerForward <= 100 &&
       !string.IsNullOrWhiteSpace(configuredForwardIngest.ImagePlaceholder),
    "merged-forward ingest must be bounded and configuration-backed");

var setuUriBuilder = new SetuApiService(
    new SingleHttpClientFactory(new HttpClient()),
    Options.Create(configuredSetu),
    NullLogger<SetuApiService>.Instance);
var tagRequestUri = setuUriBuilder.BuildLoliconUri(
    "https://api.lolicon.app/setu/v2",
    3,
    ["萝莉", "白丝"],
    useKeyword: false).AbsoluteUri;
Assert(tagRequestUri.Contains("r18=0", StringComparison.Ordinal) &&
       tagRequestUri.Contains("excludeAI=true", StringComparison.Ordinal) &&
       tagRequestUri.Contains("size=original", StringComparison.Ordinal) &&
       tagRequestUri.Split("tag=", StringSplitOptions.None).Length == 3,
    "Lolicon tag request must enforce SFW, non-AI original images and repeated AND tags");
var keywordRequestUri = setuUriBuilder.BuildLoliconUri(
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
    Options.Create(configuredSetu),
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
       configuration["AI:BaseUrl"] == "https://open.bigmodel.cn/api/paas/v4" &&
       configuration["AI:Model"] == "glm-5.2",
    "the direct cloud fallback should use GLM 5.2");
using (var openCodeConfig = JsonDocument.Parse(File.ReadAllText("opencode.json")))
{
    var rootModel = openCodeConfig.RootElement.GetProperty("model").GetString();
    var agentModel = openCodeConfig.RootElement.GetProperty("agent").GetProperty("hime-qq").GetProperty("model").GetString();
    Assert(rootModel == "hime-glm/glm-5.2" && agentModel == rootModel,
        "OpenCode root and hime-qq agent should both use GLM 5.2");
    var rootPermission = openCodeConfig.RootElement.GetProperty("permission");
    var agentPermission = openCodeConfig.RootElement.GetProperty("agent").GetProperty("hime-qq").GetProperty("permission");
    Assert(rootPermission.GetProperty("websearch").GetString() == "allow" &&
           agentPermission.GetProperty("websearch").GetString() == "allow" &&
           rootPermission.GetProperty("hime_web_read").GetString() == "allow" &&
           agentPermission.GetProperty("hime_web_read").GetString() == "allow" &&
           rootPermission.GetProperty("webfetch").GetString() == "deny" &&
           agentPermission.GetProperty("webfetch").GetString() == "deny",
        "OpenCode should allow configured safe knowledge tools while keeping arbitrary page fetch disabled");
}

var configuredAgentTools = configuration.GetSection("AgentTools").Get<AgentToolsOptions>()
    ?? throw new InvalidOperationException("AgentTools configuration should bind");
var enabledAgentTools = configuredAgentTools.EnabledDefinitions()
    .Select(item => item.Name)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
Assert(enabledAgentTools.SetEquals(["hime_sticker_search", "websearch", "hime_web_read"]) &&
       configuration.GetValue<bool>("AgentTools:WebRead:Enabled") &&
       configuration.GetValue<int>("AgentTools:WebRead:MaxResponseBytes") <= 4 * 1024 * 1024 &&
       configuration.GetSection("AgentTools:WebRead:AllowedPorts").Get<int[]>() is { Length: > 0 } allowedPorts &&
       allowedPorts.All(port => port is 80 or 443),
    "agent tools must be enabled from config and web reading must remain size/port restricted");

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
var configuredVoiceSynthesis =
    configuration.GetSection("VoiceSynthesis").Get<VoiceSynthesisOptions>()
    ?? throw new InvalidOperationException("VoiceSynthesis configuration should bind");
Assert(configuredVoiceSynthesis.IsValid() &&
       configuredVoiceSynthesis.IndexTts.EmotionVectors.Count >= 10,
    "IndexTTS2 dynamic emotion aliases and eight-dimensional vectors must come from configuration");
Assert(!configuration.GetSection("TargetedInteraction").Exists() &&
       !configuration.GetSection("ImplicitAddress").Exists(),
    "superseded targeted-interaction and implicit-address configuration must stay removed");
Assert(configuration["Personas:Version"] == "yangyang-v3-dynamic" &&
       configuration["RelationshipTrajectory:SchemaVersion"] == "v3" &&
       !configuration.GetValue<bool>("RelationshipTrajectory:ImportLegacyData"),
    "the clean persona generation must use an isolated v3 relationship trajectory without legacy import");
var configuredRelationshipLanguage =
    configuration.GetSection("RelationshipLanguage").Get<RelationshipLanguageOptions>()
    ?? throw new InvalidOperationException("RelationshipLanguage configuration should bind");
var configuredFocus =
    configuration.GetSection("ConversationFocus").Get<ConversationFocusOptions>()
    ?? throw new InvalidOperationException("ConversationFocus configuration should bind");
Assert(configuredRelationshipLanguage.IsValid() &&
       configuredFocus.IsValid() &&
       configuredFocus.SemanticFallbackEnabled,
    "dynamic relationship vocabulary and semantic conversation focus must be enabled");
var configuredSocialIntelligence =
    configuration.GetSection("SocialIntelligence").Get<SocialIntelligenceOptions>()
    ?? throw new InvalidOperationException("SocialIntelligence configuration should bind");
Assert(configuredSocialIntelligence.IsValid() &&
       configuredSocialIntelligence.IntentRules.Count >= 5 &&
       configuredSocialIntelligence.ReplyMoveChoices.Count >= 5 &&
       configuredSocialIntelligence.CandidateJudge.PenaltyRules.Count >= 3,
    "social intelligence must be configuration-backed and expose runtime-editable intent, reply-move and judge rules");
var configuredGroupSceneAwareness =
    configuration.GetSection("GroupSceneAwareness").Get<GroupSceneAwarenessOptions>()
    ?? throw new InvalidOperationException("GroupSceneAwareness configuration should bind");
Assert(configuredGroupSceneAwareness.IsValid() &&
       configuredGroupSceneAwareness.EventRules.Any(rule => rule.Id == "naming_review") &&
       configuredGroupSceneAwareness.EventRules.Any(rule => rule.Id == "followup_explain"),
    "group scene awareness must be configuration-backed and expose dynamic scene rules");
var configuredGroupChatInvestigator =
    configuration.GetSection("GroupChatInvestigator").Get<GroupChatInvestigatorOptions>()
    ?? throw new InvalidOperationException("GroupChatInvestigator configuration should bind");
Assert(configuredGroupChatInvestigator.IsValid() &&
       configuredGroupChatInvestigator.RequestMarkers.Count > 0 &&
       configuredGroupChatInvestigator.CommonPromptRules.Count > 0,
    "group chat investigation must be configuration-backed instead of hardcoded prompt matching");
var relationshipSocialRule = configuredSocialIntelligence.IntentRules
    .Single(rule => rule.Id == "relationship_tease");
var socialIntentAnalyzer = new SocialIntentAnalyzer(
    new TestOptionsMonitor<SocialIntelligenceOptions>(configuredSocialIntelligence));
var analyzedRelationshipIntent = socialIntentAnalyzer.Analyze(
    relationshipSocialRule.Markers.First(),
    new DialogueDecision(
        DialogueAct.Relationship,
        IncludeRelationshipContext: true,
        IncludePersonaState: true,
        IncludePlotKnowledge: false,
        IncludeCadenceExamples: true,
        IncludeTrustedClock: false,
        "test relationship cue"),
    new ConversationRoute(ConversationMode.Casual, "test", AllowDecorativeMedia: true));
Assert(analyzedRelationshipIntent.IntentId == relationshipSocialRule.Id &&
       analyzedRelationshipIntent.Posture == relationshipSocialRule.Posture &&
       analyzedRelationshipIntent.MatchedMarkers.Count == 1,
    "social intent detection should use configured markers and posture instead of hardcoded prompt.Contains branches");
var emotionalNeedIntent = socialIntentAnalyzer.Analyze(
    "今天下雨，所有人都有伞，就我没有",
    new DialogueDecision(
        DialogueAct.Support,
        IncludeRelationshipContext: false,
        IncludePersonaState: true,
        IncludePlotKnowledge: false,
        IncludeCadenceExamples: true,
        IncludeTrustedClock: false,
        "test emotional support cue"),
    new ConversationRoute(ConversationMode.Casual, "test", AllowDecorativeMedia: true));
Assert(emotionalNeedIntent.IntentId == "emotional_need" &&
       emotionalNeedIntent.SocialAction == DialogueAct.Support.ToString(),
    "ordinary social exclusion should be classified as a support turn");
Assert(socialIntentAnalyzer.Analyze(
        "我今天被群友晾着了",
        new DialogueDecision(DialogueAct.Support, false, true, false, true, false, "test"),
        new ConversationRoute(ConversationMode.Casual, "test", AllowDecorativeMedia: true)).IntentId == "emotional_need",
    "being ignored by the group should be recognized as an emotional need");
Assert(socialIntentAnalyzer.Analyze(
        "不是这个意思，你刚才理解错了",
        new DialogueDecision(DialogueAct.Repair, false, false, false, false, false, "test"),
        new ConversationRoute(ConversationMode.Casual, "test", AllowDecorativeMedia: true)).IntentId == "repair_or_quality_feedback",
    "user correction should be classified as a repair turn");

var configuredResponsePolicies = configuration.GetSection("ResponsePolicies").Get<ResponsePolicyOptions>()
    ?? throw new InvalidOperationException("ResponsePolicies configuration should bind");
var configuredStickerLabels = configuration.GetSection("StickerLabels").Get<StickerLabelVocabularyOptions>()
    ?? throw new InvalidOperationException("StickerLabels configuration should bind");
using var stickerLabels = new StickerLabelVocabulary(
    new TestOptionsMonitor<StickerLabelVocabularyOptions>(configuredStickerLabels),
    NullLogger<StickerLabelVocabulary>.Instance);
var stickerRequestParser = new StickerRequestParser(stickerLabels);
var router = new ConversationRouter(
    new TestOptionsMonitor<ModelRoutingOptions>(new ModelRoutingOptions
    {
        Enabled = true,
        UseHighCapabilityForTechnical = true,
        ComplexPromptMinCharacters = 120,
        HighCapabilityProviderId = "hime-glm",
        HighCapabilityModelId = "glm-5.2"
    }),
    new TestOptionsMonitor<ResponsePolicyOptions>(configuredResponsePolicies));

Assert(router.Route("hello").Mode == ConversationMode.Casual, "ordinary chat should be casual");
Assert(router.Route("opencode configuration check").Mode == ConversationMode.Technical, "technical marker should select technical mode");
var technical = router.Route("opencode configuration check");
Assert(!technical.AllowDecorativeMedia, "technical mode must disable decorative media");
Assert(router.SelectModel(technical, "opencode configuration check").ModelId == "glm-5.2", "technical mode should select GLM 5.2");
Assert(!router.SelectModel(router.Route("hello"), "hello").HasExplicitModel, "ordinary chat must keep default model");
var nuancedSocialPrompt = "我和朋友因为一场误会吵架了，现在既难过又有点后悔，不知道该怎么把真正想说的话讲清楚。";
Assert(router.SelectModel(router.Route(nuancedSocialPrompt), nuancedSocialPrompt).ModelId == "glm-5.2",
    "nuanced emotional conversation should select the higher-capability model");

var configuredEmotionalPragmatics =
    configuration.GetSection("EmotionalPragmatics").Get<EmotionalPragmaticsOptions>()
    ?? throw new InvalidOperationException("EmotionalPragmatics configuration should bind");
var configuredDialoguePlanning =
    configuration.GetSection("DialoguePlanning").Get<DialoguePlanningOptions>()
    ?? throw new InvalidOperationException("DialoguePlanning configuration should bind");
Assert(configuredDialoguePlanning.IsValid(),
    "dialogue planning question, repair, distress and relationship markers must be configuration-driven");
var emotionalPragmaticsOptions =
    new TestOptionsMonitor<EmotionalPragmaticsOptions>(configuredEmotionalPragmatics);
var emotionalPlanner = new EmotionalPragmaticsPlanner(emotionalPragmaticsOptions);
var exclusionPlan = emotionalPlanner.Plan(
    "今天下雨，所有人都有伞，就我没有",
    HimeStyleScene.PrivateReply);
Assert(exclusionPlan.IsActive &&
       exclusionPlan.NeedsSupport &&
       exclusionPlan.AvoidAdvice &&
       exclusionPlan.RestrictMediaIntensity &&
       exclusionPlan.Cues.Contains("social-exclusion") &&
       exclusionPlan.AllowedMediaEmotions.Contains("comforting"),
    "social comparison should be treated as a possible emotional bid rather than a logistics-only question");
var sighPlan = emotionalPlanner.Plan("唉……", HimeStyleScene.PrivateReply);
Assert(sighPlan.UseRecentContext &&
       sighPlan.AvoidQuestions &&
       sighPlan.AvoidAdvice &&
       sighPlan.Cues.Contains("low-information-affect"),
    "a short sigh should use recent context and leave room instead of interrogating or advising");
var adviceOptOutPlan = emotionalPlanner.Plan(
    "我心情不好，但你别分析原因，也别给建议，陪我说两句就好",
    HimeStyleScene.PrivateReply);
Assert(adviceOptOutPlan.AvoidAdvice &&
       adviceOptOutPlan.Instruction.Contains("explicitly declined analysis or advice", StringComparison.Ordinal),
    "an explicit request for companionship must suppress disguised analysis and advice");
var mixedAffectPlan = emotionalPlanner.Plan(
    "我考第一了，但是最想告诉的人已经不在了",
    HimeStyleScene.PrivateReply);
Assert(mixedAffectPlan.Cues.Contains("mixed-affect") &&
       mixedAffectPlan.Instruction.Contains("Hold the positive event and painful meaning together", StringComparison.Ordinal),
    "mixed positive and painful meaning should not collapse into congratulations");
Assert(!emotionalPlanner.GetEmptyMentionReply().Contains("请告诉我", StringComparison.Ordinal) &&
       !emotionalPlanner.GetEmptyMentionReply().Contains("随时", StringComparison.Ordinal),
    "an empty group mention should sound like a conversational acknowledgement rather than customer service");

var configuredStyle = configuration.GetSection("ConversationStyle").Get<ConversationStyleOptions>()
    ?? throw new InvalidOperationException("ConversationStyle configuration should bind");
configuredStyle.ProfileFile =
    Path.Combine(Directory.GetCurrentDirectory(), "personas", "yangyang-style-card.md");
configuredStyle.MaxExamplesPerPrompt = 3;
var styleOptions = new TestOptionsMonitor<ConversationStyleOptions>(configuredStyle);
var configuredPersona = configuration.GetSection("Personas").Get<PersonaOptions>()
    ?? throw new InvalidOperationException("Personas configuration should bind");
configuredPersona.Version = "yangyang-v1";
configuredPersona.CorpusFile =
    Path.Combine(Directory.GetCurrentDirectory(), "data", "personas", "yangyang-lines.jsonl");
configuredPersona.PlotKnowledgeFile =
    Path.Combine(Directory.GetCurrentDirectory(), "data", "personas", "yangyang-plot-events.jsonl");
Assert(configuredPersona.IsValid(),
    "persona identity, plot routing and final runtime rules must be configuration-driven");
var personaOptions = new TestOptionsMonitor<PersonaOptions>(configuredPersona);
var configuredPersonaPresence =
    configuration.GetSection("PersonaPresence").Get<PersonaPresenceOptions>()
    ?? throw new InvalidOperationException("PersonaPresence configuration should bind");
var configuredCorpusRouting =
    configuration.GetSection("PersonaCorpusRouting").Get<PersonaCorpusRoutingOptions>()
    ?? throw new InvalidOperationException("PersonaCorpusRouting configuration should bind");
Assert(configuredCorpusRouting.IsValid(),
    "persona corpus scene and emotion routing must be configuration-driven");
var corpusRoutingOptions =
    new TestOptionsMonitor<PersonaCorpusRoutingOptions>(configuredCorpusRouting);
var configuredComplianceRules =
    configuration.GetSection("PersonaComplianceRules").Get<PersonaComplianceRuleOptions>()
    ?? throw new InvalidOperationException("PersonaComplianceRules configuration should bind");
Assert(configuredComplianceRules.IsValid(),
    "dynamic persona compliance rules and fallbacks must be valid");
var complianceRuleOptions =
    new TestOptionsMonitor<PersonaComplianceRuleOptions>(configuredComplianceRules);
var relationshipLanguageOptions =
    new TestOptionsMonitor<RelationshipLanguageOptions>(configuredRelationshipLanguage);
var personaPresence = new PersonaPresenceService(
    new TestOptionsMonitor<PersonaPresenceOptions>(configuredPersonaPresence));
Assert(personaPresence.Assess(
            "公开能查到的信息可以试试，但涉及隐私的数据就不行。",
            "公开能查到的信息可以试试，但涉及隐私的数据就不行。",
            casual: true).RequiresRewrite,
    "a reply that merely echoes the user must be rejected as persona-absent");
Assert(personaPresence.Assess(
            "根据规定，我的权限是查询公开信息。",
            "你能查信息吗",
            casual: true).RequiresRewrite,
    "internal policy prose must be rejected before it reaches the user");
Assert(!personaPresence.Assess(
            "嗯？我在呢。",
            "可以帮我查信息吗",
            casual: true).RequiresRewrite,
    "a concise natural acknowledgement must not require a catchphrase");
var runtimeProfile = new PersonaRuntimeProfileService(personaOptions, styleOptions);
var runtimeInstruction = runtimeProfile.BuildFinalInstruction(
    "配置驱动人格测试",
    allowEmotionMarker: false);
Assert(configuredPersona.RuntimeIdentityRules.All(rule =>
           runtimeInstruction.Contains(rule, StringComparison.Ordinal)) &&
       configuredPersona.RuntimeGuardRules.All(rule =>
           runtimeInstruction.Contains(rule, StringComparison.Ordinal)),
    "the final persona lock must consume hot-reloadable identity and guard rules instead of compiled character prose");
var emotionalRewriteClient = new TestAiClient("只有自己没伞，这种被落下的感觉确实不好受。");
var emotionalReplyRefinement = new EmotionalReplyRefinementService(
    emotionalRewriteClient,
    runtimeProfile,
    emotionalPragmaticsOptions,
    NullLogger<EmotionalReplyRefinementService>.Instance);
Assert(emotionalReplyRefinement.Assess(
        "往我这边靠靠吧，伞够两个人用的。",
        "今天下雨，所有人都有伞，就我没有。",
        exclusionPlan).RequiresRewrite,
    "an emotional reply must not invent a shared umbrella or physical action");
Assert(emotionalReplyRefinement.Assess(
        "拿第一很不容易，他一定都看在眼里的，你先歇会儿吧。",
        "我终于拿了第一，可最想告诉的人已经不在了。",
        mixedAffectPlan).Reasons.Count >= 2,
    "unsupported third-party certainty and unsolicited advice should both be detected");
Assert(!emotionalReplyRefinement.Assess(
        "只有自己没伞，这种被落下的感觉确实不好受。",
        "今天下雨，所有人都有伞，就我没有。",
        exclusionPlan).RequiresRewrite,
    "a grounded acknowledgement should remain single-call");
var emotionallyRewritten = await emotionalReplyRefinement.RefineIfNeededAsync(
    "往我这边靠靠吧，伞够两个人用的。",
    "今天下雨，所有人都有伞，就我没有。",
    "私聊回复",
    10001,
    exclusionPlan);
Assert(emotionallyRewritten == "只有自己没伞，这种被落下的感觉确实不好受。" &&
       emotionalRewriteClient.Calls == 1,
    "only a high-confidence emotional defect should trigger one bounded rewrite");
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
Assert(technicalStyle.Contains("active persona", StringComparison.OrdinalIgnoreCase) &&
       technicalStyle.Contains("Accuracy constrains the claims", StringComparison.Ordinal) &&
       !technicalStyle.Contains("Scene examples", StringComparison.Ordinal),
    "technical replies must preserve persona identity while keeping claims precise and non-decorative");
Assert(styleCard.BuildProactivePlannerInstruction().Contains("JSON", StringComparison.Ordinal),
    "proactive planner should receive a JSON-safe delivery instruction");
Assert(styleCard.BuildProactivePlannerInstruction().Contains("Simplified Chinese only", StringComparison.Ordinal),
    "proactive planner should share the active Yangyang language contract");
var personaCorpus = new PersonaCorpusService(
    personaOptions,
    corpusRoutingOptions,
    NullLogger<PersonaCorpusService>.Instance);
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
    complianceRuleOptions,
    relationshipLanguageOptions,
    NullLogger<PersonaComplianceService>.Instance,
    personaPresence);
var echoedReply = compliance.Evaluate(
    "公开能查到的信息可以试试，但涉及隐私的数据就不行。",
    userPrompt: "公开能查到的信息可以试试，但涉及隐私的数据就不行。");
Assert(echoedReply.Issues.HasFlag(PersonaComplianceIssues.PersonaAbsent),
    "persona-presence findings must participate in the final compliance decision");
var genericReply = compliance.Evaluate("这个话题就不接了。", userPrompt: "飞机杯");
Assert(genericReply.Issues.HasFlag(PersonaComplianceIssues.GenericServiceTone),
    "a generic refusal must be identified as service-tone wording rather than accepted as persona dialogue");
var naturalRewriteClient = new TestAiClient("这个嘛……你突然问得这么直接，我一时还真不知道该怎么接。");
var naturalRewriteCompliance = new PersonaComplianceService(
    naturalRewriteClient,
    personaOptions,
    runtimeProfile,
    personaCorpus,
    plotKnowledge,
    complianceRuleOptions,
    relationshipLanguageOptions,
    NullLogger<PersonaComplianceService>.Instance);
var naturalRewrite = await naturalRewriteCompliance.RefineIfNeededAsync(
    "这个话题就不接了。",
    "飞机杯",
    "群聊回复",
    10001,
    casual: true,
    requireEmotionMarker: false);
Assert(naturalRewriteClient.Calls == 1 &&
       naturalRewrite.StartsWith("这个嘛", StringComparison.Ordinal),
    "generic refusal wording must trigger one bounded natural-persona rewrite");
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
var inlineRoleLeakReply = compliance.Evaluate(
    "[USER deOne]为什么不直接回答？",
    userPrompt: "秧秧做我老婆");
Assert(inlineRoleLeakReply.Reasons.Contains("泄露对话角色标签"),
    "inline text after a serialized role label must still be rejected");
var countedRepetitionReply = compliance.Evaluate(
    "你说了三遍了，我又不是没听见。这种事不是光喊着就能算数的。",
    userPrompt: "秧秧做我老婆");
Assert(countedRepetitionReply.Reasons.Contains("把重复关系请求写成训话或终止对话"),
    "numeric repetition reports and scolding must trigger a relationship rewrite");
var continuityCompliance = new PersonaComplianceService(
    new TestAiClient("……你今天怎么一直惦记着这个称呼？是有什么话想和我说吗？"),
    personaOptions,
    runtimeProfile,
    personaCorpus,
    plotKnowledge,
    complianceRuleOptions,
    relationshipLanguageOptions,
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
var countedRepetitionRewrite = await continuityCompliance.RefineIfNeededAsync(
    "你说了三遍了，我又不是没听见。这种事不是光喊着就能算数的。",
    "秧秧做我老婆",
    "私聊回复",
    10001,
    casual: true,
    requireEmotionMarker: false,
    repeatedCurrentMessageCount: 3);
Assert(countedRepetitionRewrite.Contains("一直惦记着这个称呼", StringComparison.Ordinal) &&
       !countedRepetitionRewrite.Contains("三遍", StringComparison.Ordinal) &&
       !countedRepetitionRewrite.Contains("不是没听见", StringComparison.Ordinal),
    "a counted, scolding repetition reply should be rewritten into a natural continuation");
var localComplianceClient = new TestAiClient("不应调用第二次模型");
var localCompliance = new PersonaComplianceService(
    localComplianceClient,
    personaOptions,
    runtimeProfile,
    personaCorpus,
    plotKnowledge,
    complianceRuleOptions,
    relationshipLanguageOptions,
    NullLogger<PersonaComplianceService>.Instance);
var locallyCleaned = await localCompliance.RefineIfNeededAsync(
    "（微微点头）这个问题我明白了。",
    "你明白了吗",
    "私聊回复",
    10001,
    casual: true,
    requireEmotionMarker: false);
Assert(locallyCleaned == "这个问题我明白了。" && localComplianceClient.Calls == 0,
    "stage directions should be removed locally without a second model request");
var naturalRepeatedReply = await localCompliance.RefineIfNeededAsync(
    "好啦，我知道你的意思了。",
    "秧秧做我老婆",
    "私聊回复",
    10001,
    casual: true,
    requireEmotionMarker: false,
    repeatedCurrentMessageCount: 2);
Assert(naturalRepeatedReply == "好啦，我知道你的意思了。" && localComplianceClient.Calls == 0,
    "a natural repeated reply must not be rewritten merely because it lacks a canned continuity phrase");
Assert(configuredComplianceRules.RelationshipFallbacks.SecondRequest ==
       "……你今天怎么一直惦记着这个称呼？是有什么话想和我说吗？",
    "the dynamic second-repeat fallback should notice repetition without forcing a scene");
Assert(configuredComplianceRules.RelationshipFallbacks.LaterRequest ==
       "还来呀……我已经听见了。",
    "the dynamic later-repeat fallback should remain open without inventing an activity");
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
           new TestOptionsMonitor<RelationshipLanguageOptions>(configuredRelationshipLanguage),
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

Assert(stickerRequestParser.GetRequestedCount("发3个表情包我看看") == 3, "three-sticker request should be detected");
Assert(stickerRequestParser.GetRequestedCount("发送三个情绪标签") == 3, "Chinese-number sticker request should be detected");
Assert(stickerRequestParser.GetRequestedCount("来两张表情包") == 2, "Chinese two-sticker request should be detected");
Assert(stickerRequestParser.GetRequestedCount("给我什个表情") == 3, "colloquial three-sticker request should be detected");
Assert(stickerRequestParser.GetRequestedCount("三连表情包") == 3, "three-in-a-row sticker request should be detected");
Assert(stickerRequestParser.GetRequestedCount("发一个忧伤的表情包") == 1, "single sticker request should be detected");
Assert(stickerRequestParser.GetRequestedCount("来个不开心的表情包") == 1,
    "implicit single sticker requests should be detected without an explicit number");
Assert(stickerRequestParser.GetRequestedEmotions("发一个忧伤的表情包").SequenceEqual(["sad"]),
    "sad sticker request should select sad emotion");
Assert(stickerRequestParser.GetRequestedEmotions("发一个开心的表情包").SequenceEqual(["happy"]),
    "happy sticker request should select happy emotion");
var compoundStickerEmotions = stickerRequestParser.GetRequestedEmotions("发一个又委屈又有点生气的表情包");
Assert(compoundStickerEmotions.SequenceEqual(["sad", "angry"], StringComparer.OrdinalIgnoreCase),
    "compound sticker requests must preserve every explicitly mentioned emotion in user order");
var negativeHappyEmotions = stickerRequestParser.GetRequestedEmotions("发一个不开心的表情包");
Assert(negativeHappyEmotions.SequenceEqual(["sad"], StringComparer.OrdinalIgnoreCase),
    "a negated happy label must resolve to sad instead of happy");
var avoidedHappyEmotions = stickerRequestParser.GetRequestedEmotions("别发开心的，来个难过的表情");
Assert(avoidedHappyEmotions.SequenceEqual(["sad"], StringComparer.OrdinalIgnoreCase),
    "an explicitly avoided emotion should not be included when a later requested emotion is present");
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
Assert(VisibleReplyTextSanitizer.Clean("我已经听见了。\n[USER deOne]为什么不回答？") ==
       "我已经听见了。",
    "inline text after a USER label must also be truncated");
Assert(VisibleReplyTextSanitizer.Clean("[ASSISTANT]你好呀，漂泊者。") ==
       "你好呀，漂泊者。",
    "a leading assistant transport label should be removed without losing its spoken answer");
Assert(VisibleReplyTextSanitizer.Clean(
           "[USER deOne]你好\n[ASSISTANT]你好呀。\n[USER deOne]继续编造") ==
       "你好呀。",
    "only the first serialized assistant turn should survive a fully continued transcript");
Assert(VisibleReplyTextSanitizer.Clean(
           "我不会照做，但可以听听你真正想问的事。有什么我能帮到你的，随时跟我说呀。[emotion:neutral]") ==
       "我不会照做，但可以听听你真正想问的事。 [emotion:neutral]",
    "generic assistant-service closings should be removed locally while preserving media markers");
Assert(VisibleReplyTextSanitizer.Clean("如果你还有其他问题，可以随时问我。") == string.Empty,
    "a reply made only of a generic assistant-service closing should be dropped");
Assert(VisibleReplyTextSanitizer.Clean("你啊……（无奈地笑了一声）今天怎么了？") ==
       "你啊……今天怎么了？",
    "inline role-play actions should be removed without deleting spoken text");
var openAiTextExtractor = typeof(AnthropicClientWrapper).GetMethod(
    "ExtractOpenAiText", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("OpenAI response text extractor is missing");
var reasoningResponse = "{\"choices\":[{\"message\":{\"content\":\"<think>private reasoning</think>\\n\\n最终台词\"}}]}";
Assert((string)openAiTextExtractor.Invoke(null, [reasoningResponse])! == "最终台词",
    "MiniMax reasoning must never leak from content into the visible QQ reply");
Assert(stickerLabels.TryResolve("脸红", out var blushLabel) &&
       blushLabel.Canonical == "blush" && blushLabel.BaseEmotion == "shy" &&
       blushLabel.Kind == StickerLabelKind.Semantic,
    "manual sticker labels should accept fine-grained Chinese visual semantics");
Assert(stickerLabels.TryResolve("温柔", out var gentleLabel) &&
       gentleLabel.Canonical == "gentle" && gentleLabel.Kind == StickerLabelKind.Intent,
    "manual sticker labels should distinguish conversational intent from visible emotion");
Assert(stickerLabels.All.Select(item => item.Canonical).Distinct(StringComparer.OrdinalIgnoreCase).Count() >
       stickerLabels.BaseEmotions.Count,
    "the human label vocabulary must remain dynamic and richer than the ten fallback emotions");
var reloadableLabelOptions = new MutableOptionsMonitor<StickerLabelVocabularyOptions>(
    new StickerLabelVocabularyOptions
    {
        FallbackEmotion = "neutral",
        Definitions =
        [
            new()
            {
                Canonical = "neutral",
                BaseEmotion = "neutral",
                Kind = StickerLabelKind.Emotion,
                ChineseName = "平静"
            }
        ]
    });
using (var reloadableLabels = new StickerLabelVocabulary(
           reloadableLabelOptions,
           NullLogger<StickerLabelVocabulary>.Instance))
{
    reloadableLabelOptions.Update(new StickerLabelVocabularyOptions
    {
        FallbackEmotion = "neutral",
        Definitions =
        [
            new()
            {
                Canonical = "neutral",
                BaseEmotion = "neutral",
                Kind = StickerLabelKind.Emotion,
                ChineseName = "平静"
            },
            new()
            {
                Canonical = "delighted",
                BaseEmotion = "delighted",
                Kind = StickerLabelKind.Emotion,
                ChineseName = "雀跃",
                Aliases = ["欢欣"]
            }
        ]
    });
    Assert(reloadableLabels.TryResolve("欢欣", out var reloadedLabel) &&
           reloadedLabel.Canonical == "delighted" &&
           reloadableLabels.BaseEmotions.Contains("delighted"),
        "a new emotion and alias must become available after runtime option reload without recompilation");
}
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
    "incoming messages must use bounded per-conversation queues");
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
Assert(ExplicitAiRequestService.TryExtractPrompt("~ai 你好", "~ai", out var privateTriggered) &&
       privateTriggered == "你好",
    "~ai followed by whitespace should enter private AI flow and strip the prefix");
Assert(!ExplicitAiRequestService.TryExtractPrompt("你好", "~ai", out _),
    "ordinary private text must not enter AI flow");
Assert(!ExplicitAiRequestService.TryExtractPrompt("~aix test", "~ai", out _),
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
var separatedLabels = stickerLabels.SplitInput("开心|脸红，俏皮；温柔+关心＆安慰 大笑");
Assert(separatedLabels.Count == 7 && separatedLabels.Contains("安慰"),
    "sticker labels should accept pipes, punctuation, plus, ampersand, and whitespace separators");
var explicitIntentMethod = typeof(StickerManagementService).GetMethod(
    "GetExplicitIntentTags", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("manual sticker intent selector is missing");
var angrySeriousDefinitions = stickerLabels.ResolveMany(["angry", "serious"]);
var inferredManualIntents = (IReadOnlyList<string>)explicitIntentMethod.Invoke(
    null, [angrySeriousDefinitions])!;
Assert(inferredManualIntents.Count == 0,
    "manual angry + serious labels must not manufacture a calm intent");
var explicitCalmDefinitions = stickerLabels.ResolveMany(["angry", "calm"]);
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
        stickerLabels,
        new TestOptionsMonitor<ConversationFocusOptions>(configuredFocus),
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
    Options.Create(configuredTagger),
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
var bootstrapTestSticker = Path.Combine(
    Directory.GetCurrentDirectory(),
    "resources", "images", "approved", "829269550", "angry_3dfcfd9b0f10dfac6cecfda3.gif");
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
    ApprovedStickerFileNames =
    [
        "smile.gif",
        "happy_535e68fcca4341cfd8ddd5f8.gif",
        "angry_3dfcfd9b0f10dfac6cecfda3.gif"
    ],
    ApprovedStickerDirectories = []
}), tagCatalog, stickerLabels, Options.Create(new StickerTagOptions()), NullLogger<ImageService>.Instance);
Assert(curatedImages.AvailableImages.Count == 3, "only the three curated GIF stickers should be available");
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
var compoundFallback = curatedImages.SearchStickers(new StickerSearchRequest
{
    Emotions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["sad"] = 1.0,
        ["angry"] = 1.0
    }
}).Single();
Assert(compoundFallback.MatchLevel == "neutral-fallback" &&
       compoundFallback.StickerId == Path.GetFileName(neutralTestSticker),
    "an explicit compound emotion with no strong combined match must prefer neutral over a misleading single emotion");
Assert(curatedImages.GetDeclaredEmotion(neutralTestSticker) == "neutral" &&
       curatedImages.ResolveEmotion("neutral") == neutralTestSticker,
    "catalog emotion must override a misleading happy_ file-name prefix");
var toolSnapshot = curatedImages.BuildOpenCodeSnapshot();
Assert(toolSnapshot.Version == StickerTagCatalog.CurrentCatalogVersion,
    "OpenCode sticker snapshot should publish the current multi-frame catalog version");
Assert(toolSnapshot.Stickers.Any(item => item.StickerId == "smile.gif" && item.Emotions.Count >= 2),
    "OpenCode snapshot should expose multi-label emotion metadata for approved stickers");
Assert(toolSnapshot.Stickers.Any(item =>
        item.StickerId == Path.GetFileName(bootstrapTestSticker) &&
        item.Emotions.ContainsKey("angry") &&
        item.SemanticTags.Contains("angry", StringComparer.OrdinalIgnoreCase)),
    "OpenCode snapshot should keep approved stickers available during asynchronous tag backfill");
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
}), tagCatalog, stickerLabels, Options.Create(new StickerTagOptions()), NullLogger<ImageService>.Instance);
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
}), tagCatalog, stickerLabels, Options.Create(new StickerTagOptions()), NullLogger<ImageService>.Instance);
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
var renderedHelpPath = renderHelp.Invoke(null, [helpSections, "秧秧"]) as string;
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
architectureServices.Configure<AiOptions>(options =>
{
    options.ApiKey = "integration-test-key";
    options.BaseUrl = "http://127.0.0.1:9";
    options.Model = "integration-test-model";
});
architectureServices.Configure<OpenCodeAgentOptions>(options =>
{
    options.Enabled = false;
    options.AutoStartLocalServer = false;
});
architectureServices.AddSingleton<IOptionsMonitor<StickerLabelVocabularyOptions>>(
    new TestOptionsMonitor<StickerLabelVocabularyOptions>(configuredStickerLabels));
architectureServices.AddSingleton<IOptionsMonitor<ResponsePolicyOptions>>(
    new TestOptionsMonitor<ResponsePolicyOptions>(configuredResponsePolicies));
architectureServices.AddSingleton<IOptions<DialoguePlanningOptions>>(
    Options.Create(configuredDialoguePlanning));
architectureServices.AddSingleton<IOptionsMonitor<PersonaCorpusRoutingOptions>>(
    corpusRoutingOptions);
architectureServices.AddSingleton<IOptionsMonitor<PersonaComplianceRuleOptions>>(
    complianceRuleOptions);
architectureServices.AddSingleton<IOptionsMonitor<RelationshipLanguageOptions>>(
    relationshipLanguageOptions);
architectureServices.AddSingleton<IOptionsMonitor<PersonaOptions>>(personaOptions);
architectureServices.AddSingleton<IOptionsMonitor<ConversationStyleOptions>>(styleOptions);
architectureServices.AddSingleton<IOptionsMonitor<ConversationFocusOptions>>(
    new TestOptionsMonitor<ConversationFocusOptions>(configuredFocus));
architectureServices.AddSingleton<IOptionsMonitor<SocialIntelligenceOptions>>(
    new TestOptionsMonitor<SocialIntelligenceOptions>(configuredSocialIntelligence));
architectureServices.AddSingleton<IOptionsMonitor<GroupSceneAwarenessOptions>>(
    new TestOptionsMonitor<GroupSceneAwarenessOptions>(configuredGroupSceneAwareness));
architectureServices.AddSingleton<IOptionsMonitor<GroupChatInvestigatorOptions>>(
    new TestOptionsMonitor<GroupChatInvestigatorOptions>(configuredGroupChatInvestigator));
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
    var participantMiddleware = architectureProvider
        .GetServices<IMessageMiddleware>()
        .OfType<ParticipantIdentityMiddleware>()
        .Single();
    var participantProfiles = architectureProvider.GetRequiredService<ParticipantIdentityService>();
    var selfPlainContext = new MessageContext(new IncomingMessage(
        "qq", "primary", "self-plain", "qq:group:100",
        100, 70001, 90001, 90001, 100,
        "hello from self", false, false, false,
        new TestReplyChannel("primary", 90001, true, 100), null!));
    var selfPlainContinued = false;
    await participantMiddleware.InvokeAsync(
        selfPlainContext,
        (_, _) =>
        {
            selfPlainContinued = true;
            return Task.CompletedTask;
        },
        CancellationToken.None);
    Assert(selfPlainContext.Handled &&
           !selfPlainContinued &&
           selfPlainContext.Outcome == "self-message-observed",
        "plain self messages must remain blocked to prevent feedback loops");
    var configuredSelfAlias = configuredFocus.BotAliases.First(alias => !string.IsNullOrWhiteSpace(alias));
    var selfAliasProbe = $"{configuredSelfAlias} help me test this";
    var selfAliasContext = new MessageContext(new IncomingMessage(
        "qq", "primary", "self-yangyang", "qq:group:100",
        100, 70002, 90001, 90001, 100,
        selfAliasProbe, false, false, false,
        new TestReplyChannel("primary", 90001, true, 100), null!));
    var selfAliasContinued = false;
    await participantMiddleware.InvokeAsync(
        selfAliasContext,
        (_, _) =>
        {
            selfAliasContinued = true;
            return Task.CompletedTask;
        },
        CancellationToken.None);
    Assert(!selfAliasContext.Handled &&
           selfAliasContinued &&
           ParticipantIdentityMiddleware.IsSelfAliasTrigger(selfAliasProbe, configuredFocus.BotAliases),
        "self messages that start with a configured assistant alias must enter the normal user pipeline");

    var externalAliasBotIdForMiddleware = Math.Abs(Random.Shared.NextInt64(1_000_000_000, 8_000_000_000));
    participantProfiles.SetKind("qq", externalAliasBotIdForMiddleware, ParticipantKind.ExternalBot, "test");
    var externalPlainContext = new MessageContext(new IncomingMessage(
        "qq", "primary", "external-plain", "qq:group:100",
        100, 70003, 90001, externalAliasBotIdForMiddleware, 100,
        "hello from external bot", false, false, false,
        new TestReplyChannel("primary", 90001, true, 100), null!));
    var externalPlainContinued = false;
    await participantMiddleware.InvokeAsync(
        externalPlainContext,
        (_, _) =>
        {
            externalPlainContinued = true;
            return Task.CompletedTask;
        },
        CancellationToken.None);
    Assert(externalPlainContext.Handled &&
           !externalPlainContinued &&
           externalPlainContext.Outcome == "external-bot-observed",
        "plain external bot messages must remain blocked to prevent bot loops");
    var externalAliasContext = new MessageContext(new IncomingMessage(
        "qq", "primary", "external-yangyang", "qq:group:100",
        100, 70004, 90001, externalAliasBotIdForMiddleware, 100,
        $"{configuredSelfAlias} help me test this from another bot", false, false, false,
        new TestReplyChannel("primary", 90001, true, 100), null!));
    var externalAliasContinued = false;
    await participantMiddleware.InvokeAsync(
        externalAliasContext,
        (_, _) =>
        {
            externalAliasContinued = true;
            return Task.CompletedTask;
        },
        CancellationToken.None);
    Assert(!externalAliasContext.Handled &&
           externalAliasContinued,
        "external bot messages that start with the assistant alias must enter the normal user pipeline");

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

    var topicContextActivities = architectureProvider.GetRequiredService<IGroupActivityService>();
    const string topicA = "test-topic-a";
    const string topicB = "test-topic-b";
    var otherHumanId = Math.Abs(Random.Shared.NextInt64(1_000_000_000, 8_000_000_000));
    topicContextActivities.RecordIncoming(
        contextGroupId,
        "测试群",
        currentUserId,
        "当前测试用户",
        "话题甲只谈风声",
        [],
        messageId: 91001,
        accountId: "test-account",
        topicId: topicA,
        conversationParticipants: [currentUserId]);
    topicContextActivities.RecordBotReply(
        contextGroupId,
        "甲话题的机器人回复",
        replyToMessageId: 91001,
        replyToUserId: currentUserId,
        topicId: topicA,
        conversationParticipants: [currentUserId]);
    topicContextActivities.RecordIncoming(
        contextGroupId,
        "测试群",
        otherHumanId,
        "另一位成员",
        "话题乙只谈晚饭",
        [],
        messageId: 91002,
        accountId: "test-account",
        topicId: topicB,
        conversationParticipants: [otherHumanId]);
    topicContextActivities.RecordBotReply(
        contextGroupId,
        "乙话题的机器人回复",
        replyToMessageId: 91002,
        replyToUserId: otherHumanId,
        topicId: topicB,
        conversationParticipants: [otherHumanId]);
    var topicScoped = contextAssembler.Build(
        currentUserId,
        "当前测试用户",
        contextGroupId,
        "那后来呢",
        topicA,
        [currentUserId]);
    var topicScopedText = string.Join('\n', topicScoped.Messages.Select(message => message.Content));
    Assert(topicScopedText.Contains("话题甲只谈风声", StringComparison.Ordinal) &&
           topicScopedText.Contains("甲话题的机器人回复", StringComparison.Ordinal) &&
           !topicScopedText.Contains("话题乙只谈晚饭", StringComparison.Ordinal) &&
           topicScoped.RecentAssistantReplies.Contains("甲话题的机器人回复", StringComparer.Ordinal) &&
           !topicScoped.RecentAssistantReplies.Contains("乙话题的机器人回复", StringComparer.Ordinal),
        "topic-scoped context and repetition history must include the selected thread and exclude a parallel conversation");

    var groupInvestigator = architectureProvider.GetRequiredService<GroupChatInvestigatorService>();
    var investigatorGroupId = Math.Abs(Random.Shared.NextInt64(1_000_000_000, 8_000_000_000));
    var configuredInvestigatorMarker = configuredGroupChatInvestigator.RequestMarkers
        .First(marker => !string.IsNullOrWhiteSpace(marker));
    const string quotedEvidenceText = "alpha comet anchor";
    topicContextActivities.RecordIncoming(
        investigatorGroupId,
        "investigator-test",
        910001,
        "source-speaker",
        $"first source: {quotedEvidenceText}",
        [],
        messageId: 94001,
        accountId: "test-account",
        topicId: "investigator-topic",
        conversationParticipants: [910001]);
    topicContextActivities.RecordIncoming(
        investigatorGroupId,
        "investigator-test",
        910002,
        "second-speaker",
        $"follow-up source: {quotedEvidenceText}",
        [],
        messageId: 94002,
        accountId: "test-account",
        topicId: "investigator-topic",
        conversationParticipants: [910002]);
    topicContextActivities.RecordIncoming(
        investigatorGroupId,
        "investigator-test",
        currentUserId,
        "investigator-user",
        $"{configuredInvestigatorMarker} {quotedEvidenceText}",
        [],
        messageId: 94003,
        accountId: "test-account",
        replyToMessageId: 94001,
        replyToUserId: 910001,
        quotedText: quotedEvidenceText,
        mentionedUserIds: [910001],
        topicId: "investigator-topic",
        conversationParticipants: [currentUserId, 910001]);
    var investigatorFocus = new ConversationFocusDecision(
        ConversationTargetKind.Bot,
        null,
        "investigator-topic",
        [currentUserId, 910001],
        1.0,
        1.0,
        FocusReplyMode.Answer,
        1.0,
        "test investigator focus",
        UsedSemanticFallback: false);
    var investigationPrompt = groupInvestigator.BuildPromptContext(
        investigatorGroupId,
        94003,
        $"{configuredInvestigatorMarker} {quotedEvidenceText}",
        investigatorFocus);
    Assert(investigationPrompt.Contains("<group_chat_investigation>", StringComparison.Ordinal) &&
           investigationPrompt.Contains(quotedEvidenceText, StringComparison.Ordinal) &&
           investigationPrompt.Contains("source-speaker", StringComparison.Ordinal) &&
           investigationPrompt.Contains("phrase_counts:", StringComparison.Ordinal) &&
           investigationPrompt.Contains("speaker_activity_ranking:", StringComparison.Ordinal),
        "group chat investigation should produce quote, speaker, count and ranking evidence from dynamic configuration");
    Assert(string.IsNullOrWhiteSpace(groupInvestigator.BuildPromptContext(
               investigatorGroupId,
               0,
               "ordinary idle chat without configured markers",
               investigatorFocus)),
        "group chat investigation must stay silent when no dynamic trigger or quote focus exists");

    var socialTurns = architectureProvider.GetRequiredService<SocialTurnCoordinator>();
    var ordinarySocialTurn = new TurnContext(
        Guid.NewGuid().ToString("N"),
        "qq",
        "primary",
        $"test-social-{Guid.NewGuid():N}",
        $"qq:group:{contextGroupId}",
        "88001",
        currentUserId,
        "当前测试用户",
        contextGroupId,
        "今天吃什么好",
        Array.Empty<string>(),
        TurnTrigger.ExplicitAi,
        DateTimeOffset.UtcNow);
    var ordinarySocialPlan = socialTurns.Build(new SocialTurnRequest(
        ordinarySocialTurn,
        88001,
        "测试群",
        ordinarySocialTurn.UserText,
        ordinarySocialTurn.UserText,
        HimeStyleScene.GroupReply));
    var ordinarySocialPrompt = string.Join(
        '\n',
        ordinarySocialPlan.Messages.Select(message => message.Content));
    Assert(ordinarySocialPlan.Decision.Act == DialogueAct.Answer &&
           !ordinarySocialPlan.Decision.IncludeRelationshipContext &&
           !ordinarySocialPlan.RequestProfile.PreferDirect &&
           !ordinarySocialPrompt.Contains("<evidence_backed_relationship_plan>", StringComparison.Ordinal),
        "ordinary questions must not receive the large relationship or marriage policy");
    Assert(ordinarySocialPrompt.Contains("<active_persona_lock>", StringComparison.Ordinal) &&
           ordinarySocialPrompt.Contains("这是当前用户的有效消息", StringComparison.Ordinal),
        "explicit social replies must use the shared persona lock and assembled conversation context");
    Assert(ordinarySocialPrompt.Contains("<dynamic_social_turn_strategy>", StringComparison.Ordinal) &&
           ordinarySocialPrompt.Contains("social_intent=", StringComparison.Ordinal),
        "social turns must inject the dynamic strategy brief before model generation");
    var sceneAwareness = architectureProvider.GetRequiredService<GroupSceneAwarenessService>();
    var sceneIncoming = new IncomingMessage(
        "qq",
        "primary",
        "scene-name-review",
        ordinarySocialTurn.ScopeKey,
        contextGroupId,
        880010,
        990010,
        currentUserId,
        contextGroupId,
        "I made a task name called Return to Light",
        false,
        false,
        false,
        new TestReplyChannel("primary", 990010, true, contextGroupId),
        null!)
    {
        ConversationParticipants = [currentUserId]
    };
    sceneAwareness.ObserveIncoming(sceneIncoming, "test group", "scene tester");
    var sceneFollowupTurn = ordinarySocialTurn with
    {
        TurnId = Guid.NewGuid().ToString("N"),
        CorrelationId = $"test-scene-followup-{Guid.NewGuid():N}",
        SourceMessageId = "880011",
        UserText = "why is that name good",
        ConversationParticipants = [currentUserId]
    };
    var sceneFollowupPlan = socialTurns.Build(new SocialTurnRequest(
        sceneFollowupTurn,
        880011,
        "test group",
        sceneFollowupTurn.UserText,
        sceneFollowupTurn.UserText,
        HimeStyleScene.GroupReply));
    var sceneFollowupPrompt = string.Join(
        '\n',
        sceneFollowupPlan.Messages.Select(message => message.Content));
    Assert(sceneAwareness.GetRecentEvents(contextGroupId).Any(item => item.Kind == "naming_review") &&
           sceneFollowupPrompt.Contains("<group_scene_awareness>", StringComparison.Ordinal) &&
           sceneFollowupPrompt.Contains("Return to Light", StringComparison.Ordinal) &&
           sceneFollowupPrompt.Contains("naming_review", StringComparison.Ordinal),
        "short follow-ups must receive dynamic group-scene context instead of relying on hard-coded reply templates");

    var emotionalTurn = ordinarySocialTurn with
    {
        TurnId = Guid.NewGuid().ToString("N"),
        CorrelationId = $"test-emotional-{Guid.NewGuid():N}",
        SourceMessageId = "880015",
        UserText = "今天下雨，所有人都有伞，就我没有"
    };
    var emotionalSocialPlan = socialTurns.Build(new SocialTurnRequest(
        emotionalTurn,
        880015,
        "测试群",
        emotionalTurn.UserText,
        emotionalTurn.UserText,
        HimeStyleScene.GroupReply));
    var emotionalSocialPrompt = string.Join(
        '\n',
        emotionalSocialPlan.Messages.Select(message => message.Content));
    Assert(emotionalSocialPlan.Decision.Act == DialogueAct.Support &&
           !emotionalSocialPlan.Decision.IncludePlotKnowledge &&
           !emotionalSocialPlan.Decision.IncludeCadenceExamples &&
           emotionalSocialPlan.RequestProfile.PreferDirect &&
           emotionalSocialPlan.EmotionalPragmatics.Cues.Contains("social-exclusion") &&
           emotionalSocialPrompt.Contains("<emotional_pragmatics", StringComparison.Ordinal) &&
           emotionalSocialPrompt.Contains("public-safe and restrained", StringComparison.Ordinal),
        "shared social planning must recognize indirect emotional bids, isolate scene examples, and use the structured direct path");

    var relationshipTurn = ordinarySocialTurn with
    {
        TurnId = Guid.NewGuid().ToString("N"),
        CorrelationId = $"test-relationship-{Guid.NewGuid():N}",
        SourceMessageId = "88002",
        UserText = "秧秧做我老婆"
    };
    var relationshipSocialPlan = socialTurns.Build(new SocialTurnRequest(
        relationshipTurn,
        88002,
        "测试群",
        relationshipTurn.UserText,
        relationshipTurn.UserText,
        HimeStyleScene.GroupReply));
    var relationshipSocialPrompt = string.Join('\n', relationshipSocialPlan.Messages.Select(message => message.Content));
    Assert(relationshipSocialPlan.Decision.Act == DialogueAct.Relationship &&
           relationshipSocialPlan.Decision.IncludeRelationshipContext &&
           relationshipSocialPlan.RequestProfile.PreferDirect &&
           relationshipSocialPrompt.Contains("<evidence_backed_relationship_plan>", StringComparison.Ordinal) &&
           relationshipSocialPrompt.Contains("social_intent=relationship_tease", StringComparison.Ordinal) &&
           relationshipSocialPrompt.Contains("allowed_conversation_moves", StringComparison.Ordinal),
        "relationship requests must retain evidence-backed boundaries, dynamic reply moves, and use the low-latency direct path");

    var reactiveTurn = ordinarySocialTurn with
    {
        TurnId = Guid.NewGuid().ToString("N"),
        CorrelationId = $"test-reactive-{Guid.NewGuid():N}",
        SourceMessageId = "88003",
        UserText = "今天风有点大",
        Trigger = TurnTrigger.Reactive
    };
    var reactiveSocialPlan = socialTurns.Build(new SocialTurnRequest(
        reactiveTurn,
        88003,
        "测试群",
        reactiveTurn.UserText,
        reactiveTurn.UserText,
        HimeStyleScene.GroupReply,
        RequireEmotionMarker: true));
    var reactiveSocialPrompt = string.Join(
        '\n',
        reactiveSocialPlan.Messages.Select(message => message.Content));
    Assert(reactiveSocialPlan.Decision.Act == DialogueAct.React &&
           reactiveSocialPrompt.Contains("这是当前用户的有效消息", StringComparison.Ordinal) &&
           reactiveSocialPrompt.Contains("End with exactly one supported", StringComparison.Ordinal),
        "natural group participation must use the same shared context and explicit media contract");
    var candidateJudge = new ReplyCandidateJudgeService(
        new TestAiClient("你想查哪一类？说具体一点，我帮你一起理清楚。"),
        architectureProvider.GetRequiredService<PersonaComplianceService>(),
        architectureProvider.GetRequiredService<ReplyLearningService>(),
        architectureProvider.GetRequiredService<PersonaRuntimeProfileService>(),
        new TestOptionsMonitor<SocialIntelligenceOptions>(configuredSocialIntelligence),
        NullLogger<ReplyCandidateJudgeService>.Instance);
    var candidateChoice = await candidateJudge.SelectBestAsync(
        "作为AI，我无法确认你的全部需求，如果你需要可以继续告诉我。",
        ordinarySocialPlan,
        ordinarySocialTurn.UserText,
        currentUserId,
        HimeStyleScene.GroupReply,
        casual: true,
        requireEmotionMarker: false,
        ReplyCandidateJudgeUsage.ExplicitAi);
    Assert(candidateChoice.Replaced &&
           candidateChoice.Reply.Contains("具体", StringComparison.Ordinal) &&
           candidateChoice.Candidates.Count >= 2 &&
           candidateChoice.Candidates.Any(candidate =>
               candidate.Reasons.Any(reason => reason.Contains("AI/规则/权限腔", StringComparison.Ordinal))),
        "candidate judge should replace a generic service-tone draft with a more concrete social reply when an alternative is better");

    var contextDatabase = architectureProvider.GetRequiredService<HimeDbContext>();
    var replyLearning = architectureProvider.GetRequiredService<ReplyLearningService>();
    var turnRecorder = architectureProvider.GetRequiredService<IConversationTurnRecorder>();
    var contextActivities = architectureProvider.GetRequiredService<IGroupActivityService>();
    var learningPersona = $"test-persona-{Guid.NewGuid():N}";
    var learningDraft = new ReplyLearningExampleDraft(
        learningPersona,
        "good",
        "capability_query",
        "group_reply",
        "Can you help me check one concrete thing?",
        "Ask which exact thing they want checked before listing capabilities.",
        "Prefer a concrete next step over a generic capability list.",
        CreatedByUserId: 10001,
        GroupId: null,
        Source: "self-test");
    var learnedRecord = replyLearning.AddExample(learningDraft);
    var duplicateLearnedRecord = replyLearning.AddExample(learningDraft);
    var learningMatches = replyLearning.FindRelevant(
        learningPersona,
        "capability_query",
        "group_reply",
        "Please help check that concrete information.",
        "good",
        3);
    Assert(learnedRecord.Id == duplicateLearnedRecord.Id &&
           learningMatches.Any(match => match.Record.Id == learnedRecord.Id),
        "reply learning should upsert human-reviewed examples and retrieve them by intent, scene and similarity");
    var groupScopedDraft = learningDraft with
    {
        UserMessage = "群内查询信息时别像客服，先问清楚是哪条线索。",
        BotReply = "你要查哪条线索？我先帮你把能对上的记录拎出来。",
        Reason = "同群内的能力查询应该先承接语境。",
        GroupId = contextGroupId
    };
    var groupScopedRecord = replyLearning.AddExample(groupScopedDraft);
    var sameGroupLearningMatches = replyLearning.FindRelevant(
        learningPersona,
        "capability_query",
        "group_reply",
        "帮我查一下这条线索",
        "good",
        5,
        contextGroupId);
    var otherGroupLearningMatches = replyLearning.FindRelevant(
        learningPersona,
        "capability_query",
        "group_reply",
        "帮我查一下这条线索",
        "good",
        5,
        contextGroupId + 1);
    Assert(sameGroupLearningMatches.Any(match => match.Record.Id == groupScopedRecord.Id) &&
           otherGroupLearningMatches.All(match => match.Record.Id != groupScopedRecord.Id),
        "group-scoped reply learning examples must not leak into unrelated groups");
    var weightedRecord = replyLearning.SetWeight(learnedRecord.Id, null, 2.5);
    var listedLearning = replyLearning.ListExamples(null, "good", 5);
    var shortLearningId = learnedRecord.Id["learn:".Length..][..8];
    Assert(weightedRecord is not null &&
           Math.Abs(weightedRecord.Weight - 2.5d) < 0.001d &&
           listedLearning.Any(record => record.Id == learnedRecord.Id) &&
           replyLearning.FindById(shortLearningId, null)?.Id == learnedRecord.Id,
        "reply learning management should support weight updates, listing and short id lookup");
    var statsBeforeDelete = replyLearning.GetStats(null);
    Assert(statsBeforeDelete.Total > 0 && statsBeforeDelete.AverageWeight > 0,
        "reply learning stats should report stored examples and normalized weights");
    contextDatabase.Database
        .GetCollection<ReplyLearningRecord>("reply_learning_examples")
        .DeleteMany(record => record.PersonaId == learningPersona);
    var explicitTurnId = Guid.NewGuid().ToString("N");
    var explicitTurn = new TurnContext(
        explicitTurnId,
        "qq",
        "primary",
        $"test-explicit-{explicitTurnId}",
        $"qq:group:{contextGroupId}",
        "99881",
        currentUserId,
        "当前测试用户",
        contextGroupId,
        "统一轮次记录测试",
        Array.Empty<string>(),
        TurnTrigger.ExplicitAi,
        DateTimeOffset.UtcNow);
    turnRecorder.RecordDelivered(
        explicitTurn,
        DeliveredTurn.TextOnly("统一轮次回复已送达", "neutral", "ai-reply") with
        {
            PlatformMessageId = 778899,
            SocialIntentId = "capability_query",
            DialogueAct = nameof(DialogueAct.Answer),
            CandidateSummary = "保留首版，候选 1，首版 90.0，最佳 90.0"
        });

    const string proactiveProbe = "这是一条应当进入后续上下文的主动消息";
    contextActivities.RecordProactiveSent(contextGroupId, proactiveProbe);
    var proactiveTurn = TurnContext.ForProactive(contextGroupId, "测试群");
    turnRecorder.RecordDelivered(
        proactiveTurn,
        new DeliveredTurn(
            proactiveProbe,
            Array.Empty<string>(),
            "calm",
            "proactive-agent",
            RecordGroupActivity: false));

    var sessions = contextDatabase.Database.GetCollection<ChatSession>("chat_sessions");
    var deliveredTurns = contextDatabase.Database.GetCollection<ConversationTurnRecord>("conversation_turns");
    Assert(deliveredTurns.Exists(turn =>
            turn.TurnId == explicitTurnId &&
            turn.SourceMessageId == "99881" &&
            turn.AssistantText == "统一轮次回复已送达" &&
            turn.AssistantMessageId == 778899 &&
            turn.SocialIntentId == "capability_query" &&
            turn.DialogueAct == nameof(DialogueAct.Answer)),
        "the durable turn ledger must retain the exact delivered explicit reply");
    Assert(deliveredTurns.Exists(turn =>
            turn.TurnId == proactiveTurn.TurnId &&
            turn.Trigger == nameof(TurnTrigger.Proactive) &&
            turn.AssistantText == proactiveProbe),
        "the durable turn ledger must retain proactive assistant-only turns");
    var contextSession = sessions.FindOne(session => session.GroupId == contextGroupId)
        ?? throw new InvalidOperationException("context test session was not persisted");
    var recordedExplicit = contextSession.Messages
        .Where(message => message.TurnId == explicitTurnId)
        .ToArray();
    Assert(recordedExplicit.Length == 2 &&
           recordedExplicit.Any(message => message.Role == "user" && message.PlatformMessageId == "99881") &&
           recordedExplicit.Any(message => message.Role == "assistant" && message.Source == "ai-reply"),
        "a delivered explicit turn must persist one traceable user/assistant pair");
    Assert(contextSession.Messages.Any(message =>
            message.TurnId == proactiveTurn.TurnId &&
            message.Role == "assistant" &&
            message.Source == "proactive-agent" &&
            message.Content == proactiveProbe),
        "a delivered proactive turn must persist its real assistant text");
    var contextAfterDeliveredTurns = contextAssembler.Build(
        currentUserId,
        "当前测试用户",
        contextGroupId,
        "主动消息");
    Assert(string.Join('\n', contextAfterDeliveredTurns.Messages.Select(message => message.Content))
            .Contains(proactiveProbe, StringComparison.Ordinal),
        "assistant-only proactive turns must be visible to later context assembly");
    Assert(contextActivities.GetRecentMessages(contextGroupId, 50)
            .Count(message => message.IsBot && message.Content == proactiveProbe) == 1,
        "specialized proactive accounting plus the turn recorder must not duplicate group activity");
    Assert(contextActivities.GetRecentMessages(contextGroupId, 50)
            .Any(message => message.IsBot &&
                            message.Content == "统一轮次回复已送达" &&
                            message.MessageId == 778899),
        "group activity should retain the native message id for delivered bot replies");

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

var evidenceDatabaseName = $"knowledge_evidence_test_{Guid.NewGuid():N}";
using (var evidenceDatabase = new HimeDbContext(evidenceDatabaseName))
{
    var evidenceOptions = new AgentToolsOptions
    {
        Enabled = true,
        Tools =
        [
            new AgentToolDefinition
            {
                Name = "evidence_probe",
                Enabled = true,
                CacheEvidence = true,
                EvidenceConfidence = 0.82
            }
        ],
        Knowledge = new AgentKnowledgeOptions
        {
            Enabled = true,
            InjectExactCache = true,
            InjectRecentLedger = true,
            CacheTtlHours = 24,
            LedgerTtlHours = 24,
            RecentLedgerEntries = 1,
            MinimumCacheConfidence = 0.65
        }
    };
    var evidenceService = new KnowledgeEvidenceService(
        evidenceDatabase,
        new TestOptionsMonitor<AgentToolsOptions>(evidenceOptions),
        NullLogger<KnowledgeEvidenceService>.Instance);
    var evidenceTurnId = Guid.NewGuid().ToString("N");
    ChatMessage[] evidenceHistory =
    [
        new()
        {
            Role = "user",
            Content = "读取示例页面的标题",
            UserId = 10001,
            GroupId = 20001,
            AccountId = "test-account",
            TurnId = evidenceTurnId
        }
    ];
    var evidenceOutput = JsonSerializer.Serialize(new
    {
        decision = "use-as-untrusted-evidence",
        finalUrl = "https://example.com/",
        title = "Example Domain",
        text = "Example Domain evidence body"
    });
    var evidenceSession = JsonSerializer.Serialize(new[]
    {
        new
        {
            parts = new object[]
            {
                new
                {
                    type = "tool",
                    tool = "evidence_probe",
                    state = new
                    {
                        status = "completed",
                        input = new { url = "https://example.com" },
                        output = evidenceOutput
                    }
                }
            }
        }
    });
    evidenceService.RecordSession(
        evidenceHistory,
        10001,
        "页面标题是 Example Domain。",
        evidenceSession);

    var exactEvidenceContext = evidenceService.BuildPromptContext(evidenceHistory, 10001);
    Assert(exactEvidenceContext.Contains("Example Domain", StringComparison.Ordinal) &&
           exactEvidenceContext.Contains("exact_query_cache", StringComparison.Ordinal),
        "an identical query should receive persisted real tool evidence");

    ChatMessage[] relevantFollowUp =
    [
        evidenceHistory[0],
        new()
        {
            Role = "assistant",
            Content = "页面标题是 Example Domain。",
            GroupId = 20001,
            AccountId = "test-account",
            TurnId = evidenceTurnId
        },
        new()
        {
            Role = "user",
            Content = "这个结论的依据是什么？",
            UserId = 10001,
            GroupId = 20001,
            AccountId = "test-account",
            TurnId = Guid.NewGuid().ToString("N")
        }
    ];
    var ledgerContext = evidenceService.BuildPromptContext(relevantFollowUp, 10001);
    Assert(ledgerContext.Contains("recent_claim_ledger", StringComparison.Ordinal) &&
           ledgerContext.Contains("https://example.com/", StringComparison.Ordinal),
        "a direct follow-up should receive the previous turn's evidence ledger");

    ChatMessage[] unrelatedConversation =
    [
        new()
        {
            Role = "assistant",
            Content = "这是完全不同的一条历史回复。",
            GroupId = 20001,
            AccountId = "test-account",
            TurnId = Guid.NewGuid().ToString("N")
        },
        new()
        {
            Role = "user",
            Content = "今天聊点别的。",
            UserId = 10001,
            GroupId = 20001,
            AccountId = "test-account",
            TurnId = Guid.NewGuid().ToString("N")
        }
    ];
    Assert(string.IsNullOrWhiteSpace(evidenceService.BuildPromptContext(unrelatedConversation, 10001)),
        "an unrelated turn must not be polluted by a recent evidence ledger");
}

var focusDatabaseName = $"conversation_focus_test_{Guid.NewGuid():N}";
var focusDatabasePath = Path.Combine(AppContext.BaseDirectory, "data", focusDatabaseName + ".db");
using (var focusDatabase = new HimeDbContext(focusDatabaseName))
{
    var focusMonitor = new TestOptionsMonitor<ConversationFocusOptions>(configuredFocus);
    var focusWriteBehind = new LiteDbWriteBehindService(
        Options.Create(new LiteDbWriteBehindOptions()),
        NullLogger<LiteDbWriteBehindService>.Instance);
    var focusActivity = new GroupActivityService(
        focusDatabase,
        focusWriteBehind,
        new TestOptionsMonitor<GroupActivityOptions>(new GroupActivityOptions()),
        stickerLabels,
        focusMonitor);
    var topicGraph = new ConversationTopicGraph(focusMonitor);
    var focusAi = new TestAiClient(
        """{"target":"unknown","target_user_id":null,"reply_mode":"stay_silent","confidence":0.95,"reason":"not addressed"}""");
    var focusResolver = new ConversationFocusResolver(
        focusAi,
        focusActivity,
        topicGraph,
        focusMonitor,
        NullLogger<ConversationFocusResolver>.Instance);
    const long focusGroupId = 880001;
    const long focusBotId = 990001;
    const long focusUserId = 770001;
    var focusChannel = new TestReplyChannel("focus-test", focusBotId, true, focusGroupId);
    var opening = new IncomingMessage(
        "qq", "focus-test", "focus-opening", $"qq:group:{focusGroupId}",
        focusGroupId, 501, focusBotId, focusUserId, focusGroupId,
        "今天的风声好像有点不一样", false, false, false, focusChannel, null!);
    var openingTopic = topicGraph.Resolve(opening, []);
    focusActivity.RecordIncoming(
        focusGroupId, "focus-test", focusUserId, "tester", opening.Text, [],
        messageId: opening.MessageId,
        accountId: opening.AccountId,
        topicId: openingTopic.TopicId,
        conversationParticipants: openingTopic.Participants);
    focusActivity.RecordBotReply(
        focusGroupId,
        "嗯，像是要变天了。",
        replyToMessageId: opening.MessageId,
        replyToUserId: focusUserId,
        topicId: openingTopic.TopicId,
        conversationParticipants: openingTopic.Participants);

    var continuation = new IncomingMessage(
        "qq", "focus-test", "focus-continuation", $"qq:group:{focusGroupId}",
        focusGroupId, 502, focusBotId, focusUserId, focusGroupId,
        "那后来呢", false, false, false, focusChannel, null!);
    var continuationDecision = await focusResolver.ResolveAsync(continuation);
    Assert(continuationDecision.IsDirectedToBot &&
           continuationDecision.TopicId == openingTopic.TopicId &&
           focusAi.Calls == 0,
        "a direct continuation after the bot must inherit the topic without a semantic model call");
    var selfAliasFocusMessage = new IncomingMessage(
        "qq", "focus-test", "focus-self-alias", $"qq:group:{focusGroupId}",
        focusGroupId, 5021, focusBotId, focusBotId, focusGroupId,
        "\u79e7\u79e7 help me test this", false, false, false, focusChannel, null!);
    var selfAliasDecision = await focusResolver.ResolveAsync(selfAliasFocusMessage);
    Assert(selfAliasDecision.IsDirectedToBot &&
           selfAliasDecision.Reason.Contains("configured-bot-alias", StringComparison.Ordinal) &&
           focusAi.Calls == 0,
        "a self message released by the alias gate must still be resolved as bot-directed by configured aliases");

    var replyToOther = continuation with
    {
        CorrelationId = "focus-other-reply",
        MessageId = 503,
        ReplyToUserId = 660001,
        MentionedUserIds = [660001],
        HasAnyMention = true
    };
    var otherDecision = await focusResolver.ResolveAsync(replyToOther);
    Assert(otherDecision.Target == ConversationTargetKind.SpecificUser &&
           otherDecision.ReplyMode == FocusReplyMode.StaySilent &&
           focusAi.Calls == 0,
        "an explicit reply or mention to another member must never be consumed by the bot");

    var unrelated = new IncomingMessage(
        "qq", "focus-test", "focus-unrelated", "qq:group:880002",
        880002, 601, focusBotId, 770002, 880002,
        "今天晚饭吃什么", false, false, false,
        new TestReplyChannel("focus-test", focusBotId, true, 880002), null!);
    var unrelatedDecision = await focusResolver.ResolveAsync(unrelated);
    Assert(unrelatedDecision.ReplyMode == FocusReplyMode.StaySilent &&
           focusAi.Calls == 0,
        "an unrelated group message with no shared topic must stay silent without spending a model call");
}
File.Delete(focusDatabasePath);

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

file sealed class MutableOptionsMonitor<T>(T initial) : IOptionsMonitor<T>
{
    private T _value = initial;
    private Action<T, string?>? _listener;

    public T CurrentValue => _value;
    public T Get(string? name) => _value;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        _listener += listener;
        return new CallbackDisposable(() => _listener -= listener);
    }

    public void Update(T value)
    {
        _value = value;
        _listener?.Invoke(value, null);
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

file sealed class TestAiClient(string response = "") : IAiClient
{
    public int Calls { get; private set; }

    public Task<string> ChatAsync(
        IReadOnlyList<ChatMessage> history,
        long senderId,
        CancellationToken ct = default,
        bool applyBoundPersona = true,
        AiRequestProfile? requestProfile = null)
    {
        Calls++;
        return Task.FromResult(response);
    }
}

file sealed class TestReplyChannel(
    string accountId,
    long selfId,
    bool isGroup,
    long targetId) : IReplyChannel
{
    public string AccountId => accountId;
    public long SelfId => selfId;
    public bool IsGroup => isGroup;
    public long TargetId => targetId;
    public Task SendTextAsync(string text, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    public Task SendImageAsync(
        string localPath,
        ImageSubType subType = ImageSubType.Normal,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    public Task SendAudioAsync(string localPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
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
