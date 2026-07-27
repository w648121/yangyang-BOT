using Hime.Hosting;
using Hime.Jobs;
using Hime.Services;
using Hime.Data.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;

// Keep the system proxy for external AI requests, but connect to local bot
// protocol servers directly. Windows may have no loopback proxy exceptions.
HttpClient.DefaultProxy = new LoopbackBypassProxy(HttpClient.DefaultProxy);

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// 业务配置按职责拆分；后加载的本机、环境变量和命令行配置具有更高优先级。
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

foreach (string configurationFile in configurationFiles)
{
    builder.Configuration.AddJsonFile(configurationFile, optional: false, reloadOnChange: true);
}

builder.Configuration.AddJsonFile(
    $"appsettings.{builder.Environment.EnvironmentName}.json",
    optional: true,
    reloadOnChange: true);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Services.AddHimeData();
builder.Services.AddHttpClient();

builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("AI"));
builder.Services.Configure<BotAccountsOptions>(builder.Configuration.GetSection("BotAccounts"));
builder.Services.Configure<OpenCodeAgentOptions>(builder.Configuration.GetSection("OpenCodeAgent"));
builder.Services.Configure<AgentToolsOptions>(builder.Configuration.GetSection("AgentTools"));
builder.Services.Configure<ModelRoutingOptions>(builder.Configuration.GetSection("ModelRouting"));
builder.Services.AddOptions<ResponsePolicyOptions>()
    .Bind(builder.Configuration.GetSection("ResponsePolicies"))
    .Validate(options => options.IsValid(), "ResponsePolicies must define Casual, Factual and Technical policies.")
    .ValidateOnStart();
builder.Services.AddOptions<DialoguePlanningOptions>()
    .Bind(builder.Configuration.GetSection("DialoguePlanning"))
    .Validate(options => options.IsValid(), "DialoguePlanning marker lists must not be empty.")
    .ValidateOnStart();
builder.Services.Configure<EmotionalPragmaticsOptions>(builder.Configuration.GetSection("EmotionalPragmatics"));
builder.Services.AddOptions<ConversationStyleOptions>()
    .Bind(builder.Configuration.GetSection("ConversationStyle"))
    .Validate(options => options.HasDialogueMoves(), "ConversationStyle must define dialogue move rules and defaults.")
    .ValidateOnStart();
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));
builder.Services.AddOptions<ChatHistoryOptions>()
    .Bind(builder.Configuration.GetSection("ChatHistory"))
    .Validate(options => options.IsValid(), "ChatHistory memory marker configuration is incomplete or invalid.")
    .ValidateOnStart();
builder.Services.Configure<ParticipantIdentityOptions>(builder.Configuration.GetSection("ParticipantIdentity"));
builder.Services.Configure<ContextAssemblyOptions>(builder.Configuration.GetSection("ContextAssembly"));
builder.Services.AddOptions<GroupActivityOptions>()
    .Bind(builder.Configuration.GetSection("GroupActivity"))
    .Validate(options => options.IsValid(), "GroupActivity retention limits are invalid.")
    .ValidateOnStart();
builder.Services.Configure<LiteDbWriteBehindOptions>(builder.Configuration.GetSection("LiteDbWriteBehind"));
builder.Services.AddOptions<RuntimeDiagnosticsOptions>()
    .Bind(builder.Configuration.GetSection("RuntimeDiagnostics"))
    .Validate(options => options.IsValid(), "RuntimeDiagnostics configuration is incomplete or invalid.")
    .ValidateOnStart();
builder.Services.Configure<DiagnosticsDashboardOptions>(builder.Configuration.GetSection("DiagnosticsDashboard"));
builder.Services.Configure<MessageDeduplicationOptions>(builder.Configuration.GetSection("MessageDeduplication"));
builder.Services.AddOptions<PersonaOptions>()
    .Bind(builder.Configuration.GetSection("Personas"))
    .Validate(options => options.IsValid(), "Personas must define identity, plot and runtime guard rules.")
    .ValidateOnStart();
builder.Services.AddOptions<PersonaCorpusRoutingOptions>()
    .Bind(builder.Configuration.GetSection("PersonaCorpusRouting"))
    .Validate(options => options.IsValid(), "PersonaCorpusRouting must define scene and emotion markers.")
    .ValidateOnStart();
builder.Services.AddOptions<PersonaPresenceOptions>()
    .Bind(builder.Configuration.GetSection("PersonaPresence"))
    .Validate(options => options.IsValid(), "PersonaPresence thresholds are invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<PersonaComplianceRuleOptions>()
    .Bind(builder.Configuration.GetSection("PersonaComplianceRules"))
    .Validate(options => options.IsValid(), "PersonaComplianceRules contains invalid patterns or fallbacks.")
    .ValidateOnStart();
builder.Services.AddOptions<SocialIntelligenceOptions>()
    .Bind(builder.Configuration.GetSection("SocialIntelligence"))
    .Validate(options => options.IsValid(), "SocialIntelligence must define dynamic social rules.")
    .ValidateOnStart();
builder.Services.AddOptions<GroupSceneAwarenessOptions>()
    .Bind(builder.Configuration.GetSection("GroupSceneAwareness"))
    .Validate(options => options.IsValid(), "GroupSceneAwareness must define dynamic group scene rules.")
    .ValidateOnStart();
builder.Services.AddOptions<GroupChatInvestigatorOptions>()
    .Bind(builder.Configuration.GetSection("GroupChatInvestigator"))
    .Validate(options => options.IsValid(), "GroupChatInvestigator must define dynamic investigation rules.")
    .ValidateOnStart();
builder.Services.AddOptions<ForwardMessageIngestOptions>()
    .Bind(builder.Configuration.GetSection("ForwardMessageIngest"))
    .Validate(options => options.IsValid(), "ForwardMessageIngest limits and placeholders are invalid.")
    .ValidateOnStart();
builder.Services.Configure<PersonaStateOptions>(builder.Configuration.GetSection("PersonaState"));
builder.Services.AddOptions<RelationshipTrajectoryOptions>()
    .Bind(builder.Configuration.GetSection("RelationshipTrajectory"))
    .Validate(options => options.IsValid(), "RelationshipTrajectory configuration is incomplete.")
    .ValidateOnStart();
builder.Services.AddOptions<RelationshipLanguageOptions>()
    .Bind(builder.Configuration.GetSection("RelationshipLanguage"))
    .Validate(options => options.IsValid(), "RelationshipLanguage marker lists must not be empty.")
    .ValidateOnStart();
builder.Services.Configure<ImageOptions>(builder.Configuration.GetSection("Images"));
builder.Services.Configure<GroupStickerOptions>(builder.Configuration.GetSection("GroupStickers"));
builder.Services.Configure<StickerVisionOptions>(builder.Configuration.GetSection("StickerVision"));
builder.Services.AddOptions<AnimeTaggerOptions>()
    .Bind(builder.Configuration.GetSection("AnimeTagger"))
    .Validate(options => options.IsValid(), "AnimeTagger configuration is incomplete or invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<StickerTagOptions>()
    .Bind(builder.Configuration.GetSection("StickerTags"))
    .Validate(options => options.IsValid(), "StickerTags configuration is incomplete or invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<StickerLabelVocabularyOptions>()
    .Bind(builder.Configuration.GetSection("StickerLabels"))
    .Validate(options => options.IsValid(), "StickerLabels configuration is incomplete or invalid.")
    .ValidateOnStart();
builder.Services.Configure<GalleryOptions>(builder.Configuration.GetSection("Gallery"));
builder.Services.AddOptions<SetuOptions>()
    .Bind(builder.Configuration.GetSection("Setu"))
    .Validate(options => options.IsValid(), "Setu configuration is incomplete or invalid.")
    .ValidateOnStart();
builder.Services.Configure<OneBotOptions>(builder.Configuration.GetSection("OneBot"));
builder.Services.Configure<OllamaVisionOptions>(builder.Configuration.GetSection("OllamaVision"));
builder.Services.Configure<RecentVisualContextOptions>(builder.Configuration.GetSection("RecentVisualContext"));
builder.Services.AddOptions<VoiceSynthesisOptions>()
    .Bind(builder.Configuration.GetSection("VoiceSynthesis"))
    .Validate(options => options.IsValid(), "VoiceSynthesis IndexTTS2 emotion vectors must contain eight values.")
    .ValidateOnStart();
builder.Services.Configure<ProactiveAgentOptions>(builder.Configuration.GetSection("ProactiveAgent"));
builder.Services.Configure<ReactiveConversationOptions>(builder.Configuration.GetSection("ReactiveConversation"));
builder.Services.Configure<PrivateConversationOptions>(builder.Configuration.GetSection("PrivateConversation"));
builder.Services.AddOptions<ConversationFocusOptions>()
    .Bind(builder.Configuration.GetSection("ConversationFocus"))
    .Validate(options => options.IsValid(), "ConversationFocus contains invalid thresholds or limits.")
    .ValidateOnStart();
builder.Services.Configure<MusicOptions>(builder.Configuration.GetSection("Music"));
builder.Services.AddOptions<GsCoreOptions>()
    .Bind(builder.Configuration.GetSection("GsCore"))
    .Validate(options => options.IsValid(), "GsCore configuration is incomplete or invalid.")
    .ValidateOnStart();
builder.Services.Configure<MessageDispatchOptions>(builder.Configuration.GetSection("MessageDispatch"));
builder.Services.Configure<ReplySchedulingOptions>(builder.Configuration.GetSection("ReplyScheduling"));
builder.Services.Configure<ProgramUpdateOptions>(builder.Configuration.GetSection("ProgramUpdates"));
builder.Services.AddOptions<JobOptions>()
    .Bind(builder.Configuration.GetSection("Jobs"))
    .Validate(options => options.IsValid(), "Jobs configuration is incomplete or invalid.")
    .ValidateOnStart();

builder.Services.AddSingleton<HimeBotService>();
builder.Services.AddSingleton<IGroupMessageSender>(provider => provider.GetRequiredService<HimeBotService>());
builder.Services.AddSingleton<IAccountMessageSender>(provider => provider.GetRequiredService<HimeBotService>());
// Register first so it stops last and can flush snapshots after message producers stop.
builder.Services.AddHostedService(provider => provider.GetRequiredService<LiteDbWriteBehindService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<DiagnosticsDashboardService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<ConversationMessageDispatcher>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<ScheduledReplyDispatcher>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<VoiceCacheMaintenanceService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<AutoVoiceDeliveryService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<GroupStickerCollector>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<OpenCodeServerService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<GsCoreServerService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<HimeBotService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<JobSchedulerService>());
builder.Services.AddHostedService<StickerTagBackfillService>();
builder.Services.AddHostedService<ProactiveAgentService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<NcmMusicServerService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<GptSoVitsServerService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<IndexTtsServerService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<VoiceWarmupService>());

using IHost host = builder.Build();

// One-shot operational migration used when moving legacy configuration allow-lists
// into LiteDB. Normal runtime authorization never reads these command-line values.
if (args.Any(argument => argument.Equals("--migrate-group-responses", StringComparison.OrdinalIgnoreCase)))
{
    var ids = args
        .Where(argument => !argument.Equals("--migrate-group-responses", StringComparison.OrdinalIgnoreCase))
        .SelectMany(argument => argument.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries))
        .Select(argument => long.TryParse(argument.Trim(), out var value) ? value : 0)
        .Where(value => value > 0)
        .Distinct()
        .ToArray();
    var states = host.Services.GetRequiredService<GroupResponseStateService>();
    foreach (var groupId in ids)
        states.SetEnabled(groupId, enabled: true, updatedByUserId: 0, source: "legacy-config-migration");
    var persisted = states.GetEnabledGroupIds();
    Console.WriteLine($"Migrated {ids.Length} group response state(s) into LiteDB. Enabled groups: {string.Join(',', persisted)}");
    return;
}

await host.RunAsync();

file sealed class LoopbackBypassProxy(IWebProxy inner) : IWebProxy
{
    public ICredentials? Credentials
    {
        get => inner.Credentials;
        set => inner.Credentials = value;
    }

    public Uri? GetProxy(Uri destination) =>
        destination.IsLoopback ? destination : inner.GetProxy(destination);

    public bool IsBypassed(Uri host) =>
        host.IsLoopback || inner.IsBypassed(host);
}
