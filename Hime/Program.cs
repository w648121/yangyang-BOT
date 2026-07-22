using Hime.Hosting;
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
    "config/core.json",
    "config/ai.json",
    "config/conversation.json",
    "config/stickers.json",
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
builder.Services.Configure<OpenCodeAgentOptions>(builder.Configuration.GetSection("OpenCodeAgent"));
builder.Services.Configure<ModelRoutingOptions>(builder.Configuration.GetSection("ModelRouting"));
builder.Services.Configure<ConversationStyleOptions>(builder.Configuration.GetSection("ConversationStyle"));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));
builder.Services.Configure<ChatHistoryOptions>(builder.Configuration.GetSection("ChatHistory"));
builder.Services.Configure<LiteDbWriteBehindOptions>(builder.Configuration.GetSection("LiteDbWriteBehind"));
builder.Services.Configure<RuntimeDiagnosticsOptions>(builder.Configuration.GetSection("RuntimeDiagnostics"));
builder.Services.Configure<DiagnosticsDashboardOptions>(builder.Configuration.GetSection("DiagnosticsDashboard"));
builder.Services.Configure<MessageDeduplicationOptions>(builder.Configuration.GetSection("MessageDeduplication"));
builder.Services.Configure<PersonaOptions>(builder.Configuration.GetSection("Personas"));
builder.Services.Configure<PersonaStateOptions>(builder.Configuration.GetSection("PersonaState"));
builder.Services.Configure<RelationshipTrajectoryOptions>(builder.Configuration.GetSection("RelationshipTrajectory"));
builder.Services.Configure<ImageOptions>(builder.Configuration.GetSection("Images"));
builder.Services.Configure<GroupStickerOptions>(builder.Configuration.GetSection("GroupStickers"));
builder.Services.Configure<StickerVisionOptions>(builder.Configuration.GetSection("StickerVision"));
builder.Services.Configure<AnimeTaggerOptions>(builder.Configuration.GetSection("AnimeTagger"));
builder.Services.Configure<StickerTagOptions>(builder.Configuration.GetSection("StickerTags"));
builder.Services.Configure<OllamaVisionOptions>(builder.Configuration.GetSection("OllamaVision"));
builder.Services.Configure<RecentVisualContextOptions>(builder.Configuration.GetSection("RecentVisualContext"));
builder.Services.Configure<VoiceSynthesisOptions>(builder.Configuration.GetSection("VoiceSynthesis"));
builder.Services.Configure<ProactiveAgentOptions>(builder.Configuration.GetSection("ProactiveAgent"));
builder.Services.Configure<TargetedInteractionOptions>(builder.Configuration.GetSection("TargetedInteraction"));
builder.Services.Configure<ReactiveConversationOptions>(builder.Configuration.GetSection("ReactiveConversation"));
builder.Services.Configure<PrivateConversationOptions>(builder.Configuration.GetSection("PrivateConversation"));
builder.Services.Configure<ImplicitAddressOptions>(builder.Configuration.GetSection("ImplicitAddress"));
builder.Services.Configure<MusicOptions>(builder.Configuration.GetSection("Music"));
builder.Services.Configure<GsCoreOptions>(builder.Configuration.GetSection("GsCore"));
builder.Services.Configure<MessageDispatchOptions>(builder.Configuration.GetSection("MessageDispatch"));
builder.Services.Configure<ReplySchedulingOptions>(builder.Configuration.GetSection("ReplyScheduling"));
builder.Services.Configure<ProgramUpdateOptions>(builder.Configuration.GetSection("ProgramUpdates"));

builder.Services.AddSingleton<HimeBotService>();
builder.Services.AddSingleton<IGroupMessageSender>(provider => provider.GetRequiredService<HimeBotService>());
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
builder.Services.AddHostedService<StickerTagBackfillService>();
builder.Services.AddHostedService<ProactiveAgentService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<NcmMusicServerService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<GptSoVitsServerService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<IndexTtsServerService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<VoiceWarmupService>());

using IHost host = builder.Build();
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
