using Hime.Data.Models;
using Hime.Data.Services;
using Hime.Messaging;
using Hime.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Hosting;

/// <summary>
/// 周期性评估已授权群的静默状态。所有硬性频率、时段和白名单检查均发生在调用模型前。
/// </summary>
public sealed class ProactiveAgentService : BackgroundService
{
    private readonly IGroupActivityService _activities;
    private readonly GroupResponseStateService _groupResponses;
    private readonly IGroupMessageSender _sender;
    private readonly ProactiveGroupAgent _agent;
    private readonly ProactiveContentPlanner _contentPlanner;
    private readonly GroupStickerCollector _stickers;
    private readonly AutoVoiceDeliveryService _autoVoiceDelivery;
    private readonly IConversationTurnRecorder _turnRecorder;
    private readonly ConversationMessageDispatcher _messageDispatcher;
    private readonly ScheduledReplyDispatcher _scheduledReplies;
    private readonly ProactiveAgentOptions _options;
    private readonly ILogger<ProactiveAgentService> _logger;

    public ProactiveAgentService(
        IGroupActivityService activities,
        GroupResponseStateService groupResponses,
        IGroupMessageSender sender,
        ProactiveGroupAgent agent,
        ProactiveContentPlanner contentPlanner,
        GroupStickerCollector stickers,
        AutoVoiceDeliveryService autoVoiceDelivery,
        IConversationTurnRecorder turnRecorder,
        ConversationMessageDispatcher messageDispatcher,
        ScheduledReplyDispatcher scheduledReplies,
        IOptions<ProactiveAgentOptions> options,
        ILogger<ProactiveAgentService> logger)
    {
        _activities = activities;
        _groupResponses = groupResponses;
        _sender = sender;
        _agent = agent;
        _contentPlanner = contentPlanner;
        _stickers = stickers;
        _autoVoiceDelivery = autoVoiceDelivery;
        _turnRecorder = turnRecorder;
        _messageDispatcher = messageDispatcher;
        _scheduledReplies = scheduledReplies;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("主动 Agent 已关闭（ProactiveAgent:Enabled=false）");
            return;
        }

        var enabledGroups = _groupResponses.GetEnabledGroupIds();
        _activities.EnsureGroups(enabledGroups);

        _logger.LogInformation(
            "主动 Agent 已启动（演练={DryRun}，数据库启用群数={GroupCount}，静默阈值={IdleMinutes} 分钟）",
            _options.DryRun,
            enabledGroups.Count,
            _options.MinimumHourlyMessages);

        var initialDelay = TimeSpan.FromSeconds(Math.Clamp(_options.InitialDelaySeconds, 10, 1800));
        _logger.LogInformation(
            "Proactive agent will wait {InitialDelaySeconds}s before its first scan to avoid restart bursts",
            initialDelay.TotalSeconds);
        await Task.Delay(initialDelay, stoppingToken);
        await EvaluateAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_options.ScanIntervalSeconds, 15, 3600)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await EvaluateAsync(stoppingToken);
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        if (!_sender.IsReady || IsQuietHours(DateTimeOffset.Now.Hour))
            return;

        var now = DateTime.UtcNow;
        var maximumGroups = Math.Clamp(_options.MaximumGroupsPerScan, 1, 20);
        var handledGroups = 0;
        foreach (var group in _activities.GetGroups()
                     .OrderBy(group => group.LastProactiveAt ?? DateTime.MinValue))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_messageDispatcher.IsBusy(group.GroupId) || _scheduledReplies.IsPending(group.GroupId))
                continue;
            var quota = _activities.GetHourlyQuota(
                group.GroupId,
                _options.MinimumHourlyMessages,
                _options.MaximumHourlyMessages);
            if (!IsCandidate(group, quota, now))
                continue;

            try
            {
                var plan = _contentPlanner.Plan(
                    group,
                    _options,
                    _activities.CanSendProactiveArticle(group.GroupId, _options.MaximumArticlesPerHourPerGroup),
                    now);
                var decision = await _agent.PlanAsync(group, plan, cancellationToken);
                await ExecuteDecisionAsync(group, decision, plan, quota, cancellationToken);
                handledGroups++;
                if (handledGroups >= maximumGroups)
                    break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "主动 Agent 执行失败 (GroupId={GroupId})", group.GroupId);
            }
        }
    }

    private bool IsCandidate(GroupActivityRecord group, ProactiveHourlyQuota quota, DateTime now)
    {
        if (!_groupResponses.IsEnabled(group.GroupId) ||
            quota.Sent >= quota.Target ||
            group.LastIncomingAt == default)
            return false;

        var lastAttempt = quota.LastSentAt;
        if (group.LastProactiveDecisionAt is { } lastDecision &&
            (lastAttempt is null || lastDecision > lastAttempt.Value))
        {
            lastAttempt = lastDecision;
        }

        if (lastAttempt is not { } lastSent)
            return true;

        // 目标为 3/4/5 次时，分别按 20/15/12 分钟分散发送；配置可进一步提高间隔。
        var spacingMinutes = Math.Max(
            Math.Max(1, _options.MinimumIntervalMinutes),
            (int)Math.Ceiling(60d / quota.Target));
        return now - lastSent >= TimeSpan.FromMinutes(spacingMinutes);
    }

    private async Task ExecuteDecisionAsync(
        GroupActivityRecord group,
        ProactiveDecision decision,
        ProactiveContentPlan plan,
        ProactiveHourlyQuota quota,
        CancellationToken cancellationToken)
    {
        if (!_groupResponses.IsEnabled(group.GroupId))
        {
            _logger.LogInformation(
                "Skipping proactive send because group response is disabled (GroupId={GroupId})",
                group.GroupId);
            return;
        }

        if (_messageDispatcher.IsBusy(group.GroupId) || _scheduledReplies.IsPending(group.GroupId))
        {
            _logger.LogDebug(
                "Skipping proactive send because the group has pending incoming/reply work (GroupId={GroupId})",
                group.GroupId);
            return;
        }

        var action = NormalizeAction(plan.Action);

        if (action == ProactiveAction.Sticker)
        {
            await ExecuteStickerDecisionAsync(group, decision, quota, cancellationToken);
            return;
        }

        var text = action == ProactiveAction.Article
            ? SanitizeText(decision.Text, _options.MaxArticleLength)
            : SanitizeText(decision.Text);

        if (string.IsNullOrWhiteSpace(text))
        {
            _activities.RecordProactiveDecision(group.GroupId);
            _logger.LogWarning(
                "跳过空的主动文本，不再注入固定兜底句 (GroupId={GroupId}, Action={Action})",
                group.GroupId,
                action);
            return;
        }

        if (IsDuplicateOfRecentBotReply(group, text))
        {
            _activities.RecordProactiveDecision(group.GroupId);
            _logger.LogInformation(
                "拦截重复或高度相似的主动消息 (GroupId={GroupId}, Action={Action})",
                group.GroupId,
                action);
            return;
        }

        if (_options.DryRun)
        {
            _logger.LogInformation(
                "[演练] 主动 {Action} (GroupId={GroupId}, Slot={Slot}/{Target}, Emotion={Emotion}) | 原因: {Reason}",
                action,
                group.GroupId,
                quota.Sent + 1,
                quota.Target,
                decision.Emotion,
                decision.Reason);
            _activities.RecordProactiveDecision(group.GroupId);
            return;
        }

        await _sender.SendGroupTextAsync(group.GroupId, text, cancellationToken);
        _autoVoiceDelivery.Enqueue(
            text,
            (path, token) => _sender.SendGroupAudioAsync(group.GroupId, path, token),
            context: $"proactive-group:{group.GroupId}",
            emotion: decision.Emotion);

        if (action == ProactiveAction.Article)
            _activities.RecordProactiveArticleSent(group.GroupId, text);
        else
            _activities.RecordProactiveSent(group.GroupId, text);
        _turnRecorder.RecordDelivered(
            TurnContext.ForProactive(group.GroupId, group.GroupName),
            new DeliveredTurn(
                text,
                Array.Empty<string>(),
                decision.Emotion,
                "proactive-agent",
                RecordGroupActivity: false));
        _logger.LogInformation(
            "已发送主动文字，中文语音已入队 (GroupId={GroupId}, Slot={Slot}/{Target}, Action={Action})",
            group.GroupId,
            quota.Sent + 1,
            quota.Target,
            action);
    }

    private ProactiveAction NormalizeAction(ProactiveAction action) => action switch
    {
        ProactiveAction.Article when _options.AllowArticles => action,
        ProactiveAction.Voice when _options.AllowVoice => action,
        ProactiveAction.Sticker when _options.AllowSticker => action,
        _ when _options.AllowText => ProactiveAction.Text,
        _ => ProactiveAction.Sticker
    };

    private async Task ExecuteStickerDecisionAsync(
        GroupActivityRecord group,
        ProactiveDecision decision,
        ProactiveHourlyQuota quota,
        CancellationToken cancellationToken)
    {
        if (_options.DryRun)
        {
            _logger.LogInformation(
                "[演练] 主动表情 (GroupId={GroupId}, Slot={Slot}/{Target}, Emotion={Emotion})",
                group.GroupId,
                quota.Sent + 1,
                quota.Target,
                decision.Emotion);
            _activities.RecordProactiveDecision(group.GroupId);
            return;
        }

        var path = _stickers.GetRandomCollectedSticker(group.GroupId, decision.Emotion);
        if (path is null)
        {
            _activities.RecordProactiveDecision(group.GroupId);
            _logger.LogInformation(
                "没有匹配的主动表情，跳过本次发送 (GroupId={GroupId}, Emotion={Emotion})",
                group.GroupId,
                decision.Emotion);
            return;
        }

        await _sender.SendGroupImageAsync(
            group.GroupId,
            path,
            Sora.Core.Enums.ImageSubType.Sticker,
            cancellationToken);
        _activities.RecordProactiveSent(group.GroupId, "[主动表情]");
        _turnRecorder.RecordDelivered(
            TurnContext.ForProactive(group.GroupId, group.GroupName),
            new DeliveredTurn(
                "[主动表情]",
                [path],
                decision.Emotion,
                "proactive-agent",
                RecordGroupActivity: false));
        _logger.LogInformation(
            "已发送主动表情，不附加固定文字 (GroupId={GroupId}, Slot={Slot}/{Target}, Emotion={Emotion})",
            group.GroupId,
            quota.Sent + 1,
            quota.Target,
            decision.Emotion);
    }

    private bool IsDuplicateOfRecentBotReply(GroupActivityRecord group, string text)
    {
        var threshold = Math.Clamp(_options.DuplicateSimilarityThreshold, 0.5, 1.0);
        return (group.RecentMessages ?? [])
            .Where(message => message.IsBot && !string.IsNullOrWhiteSpace(message.Content))
            .TakeLast(20)
            .Any(message => TextSimilarity(message.Content, text) >= threshold);
    }

    private static double TextSimilarity(string? left, string? right)
    {
        var a = NormalizeForSimilarity(left);
        var b = NormalizeForSimilarity(right);
        if (a.Length == 0 || b.Length == 0)
            return 0;
        if (string.Equals(a, b, StringComparison.Ordinal))
            return 1;
        if (a.Length < 6 || b.Length < 6)
            return 0;

        var aPairs = BuildPairs(a);
        var bPairs = BuildPairs(b);
        var intersection = aPairs.Count(bPairs.Contains);
        var union = aPairs.Count + bPairs.Count - intersection;
        return union == 0 ? 0 : intersection / (double)union;
    }

    private static HashSet<string> BuildPairs(string value)
    {
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < value.Length - 1; i++)
            pairs.Add(value.Substring(i, 2));
        return pairs;
    }

    private static string NormalizeForSimilarity(string? value) =>
        new((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private bool IsQuietHours(int hour)
    {
        var start = Math.Clamp(_options.QuietHoursStart, 0, 23);
        var end = Math.Clamp(_options.QuietHoursEnd, 0, 23);
        if (start == end)
            return false;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    private string SanitizeText(string? text, int? maxLengthOverride = null)
    {
        var value = VisibleReplyTextSanitizer.Clean(text).Replace("@", string.Empty, StringComparison.Ordinal)
            .Replace("[CQ:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Trim();
        var maxLength = Math.Clamp(maxLengthOverride ?? _options.MaxTextLength, 10, 500);
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
