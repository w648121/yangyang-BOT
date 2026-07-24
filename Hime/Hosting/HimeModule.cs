using Hime.Commands;
using Hime.Data;
using Hime.Data.Services;
using Hime.Jobs;
using Hime.Messaging;
using Hime.Messaging.Interactions;
using Hime.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Hime.Hosting;

/// <summary>
/// Hime 应用的 DI 注册模块
/// </summary>
public static class HimeModule
{
    /// <summary>
    /// 注册 Hime 数据层、AI 服务和命令实例
    /// </summary>
    public static IServiceCollection AddHimeData(this IServiceCollection services)
    {
        // DbContext 注册为单例
        services.AddSingleton<HimeDbContext>();
        services.AddOptions<LiteDbWriteBehindOptions>();
        services.AddSingleton<LiteDbWriteBehindService>();
        services.AddOptions<RuntimeDiagnosticsOptions>();
        services.AddOptions<DiagnosticsDashboardOptions>();
        services.AddSingleton<RuntimeDiagnostics>();
        services.AddSingleton<RuntimeStatusService>();
        services.AddSingleton<DiagnosticsDashboardService>();
        services.AddOptions<MessageDeduplicationOptions>();
        services.AddSingleton<MessageDeduplicationService>();

        // Unified in-process message core (adapter -> ordered dispatcher -> middleware
        // -> command/event buses). This remains a modular monolith: no HTTP or JSON
        // boundary is added between Hime features.
        services.AddSingleton<ISoraMessageAdapter, SoraMessageAdapter>();
        services.AddOptions<ParticipantIdentityOptions>();
        services.AddOptions<ContextAssemblyOptions>();
        services.AddSingleton<ParticipantIdentityService>();
        services.AddSingleton<ConversationContextAssembler>();
        services.AddSingleton<IConversationTurnRecorder, ConversationTurnRecorder>();
        services.AddSingleton<ICommandBus, InProcessCommandBus>();
        services.AddSingleton<IEventBus, InProcessEventBus>();
        services.AddSingleton<IPendingInteractionStore, LiteDbPendingInteractionStore>();
        services.AddSingleton<IInteractionManager, InteractionManager>();
        services.AddSingleton<IInteractionContinuationHandler, PrivateAiPromptContinuationHandler>();
        services.AddSingleton<IInteractionContinuationHandler, JobDraftContinuationHandler>();
        services.AddSingleton<IMessageMiddleware, BusinessLoggingMiddleware>();
        services.AddSingleton<IMessageMiddleware, ParticipantIdentityMiddleware>();
        services.AddSingleton<IMessageMiddleware, GroupResponseGateMiddleware>();
        services.AddSingleton<IMessageMiddleware, PendingInteractionMiddleware>();
        services.AddSingleton<MessageMiddlewarePipeline>();
        services.AddSingleton<MessageCoordinator>();
        services.AddSingleton<MessageCommandRouter>();
        services.AddSingleton<MessageDiagnosticsEventHandler>();
        services.AddSingleton<IEventHandler<MessageAcceptedEvent>>(provider =>
            provider.GetRequiredService<MessageDiagnosticsEventHandler>());
        services.AddSingleton<IEventHandler<MessageCompletedEvent>>(provider =>
            provider.GetRequiredService<MessageDiagnosticsEventHandler>());

        services.AddSingleton<ICommandHandler<DispatchLegacyMessageCommand>, DispatchLegacyMessageCommandHandler>();
        services.AddSingleton<ICommandHandler<GenerateAiReplyCommand>, GenerateAiReplyCommandHandler>();
        services.AddSingleton<ICommandHandler<ClearAiConversationCommand>, ClearAiConversationCommandHandler>();
        services.AddSingleton<ICommandHandler<HandleGsCoreCommand>, HandleGsCoreCommandHandler>();
        services.AddSingleton<ICommandHandler<PlayMusicRequestCommand>, PlayMusicRequestCommandHandler>();
        services.AddSingleton<ICommandHandler<SendSetuRequestCommand>, SendSetuRequestCommandHandler>();
        services.AddSingleton<ICommandHandler<TryTargetedInteractionCommand>, TryTargetedInteractionCommandHandler>();
        services.AddSingleton<ICommandHandler<TryReactiveConversationCommand>, TryReactiveConversationCommandHandler>();
        services.AddSingleton<ICommandHandler<ExecuteScheduledJobCommand>, ExecuteScheduledJobCommandHandler>();

        // 数据服务
        services.AddSingleton<IChatService, ChatService>();
        services.AddSingleton<IGroupActivityService, GroupActivityService>();
        services.AddSingleton<GroupResponseStateService>();
        services.AddOptions<PersonaStateOptions>();
        services.AddSingleton<IPersonaStateService, PersonaStateService>();
        services.AddOptions<RelationshipTrajectoryOptions>();
        services.AddSingleton<IRelationshipTrajectoryService, RelationshipTrajectoryService>();

        // 人设系统（基于 appsettings.json 的 "Personas" 节点 + personas/*.md）
        services.AddOptions<PersonaOptions>();
        services.AddSingleton<PersonaRegistry>();
        services.AddSingleton<PersonaRuntimeProfileService>();
        services.AddSingleton<PersonaCorpusService>();
        services.AddSingleton<PersonaPlotKnowledgeService>();
        services.AddOptions<ProgramUpdateOptions>();
        services.AddSingleton<ProgramUpdateLogService>();

        // AI 客户端（基于 appsettings.json 的 "AI" 节点）
        services.AddOptions<AiOptions>();
        services.AddOptions<OpenCodeAgentOptions>();
        services.AddOptions<ModelRoutingOptions>();
        services.AddSingleton<AnthropicClientWrapper>();
        services.AddSingleton<OpenCodeServerService>();
        services.AddSingleton<OpenCodeAgentClient>();
        services.AddSingleton<IAiClient>(provider => provider.GetRequiredService<OpenCodeAgentClient>());
        services.AddSingleton<AnthropicChatClientAdapter>();
        services.AddSingleton<ConversationRouter>();
        services.AddOptions<DialoguePlanningOptions>();
        services.AddSingleton<SocialTurnCoordinator>();
        services.AddOptions<ConversationStyleOptions>();
        services.AddSingleton<ConversationStyleService>();
        services.AddSingleton<PersonaComplianceService>();
        services.AddSingleton<RuntimeFactResponder>();
        services.AddSingleton<IntelligenceSelfTestService>();
        services.AddSingleton<ProactiveGroupAgent>();
        services.AddSingleton<GroupConversationStateMachine>();
        services.AddSingleton<ProactiveContentPlanner>();

        // Local image resources and semantic sticker catalog.
        services.AddOptions<ImageOptions>();
        services.AddOptions<StickerVisionOptions>();
        services.AddOptions<AnimeTaggerOptions>();
        services.AddOptions<StickerTagOptions>();
        services.AddOptions<OllamaVisionOptions>();
        services.AddOptions<RecentVisualContextOptions>();
        services.AddSingleton<AnimeStickerTagger>();
        services.AddSingleton<StickerTagCatalog>();
        services.AddSingleton<ImageService>();
        services.AddSingleton<OpenCodeStickerCatalogPublisher>();
        services.AddSingleton<StickerManagementService>();
        services.AddOptions<GalleryOptions>();
        services.AddSingleton<GalleryService>();
        services.AddOptions<SetuOptions>();
        services.AddSingleton<SetuIntentInterpreter>();
        services.AddSingleton<SetuApiService>();
        services.AddSingleton<SetuRequestService>();
        services.AddOptions<OneBotOptions>();
        services.AddSingleton<OneBotApiClient>();
        services.AddSingleton<OneBotForwardMessageSender>();
        services.AddSingleton<IncomingImageStore>();
        services.AddSingleton<OllamaVisionService>();
        services.AddSingleton<RecentVisualContextStore>();

        // 群表情包高频收集与随机回复
        services.AddOptions<GroupStickerOptions>();
        services.AddSingleton<OnnxStickerEmotionClassifier>();
        services.AddSingleton<LocalImageInspector>();
        services.AddSingleton<StickerEmotionAnalyzer>();
        services.AddSingleton<GroupStickerCollector>();

        // 本地基础 TTS + RVC 音色转换
        services.AddOptions<VoiceSynthesisOptions>();
        services.AddOptions<ProactiveAgentOptions>();
        services.AddOptions<TargetedInteractionOptions>();
        services.AddOptions<ReactiveConversationOptions>();
        services.AddOptions<PrivateConversationOptions>();
        services.AddOptions<ImplicitAddressOptions>();
        services.AddOptions<MusicOptions>();
        services.AddSingleton<VoiceSynthesisService>();
        services.AddSingleton<VoiceCacheMaintenanceService>();
        services.AddSingleton<VoiceWarmupService>();
        services.AddSingleton<VoiceOutboxStore>();
        services.AddSingleton<AutoVoiceDeliveryService>();
        services.AddSingleton<GptSoVitsServerService>();
        services.AddSingleton<IndexTtsServerService>();
        services.AddSingleton<VoiceEngineCoordinator>();
        services.AddSingleton<TargetedInteractionService>();
        services.AddSingleton<ReactiveConversationService>();
        services.AddSingleton<ImplicitAddressDetector>();
        services.AddSingleton<NcmMusicServerService>();
        services.AddSingleton<NcmMusicService>();
        services.AddSingleton<OneBotMusicCardSender>();
        services.AddOptions<GsCoreOptions>();
        services.AddSingleton<GsCoreServerService>();
        services.AddSingleton<GsCoreBridgeService>();
        services.AddOptions<MessageDispatchOptions>();
        services.AddSingleton<ConversationMessageDispatcher>();
        services.AddOptions<ReplySchedulingOptions>();
        services.AddSingleton<ScheduledReplyDispatcher>();
        services.AddOptions<JobOptions>();
        services.AddSingleton<JobIntentDetector>();
        services.AddSingleton<JobTimeParser>();
        services.AddSingleton<JobEditIntentParser>();
        services.AddSingleton<TemporalAnchorService>();
        services.AddSingleton<ScheduledJobStore>();
        services.AddSingleton<JobRequestService>();
        services.AddSingleton<JobExecutionService>();
        services.AddSingleton<JobSchedulerService>();

        // 命令实例（实例命令类必须以 Singleton 注册，且在 ScanAssembly 之前 RegisterCommandInstance）
        services.AddSingleton<AdminCommand>();
        services.AddSingleton<StickerCommand>();
        services.AddSingleton<AiCommand>();
        services.AddSingleton<VoiceCommand>();
        services.AddSingleton<MusicCommand>();
        services.AddSingleton<GalleryCommand>();
        services.AddSingleton<GroupResponseCommand>();
        services.AddSingleton<UpdateLogCommand>();
        services.AddSingleton<HelpCommand>();
        services.AddSingleton<JobCommand>();

        return services;
    }
}
