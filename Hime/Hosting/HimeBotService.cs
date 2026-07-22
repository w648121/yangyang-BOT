using Hime.Commands;
using Hime.Data.Services;
using Hime.Messaging;
using Hime.Messaging.Interactions;
using Hime.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora;
using Sora.Adapter.Milky;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;
using Sora.Entities.Segments;

namespace Hime.Hosting;

/// <summary>
/// Hime 机器人后台服务 - 持有 SoraService 的生命周期
/// </summary>
public class HimeBotService : BackgroundService, IGroupMessageSender
{
    private readonly ILogger<HimeBotService> _logger;
    private readonly IServiceProvider _services;
    private readonly IncomingImageStore _incomingImageStore;
    private readonly RecentVisualContextStore _recentVisualContexts;
    private readonly GroupStickerCollector _stickerCollector;
    private readonly IGroupActivityService _groupActivities;
    private readonly IRelationshipTrajectoryService _relationshipTrajectory;
    private readonly TargetedInteractionService _targetedInteractions;
    private readonly ReactiveConversationService _reactiveConversations;
    private readonly PrivateConversationOptions _privateConversations;
    private readonly ImplicitAddressDetector _implicitAddressDetector;
    private readonly MusicCommand _musicCommand;
    private readonly GsCoreBridgeService _gsCoreBridge;
    private readonly ConversationMessageDispatcher _messageDispatcher;
    private readonly MessageDeduplicationService _messageDeduplication;
    private readonly RuntimeDiagnostics _diagnostics;
    private readonly ISoraMessageAdapter _soraMessageAdapter;
    private readonly MessageCoordinator _messageCoordinator;
    private readonly ICommandBus _commandBus;
    private readonly IInteractionManager _interactions;
    private readonly object _privateBatchSync = new();
    private readonly Dictionary<long, PendingPrivateConversation> _pendingPrivateConversations = [];
    private readonly CancellationTokenSource _privateBatchStop = new();
    private SoraService? _soraService;

    public HimeBotService(
        ILogger<HimeBotService> logger,
        IServiceProvider services,
        IncomingImageStore incomingImageStore,
        RecentVisualContextStore recentVisualContexts,
        GroupStickerCollector stickerCollector,
        IGroupActivityService groupActivities,
        IRelationshipTrajectoryService relationshipTrajectory,
        TargetedInteractionService targetedInteractions,
        ReactiveConversationService reactiveConversations,
        IOptions<PrivateConversationOptions> privateConversations,
        ImplicitAddressDetector implicitAddressDetector,
        MusicCommand musicCommand,
        GsCoreBridgeService gsCoreBridge,
        ConversationMessageDispatcher messageDispatcher,
        MessageDeduplicationService messageDeduplication,
        RuntimeDiagnostics diagnostics,
        ISoraMessageAdapter soraMessageAdapter,
        MessageCoordinator messageCoordinator,
        ICommandBus commandBus,
        IInteractionManager interactions)
    {
        _logger = logger;
        _services = services;
        _incomingImageStore = incomingImageStore;
        _recentVisualContexts = recentVisualContexts;
        _stickerCollector = stickerCollector;
        _groupActivities = groupActivities;
        _relationshipTrajectory = relationshipTrajectory;
        _targetedInteractions = targetedInteractions;
        _reactiveConversations = reactiveConversations;
        _privateConversations = privateConversations.Value;
        _implicitAddressDetector = implicitAddressDetector;
        _musicCommand = musicCommand;
        _gsCoreBridge = gsCoreBridge;
        _messageDispatcher = messageDispatcher;
        _messageDeduplication = messageDeduplication;
        _diagnostics = diagnostics;
        _soraMessageAdapter = soraMessageAdapter;
        _messageCoordinator = messageCoordinator;
        _commandBus = commandBus;
        _interactions = interactions;
    }

    public bool IsReady => _soraService is not null;

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _privateBatchStop.Cancel();
        return base.StopAsync(cancellationToken);
    }

    public async Task SendGroupTextAsync(long groupId, string text, CancellationToken cancellationToken = default)
    {
        using var operation = _diagnostics.Begin("reply.send.text");
        var api = _soraService?.GetApi()
            ?? throw new InvalidOperationException("Sora 服务尚未连接，不能发送主动消息。");
        try
        {
            await api.SendGroupMessageAsync(groupId, new MessageBody(text), cancellationToken);
            _diagnostics.Increment("replies.text.sent");
        }
        catch
        {
            operation.Fail();
            throw;
        }
    }

    public async Task SendGroupImageAsync(
        long groupId,
        string localPath,
        ImageSubType subType = ImageSubType.Normal,
        CancellationToken cancellationToken = default)
    {
        using var operation = _diagnostics.Begin("reply.send.image");
        var api = _soraService?.GetApi()
            ?? throw new InvalidOperationException("Sora 服务尚未连接，不能发送主动消息。");
        var fileUri = new Uri(Path.GetFullPath(localPath)).AbsoluteUri;
        var message = new MessageBody().AddImage(fileUri, subType);
        try
        {
            await api.SendGroupMessageAsync(groupId, message, cancellationToken);
            _diagnostics.Increment("replies.image.sent");
        }
        catch
        {
            operation.Fail();
            throw;
        }
    }

    public async Task SendGroupAudioAsync(long groupId, string localPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath))
            throw new FileNotFoundException("主动语音文件不存在。", localPath);

        var api = _soraService?.GetApi()
            ?? throw new InvalidOperationException("Sora 服务尚未连接，不能发送主动消息。");
        var message = new MessageBody().AddAudio(new Uri(Path.GetFullPath(localPath)).AbsoluteUri);
        await api.SendGroupMessageAsync(groupId, message, cancellationToken);
    }

    public async Task SendFriendAudioAsync(long userId, string localPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath))
            throw new FileNotFoundException("Private voice file does not exist.", localPath);

        var api = _soraService?.GetApi()
            ?? throw new InvalidOperationException("Sora is not connected; private voice cannot be sent.");
        var message = new MessageBody().AddAudio(new Uri(Path.GetFullPath(localPath)).AbsoluteUri);
        await api.SendFriendMessageAsync(userId, message, cancellationToken);
    }

    protected override async Task<Task> ExecuteAsync(CancellationToken stoppingToken)
    {
        // 创建 Milky 协议服务 - 通过 LLBot 提供的 SSE /event 端点接收事件
        _soraService = SoraServiceFactory.Instance.CreateMilkyService(
            new MilkyConfig
            {
                Host = "127.0.0.1",
                Port = 3010,
                AccessToken = "",
                EventTransport = Sora.Adapter.Milky.EventTransport.Sse,
                MinimumLogLevel =  LogLevel.Information
            });

        // 订阅消息接收事件（同时处理日志输出和 @提及 触发 AI 对话）
        _soraService.Events.OnMessageReceived += OnMessageReceived;

        // 注册需要 DI 注入的命令实例（必须在 ScanAssembly 之前）
        _soraService.Commands.RegisterCommandInstance(_services.GetRequiredService<AdminCommand>());
        _soraService.Commands.RegisterCommandInstance(_services.GetRequiredService<StickerCommand>());
        _soraService.Commands.RegisterCommandInstance(_services.GetRequiredService<AiCommand>());
        _soraService.Commands.RegisterCommandInstance(_services.GetRequiredService<VoiceCommand>());
        _soraService.Commands.RegisterCommandInstance(_services.GetRequiredService<MusicCommand>());
        _soraService.Commands.RegisterCommandInstance(_services.GetRequiredService<UpdateLogCommand>());
        _soraService.Commands.RegisterCommandInstance(_services.GetRequiredService<HelpCommand>());

        // 扫描当前程序集，自动注册所有 [CommandGroup] / [Command] 标记的指令
        _soraService.Commands.ScanAssembly(typeof(Program).Assembly);

        // 关键：显式调用 StartAsync 才会真正发起 SSE 连接 + 注册事件订阅
        await _soraService.StartAsync(stoppingToken);

        _logger.LogInformation("Hime 机器人服务已启动，通过 SSE 连入 127.0.0.1:3010");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 收到消息时：
    ///   1. 把聊天内容打印到控制台（不影响命令执行）
    ///   2. 群聊中 @机器人 且不含 /ai 前缀时，直接触发 AI 对话
    /// </summary>
    private async ValueTask OnMessageReceived(MessageReceivedEvent e)
    {
        var message = _soraMessageAdapter.Adapt(e);
        var sourceScope = message.ScopeKey;
        var eventKey = $"{message.ScopeKey}:message:{message.MessageId}";
        if (!_messageDeduplication.TryAccept(eventKey))
        {
            _diagnostics.Increment("messages.duplicate");
            using (_diagnostics.PushCorrelation(message.CorrelationId))
                _diagnostics.Event("duplicate", sourceScope);
            _logger.LogInformation("Ignored duplicate QQ event (CorrelationId={CorrelationId})", message.CorrelationId);
            return;
        }

        _diagnostics.Increment("messages.received");
        var description = $"{message.ScopeKey}:message:{message.MessageId}";

        await _messageDispatcher.EnqueueAsync(
            message.ConversationKey,
            description,
            async cancellationToken =>
            {
                using var correlation = _diagnostics.PushCorrelation(message.CorrelationId);
                _diagnostics.Event("message", sourceScope);
                try
                {
                    await _messageCoordinator.DispatchAsync(
                        message,
                        (context, token) => _commandBus.SendAsync(
                            new DispatchLegacyMessageCommand(
                                innerToken => ProcessMessageAsync(e, context.Message, innerToken)),
                            token),
                        cancellationToken);
                    _diagnostics.Increment("messages.completed");
                }
                catch
                {
                    _diagnostics.Increment("messages.failed");
                    throw;
                }
            });
    }

    private async Task ProcessMessageAsync(
        MessageReceivedEvent e,
        IncomingMessage incoming,
        CancellationToken cancellationToken)
    {
        var messageBody = e.Message.Body;
        var rawMessageText = incoming.Text;
        var isAtBot = incoming.MentionsSelf;
        var hasAnyMention = incoming.HasAnyMention;
        var containsVisual = incoming.ContainsVisual;

        // Explicit ww commands are handled by the local GsCore feature service.
        // Returning here keeps Hime as the only QQ sender and prevents its normal
        // AI/sticker pipelines from producing a second response to the same event.
        if (_gsCoreBridge.ShouldHandle(rawMessageText))
        {
            await _commandBus.SendAsync(
                new HandleGsCoreCommand(e, rawMessageText),
                cancellationToken);
            return;
        }

        var isTargetedInteractionUser = _targetedInteractions.IsConfiguredTarget(e);
        var isImplicitlyAddressed = await _implicitAddressDetector.IsAddressedToBotAsync(
            e,
            rawMessageText,
            isAtBot,
            hasAnyMention,
            cancellationToken);
        var isBotDirected = isAtBot || isImplicitlyAddressed;

        // A QQ quote/reply card does not carry the original ImageSegment in the
        // later @bot question. Archive a regular image when it arrives, then
        // make it available only to that same sender for a short explicit
        // follow-up such as “这张图片里是什么内容？”.
        if (containsVisual)
        {
            try
            {
                var visualSenderId = e.Sender?.UserId ?? e.Message.SenderId;
                var imagePaths = await _incomingImageStore.ArchiveOrdinaryImagesAsync(
                    messageBody,
                    visualSenderId,
                    e.Message.SourceType == MessageSourceType.Group ? e.Message.GroupId : null,
                    cancellationToken);
                _recentVisualContexts.Remember(
                    e.Message.SourceType == MessageSourceType.Group ? e.Message.GroupId : null,
                    visualSenderId,
                    imagePaths);
                if (imagePaths.Count > 0)
                {
                    _logger.LogInformation(
                        "Stored {Count} ordinary image(s) for a short visual follow-up (GroupId={GroupId}, UserId={UserId})",
                        imagePaths.Count,
                        e.Message.SourceType == MessageSourceType.Group ? e.Message.GroupId : null,
                        visualSenderId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to archive a recent ordinary image for visual follow-up");
            }
        }

        // 所有群图片都会归档并参与高频表情统计；随机回复避开命令和 @机器人消息。
        StickerProcessResult stickerResult = StickerProcessResult.Empty;
        if (e.Message.SourceType == MessageSourceType.Group)
        {
            try
            {
                // A configured target is handled by TargetedInteractionService instead, so
                // a random sticker cannot consume the event or create a duplicate response.
                var allowRandomStickerReply = !isTargetedInteractionUser && !isBotDirected && !rawMessageText.TrimStart().StartsWith('/');
                stickerResult = await _stickerCollector.ProcessAsync(e, allowRandomStickerReply, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "处理群表情包收集失败");
            }
        }

        // New relationship trajectories start at the configured v2 activation
        // instant. Every real user message is recorded once; the stable event ID
        // deduplicates messages that also enter through /ai or mention handling.
        var trajectorySenderId = e.Sender?.UserId ?? e.Message.SenderId;
        if (trajectorySenderId > 0 && trajectorySenderId != e.SelfId)
        {
            var trajectoryNickname = e.Sender?.Nickname ?? e.Member?.Nickname ?? trajectorySenderId.ToString();
            long? trajectoryGroupId = e.Message.SourceType == MessageSourceType.Group
                ? (long)e.Message.GroupId
                : null;
            var addressedUsers = messageBody?.OfType<MentionSegment>()
                .Select(mention => (long)mention.Target)
                .Where(target => target > 0 && target != e.SelfId)
                .Distinct()
                .ToArray() ?? [];
            _relationshipTrajectory.RecordUserMessage(
                e.Message.MessageId,
                trajectorySenderId,
                trajectoryNickname,
                trajectoryGroupId,
                rawMessageText,
                stickerResult.ArchivedPaths,
                addressedUsers);
        }

        // 为主动 Agent 记录每个群的近期内容；只保存有限窗口，并附带 QQ、昵称和本地图片路径。
        if (e.Message.SourceType == MessageSourceType.Group && e.Sender?.UserId != e.SelfId)
        {
            var groupName = e.Group?.GroupName ?? e.Message.GroupId.ToString();
            var senderId = e.Sender?.UserId ?? e.Message.SenderId;
            var senderName = e.Sender?.Nickname ?? e.Member?.Nickname ?? senderId.ToString();
            _groupActivities.RecordIncoming(
                e.Message.GroupId,
                groupName,
                senderId,
                senderName,
                rawMessageText,
                stickerResult.ArchivedPaths,
                stickerResult.EmotionHints.Select(hint => hint.Emotion).ToList(),
                stickerResult.EmotionHints.SelectMany(hint => hint.SemanticTags).ToList());
            if (stickerResult.RandomReplySent)
                _groupActivities.RecordBotReply(e.Message.GroupId, "[随机表情回复]");
        }

        // ── 1. 聊天日志 ──
        try
        {
            var senderName = e.Sender?.Nickname ?? e.Member?.Nickname ?? e.Sender?.UserId.ToString() ?? "未知";
            var text = rawMessageText;

            if (e.Message.SourceType == MessageSourceType.Group)
            {
                var groupName = e.Group?.GroupName ?? e.Message.GroupId.ToString();
                Console.WriteLine($"[群聊] {groupName}({e.Message.GroupId}) | {senderName}({e.Sender?.UserId}): {text}");
            }
            else
            {
                Console.WriteLine($"[私聊] {senderName}({e.Sender?.UserId}): {text}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打印聊天日志失败");
        }

        // 群专用的轻度接话/吐槽。它只会在显式允许的群和 QQ 号上运行，
        // 并避开命令、@ 机器人消息及已经发送随机表情的消息。
        // Natural song request, for example: @Hime 播放 伯虎说. It is intentionally
        // handled before AI chat so the request produces a music card instead of prose.
        // Some QQ relationship badges preserve the visible @ text but do not expose the
        // expected self ID in the incoming mention segment. A message that contains any
        // mention plus an explicit play verb is still an unambiguous music command.
        if (MusicCommand.TryExtractNaturalPlayRequest(rawMessageText, isAtBot || hasAnyMention, out var musicKeywords))
        {
            _logger.LogInformation("Music request detected (IsAtBot={IsAtBot}, HasMention={HasMention}, Keywords={Keywords})",
                isAtBot,
                hasAnyMention,
                musicKeywords);
            await _commandBus.SendAsync(
                new PlayMusicRequestCommand(e, musicKeywords),
                cancellationToken);
            if (e.Message.SourceType == MessageSourceType.Group)
                _groupActivities.RecordBotReply(e.Message.GroupId, "[音乐卡片]");
            return;
        }

        // Keep the explicit AI trigger identical in private and group conversations.
        // It is handled before targeted/reactive chat so one event can only produce
        // one AI reply. /ai remains available through Sora's command router.
        if (e.Message.SourceType == MessageSourceType.Group &&
            TryExtractPrivateAiPrompt(
                rawMessageText,
                _privateConversations.TriggerPrefix,
                out var groupAiPrompt))
        {
            if (IsPrivateResetPrompt(groupAiPrompt))
            {
                await _commandBus.SendAsync(
                    new ClearAiConversationCommand(e),
                    cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(groupAiPrompt) || containsVisual)
            {
                await _commandBus.SendAsync(
                    new GenerateAiReplyCommand(e, groupAiPrompt),
                    cancellationToken);
            }
            else
            {
                await AiCommand.Reply(e, "请在 ~ai 后写下想聊的内容～");
            }

            return;
        }

        if (e.Message.SourceType == MessageSourceType.Group)
        {
            await _commandBus.SendAsync(
                new TryTargetedInteractionCommand(
                    e,
                    rawMessageText,
                    containsVisual,
                    isBotDirected,
                    stickerResult.RandomReplySent),
                cancellationToken);

            await _commandBus.SendAsync(
                new TryReactiveConversationCommand(
                    e,
                    rawMessageText,
                    isBotDirected,
                    stickerResult.RandomReplySent,
                    isTargetedInteractionUser),
                cancellationToken);
        }

        // ── 2. 私聊仅通过显式 ~ai 指令进入 AI 对话 ──
        try
        {
            if (e.Message.SourceType != MessageSourceType.Group)
            {
                var senderId = e.Sender?.UserId ?? e.Message.SenderId;
                var isSelfMessage = senderId == e.SelfId;
                if (_privateConversations.Enabled &&
                    !isSelfMessage &&
                    _privateConversations.Allows(senderId) &&
                    TryExtractPrivateAiPrompt(
                        rawMessageText,
                        _privateConversations.TriggerPrefix,
                        out var privatePrompt))
                {
                    if (IsPrivateResetPrompt(privatePrompt))
                    {
                        await _commandBus.SendAsync(
                            new ClearAiConversationCommand(e),
                            cancellationToken);
                    }
                    else if (!string.IsNullOrWhiteSpace(privatePrompt) || containsVisual)
                    {
                        QueuePrivateConversation(e, senderId, privatePrompt, containsVisual);
                    }
                    else
                    {
                        var now = DateTimeOffset.UtcNow;
                        await _interactions.RegisterAsync(
                            new PendingInteraction(
                                Guid.NewGuid().ToString("N"),
                                incoming.ScopeKey,
                                InteractionKinds.PrivateAiPrompt,
                                InteractionMode.HardWait,
                                senderId,
                                now,
                                now.AddMinutes(2)),
                            cancellationToken);
                        await AiCommand.Reply(
                            e,
                            "请继续发送你的问题或图片，我会把下一条消息作为本次输入。2 分钟内有效；发送“取消”可退出等待。");
                    }
                }

                return;
            }

            // ── 3. 群聊 @机器人 → AI 对话（仅当不含 /ai 前缀时，避免与命令系统重复响应） ──
            if (e.Message.SourceType == MessageSourceType.Group)
            {
                var rawText = rawMessageText;
                var trimmed = rawText.Trim();

                // 如果消息以 /ai 开头，交给 AiCommand 的命令系统处理，此处跳过
                if (trimmed.StartsWith("/", StringComparison.Ordinal))
                    return;

                // 检查是否 @了机器人（MessageBody 实现了 IEnumerable<BaseSegment>）
                if (isBotDirected)
                {
                    // GetText() 已剥离 @提及 元素，剩下纯文本内容
                    var prompt = rawText;
                    var hasImage = messageBody?.OfType<ImageSegment>().Any() == true;
                    if (string.IsNullOrWhiteSpace(prompt) && !hasImage)
                    {
                        await AiCommand.Reply(e, "请告诉我你想问什么～");
                        return;
                    }

                    await _commandBus.SendAsync(
                        new GenerateAiReplyCommand(e, prompt),
                        cancellationToken);
                    _groupActivities.RecordBotReply(e.Message.GroupId, "[被动 AI 回复]");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理 @提及 消息失败");
        }
    }

    /// <summary>
    /// Coalesces a burst of ordinary private messages so a user who sends text
    /// followed by several stickers receives one contextual reply instead of a
    /// separate model call for every incoming event.
    /// </summary>
    private void QueuePrivateConversation(
        MessageReceivedEvent message,
        long userId,
        string rawText,
        bool containsVisual)
    {
        PendingPrivateConversation queue;
        var startWorker = false;
        var maximum = Math.Clamp(_privateConversations.MaxMergedMessages, 1, 20);
        lock (_privateBatchSync)
        {
            if (!_pendingPrivateConversations.TryGetValue(userId, out queue!))
            {
                queue = new PendingPrivateConversation();
                _pendingPrivateConversations[userId] = queue;
            }

            queue.Messages.Add(new PendingPrivateMessage(message, rawText, containsVisual));
            if (queue.Messages.Count > maximum)
                queue.Messages.RemoveRange(0, queue.Messages.Count - maximum);
            queue.LastReceivedUtc = DateTime.UtcNow;

            if (!queue.IsProcessing)
            {
                queue.IsProcessing = true;
                startWorker = true;
            }
        }

        _logger.LogInformation(
            "Queued ordinary private message for merged reply (UserId={UserId}, HasVisual={HasVisual})",
            userId,
            containsVisual);
        if (startWorker)
            _ = ProcessPrivateConversationAsync(userId, queue, _privateBatchStop.Token);
    }

    private async Task ProcessPrivateConversationAsync(
        long userId,
        PendingPrivateConversation queue,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (true)
                {
                    TimeSpan remaining;
                    lock (_privateBatchSync)
                    {
                        var window = TimeSpan.FromSeconds(Math.Clamp(_privateConversations.MergeWindowSeconds, 0, 15));
                        remaining = queue.LastReceivedUtc + window - DateTime.UtcNow;
                    }

                    if (remaining <= TimeSpan.Zero)
                        break;
                    await Task.Delay(remaining, cancellationToken);
                }

                List<PendingPrivateMessage> batch;
                lock (_privateBatchSync)
                {
                    // A message can arrive between the final delay and this lock.
                    // In that case begin the quiet-period check again.
                    var window = TimeSpan.FromSeconds(Math.Clamp(_privateConversations.MergeWindowSeconds, 0, 15));
                    if (DateTime.UtcNow - queue.LastReceivedUtc < window)
                        continue;

                    batch = queue.Messages.ToList();
                    queue.Messages.Clear();
                }

                if (batch.Count == 0)
                    continue;

                var latest = batch[^1];
                var prompt = BuildMergedPrivatePrompt(batch);
                try
                {
                    _logger.LogInformation(
                        "Routing merged private conversation to AI (UserId={UserId}, Messages={MessageCount}, Visuals={VisualCount})",
                        userId,
                        batch.Count,
                        batch.Count(item => item.ContainsVisual));
                    await _commandBus.SendAsync(
                        new GenerateAiReplyCommand(latest.Event, prompt),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Merged private conversation reply failed (UserId={UserId})", userId);
                }

                lock (_privateBatchSync)
                {
                    if (queue.Messages.Count == 0)
                    {
                        _pendingPrivateConversations.Remove(userId);
                        queue.IsProcessing = false;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown: pending unsent batches are intentionally discarded.
        }
        finally
        {
            lock (_privateBatchSync)
            {
                if (_pendingPrivateConversations.TryGetValue(userId, out var current) && ReferenceEquals(current, queue))
                {
                    _pendingPrivateConversations.Remove(userId);
                    queue.IsProcessing = false;
                }
            }
        }
    }

    private static string BuildMergedPrivatePrompt(IReadOnlyList<PendingPrivateMessage> batch)
    {
        var textParts = batch
            .Select(item => item.Text.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Take(8)
            .ToArray();
        var visualCount = batch.Count(item => item.ContainsVisual);

        if (textParts.Length == 0)
        {
            return visualCount > 1
                ? $"[The user sent {visualCount} images or stickers in quick succession without text. Give one brief, friendly reaction. Do not claim to know visual details unless trusted local context describes them.]"
                : "[The user sent an image or sticker without text. Give one brief, friendly reaction. Do not claim to know visual details unless trusted local context describes them.]";
        }

        var prefix = batch.Count > 1
            ? "[The following private messages were sent in quick succession. Reply once to their combined conversational meaning; do not answer each line separately.]\n"
            : string.Empty;
        var visualHint = visualCount > 0
            ? $"\n[The burst also contains {visualCount} image or sticker message(s).]"
            : string.Empty;
        return prefix + string.Join('\n', textParts) + visualHint;
    }

    private static bool TryExtractPrivateAiPrompt(
        string? rawText,
        string? configuredPrefix,
        out string prompt)
    {
        prompt = string.Empty;
        var text = rawText?.Trim() ?? string.Empty;
        var prefix = NormalizePrivateTrigger(configuredPrefix);
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (text.Length > prefix.Length && !char.IsWhiteSpace(text[prefix.Length]))
            return false;

        prompt = text[prefix.Length..].TrimStart();
        return true;
    }

    private static string NormalizePrivateTrigger(string? configuredPrefix) =>
        string.IsNullOrWhiteSpace(configuredPrefix) ? "~ai" : configuredPrefix.Trim();

    private static bool IsPrivateResetPrompt(string prompt) =>
        prompt.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
        prompt.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
        prompt.Equals("重置", StringComparison.Ordinal);

    private sealed class PendingPrivateConversation
    {
        public List<PendingPrivateMessage> Messages { get; } = [];
        public DateTime LastReceivedUtc { get; set; }
        public bool IsProcessing { get; set; }
    }

    private sealed record PendingPrivateMessage(
        MessageReceivedEvent Event,
        string Text,
        bool ContainsVisual);
}
