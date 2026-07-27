using System.Text.RegularExpressions;
using Hime.Data.Models;
using Hime.Data.Services;
using Hime.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Services;

/// <summary>
/// Lets Hime occasionally join a normal group discussion. This is intentionally separate
/// from command handling and stickers so each path has clear limits.
/// </summary>
public sealed class ReactiveConversationService
{
    private static readonly Regex EmotionMarker = new(
        @"\[(?:emotion|情绪|情緒):\s*([^\]\r\n]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StickerMarker = new(
        @"\[(?:sticker|表情|表情包):\s*([^\]\r\n]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StickerIdMarker = new(
        @"\[sticker-id:\s*([a-zA-Z0-9_.-]{1,160})\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnsafeLink = new(
        @"(?:https?://|www\.)\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IAiClient _ai;
    private readonly GroupResponseStateService _groupResponses;
    private readonly ReactiveConversationOptions _options;
    private readonly SocialTurnCoordinator _socialTurns;
    private readonly ReplyCandidateJudgeService _candidateJudge;
    private readonly PersonaComplianceService _personaCompliance;
    private readonly AutoVoiceDeliveryService _autoVoiceDelivery;
    private readonly ScheduledReplyDispatcher _scheduledReplies;
    private readonly ImageService _images;
    private readonly IConversationTurnRecorder _turnRecorder;
    private readonly ILogger<ReactiveConversationService> _logger;
    private readonly object _sync = new();
    private readonly HashSet<long> _pendingGroups = [];
    private readonly Dictionary<long, DateTimeOffset> _lastGroupReply = new();
    private readonly Dictionary<(long GroupId, long UserId), DateTimeOffset> _lastUserReply = new();
    private readonly Dictionary<long, Queue<DateTimeOffset>> _sentByGroup = new();

    public ReactiveConversationService(
        IAiClient ai,
        GroupResponseStateService groupResponses,
        IOptions<ReactiveConversationOptions> options,
        SocialTurnCoordinator socialTurns,
        ReplyCandidateJudgeService candidateJudge,
        PersonaComplianceService personaCompliance,
        AutoVoiceDeliveryService autoVoiceDelivery,
        ScheduledReplyDispatcher scheduledReplies,
        ImageService images,
        IConversationTurnRecorder turnRecorder,
        ILogger<ReactiveConversationService> logger)
    {
        _ai = ai;
        _groupResponses = groupResponses;
        _options = options.Value;
        _socialTurns = socialTurns;
        _candidateJudge = candidateJudge;
        _personaCompliance = personaCompliance;
        _autoVoiceDelivery = autoVoiceDelivery;
        _scheduledReplies = scheduledReplies;
        _images = images;
        _turnRecorder = turnRecorder;
        _logger = logger;
    }

    public async Task<ReactiveConversationResult> TryReplyAsync(
        IncomingMessage incoming,
        string rawText,
        ConversationFocusDecision? focus,
        bool stickerReplyAlreadySent,
        CancellationToken cancellationToken = default)
    {
        var message = incoming.NativeEvent;
        var groupId = message.Message.GroupId;
        var userId = message.Sender?.UserId ?? message.Message.SenderId;
        var content = rawText.Trim();

        if (!_options.Enabled ||
            message.Message.SourceType != Sora.Core.Enums.MessageSourceType.Group ||
            !_groupResponses.IsEnabled(groupId) ||
            focus?.AllowsNaturalReaction != true ||
            stickerReplyAlreadySent ||
            content.Length < Math.Max(1, _options.MinMessageCharacters) ||
            content.StartsWith('/') ||
            !ShouldReply(focus) ||
            !TryBegin(groupId, userId))
        {
            return ReactiveConversationResult.NotSent;
        }

        try
        {
            var nickname = message.Sender?.Nickname ?? message.Member?.Nickname ?? userId.ToString();
            var turn = TurnContext.FromIncoming(
                incoming,
                content,
                nickname,
                TurnTrigger.Reactive);
            var socialPlan = _socialTurns.Build(new SocialTurnRequest(
                turn,
                message.Message.MessageId,
                message.Group?.GroupName,
                content,
                content,
                HimeStyleScene.GroupReply,
                RequireEmotionMarker: true,
                CorpusMaximum: 2,
                PlotMaximum: 3,
                Focus: focus));
            var history = socialPlan.Messages;
            var generated = await _ai.ChatAsync(
                history,
                senderId: userId,
                ct: cancellationToken,
                requestProfile: socialPlan.RequestProfile);
            var candidateChoice = await _candidateJudge.SelectBestAsync(
                generated,
                socialPlan,
                content,
                userId,
                HimeStyleScene.GroupReply,
                casual: true,
                requireEmotionMarker: true,
                ReplyCandidateJudgeUsage.ReactiveConversation,
                cancellationToken);
            var refined = await _personaCompliance.RefineIfNeededAsync(
                candidateChoice.Reply,
                content,
                "群内自然接话",
                userId,
                casual: true,
                requireEmotionMarker: true,
                recentAssistantReplies: socialPlan.RecentAssistantReplies,
                repeatedCurrentMessageCount: socialPlan.Interaction.RepeatedCurrentMessageCount,
                cancellationToken: cancellationToken);
            var emotion = ExtractEmotion(refined);
            var sticker = ExtractSticker(refined);
            var reply = Normalize(refined);
            if (string.IsNullOrWhiteSpace(reply))
                return ReactiveConversationResult.NotSent;

            var minimum = Math.Max(0, Math.Min(_options.MinNaturalDelaySeconds, _options.MaxNaturalDelaySeconds));
            var maximum = Math.Max(minimum, Math.Max(_options.MinNaturalDelaySeconds, _options.MaxNaturalDelaySeconds));
            var delay = Random.Shared.Next(minimum, maximum + 1);
            if (!TryCommit(groupId, userId))
                return ReactiveConversationResult.NotSent;

            async Task SendReplyAsync(CancellationToken sendToken)
            {
                if (!_groupResponses.IsEnabled(groupId))
                {
                    _logger.LogInformation(
                        "Cancelled delayed reactive reply because group response was stopped (GroupId={GroupId})",
                        groupId);
                    return;
                }

                var replyMessage = new MessageBody()
                    .AddReply(message.Message.MessageId)
                    .AddText(reply);
                var sendResult = await message.Api.SendGroupMessageAsync(groupId, replyMessage, sendToken);
                var platformMessageId = PlatformSendResultInspector.TryGetMessageId(sendResult);
                if (sticker is not null)
                {
                    var stickerMessage = new MessageBody().AddImage(
                        new Uri(Path.GetFullPath(sticker)).AbsoluteUri,
                        Sora.Core.Enums.ImageSubType.Sticker);
                    await message.Api.SendGroupMessageAsync(groupId, stickerMessage, sendToken);
                }
                _turnRecorder.RecordDelivered(
                    turn,
                    new DeliveredTurn(
                        reply,
                        sticker is null ? Array.Empty<string>() : [sticker],
                        emotion,
                        "reactive-conversation")
                    {
                        PlatformMessageId = platformMessageId,
                        SocialIntentId = socialPlan.SocialIntent.IntentId,
                        DialogueAct = socialPlan.Decision.Act.ToString(),
                        CandidateSummary = BuildCandidateSummary(candidateChoice)
                    });
                _autoVoiceDelivery.Enqueue(
                    reply,
                    async (path, token) =>
                    {
                        var audio = new MessageBody().AddAudio(new Uri(Path.GetFullPath(path)).AbsoluteUri);
                        await message.Api.SendGroupMessageAsync(groupId, audio, token);
                    },
                    context: $"reactive-group:{groupId}",
                    emotion: emotion);
                _logger.LogInformation(
                    "Reactive group reply sent (GroupId={GroupId}, UserId={UserId}, Delay={DelaySeconds}s)",
                    groupId,
                    userId,
                    delay);
            }

            var scheduled = delay > 0 && _scheduledReplies.TrySchedule(
                TimeSpan.FromSeconds(delay),
                groupId,
                $"reactive-group:{groupId}:message:{message.Message.MessageId}",
                SendReplyAsync);
            if (!scheduled)
                await SendReplyAsync(cancellationToken);

            return new ReactiveConversationResult(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Reactive group reply failed (GroupId={GroupId}, UserId={UserId})",
                groupId,
                userId);
            return ReactiveConversationResult.NotSent;
        }
        finally
        {
            lock (_sync)
                _pendingGroups.Remove(groupId);
        }
    }

    private bool ShouldReply(ConversationFocusDecision focus)
    {
        var probability =
            Math.Max(0, _options.BaseReplyProbability) +
            Math.Clamp(focus.ParticipationScore, 0, 1) *
            Math.Max(0, _options.ParticipationProbabilityWeight);
        var maximum = Math.Clamp(_options.MaximumReplyProbability, 0, 1);
        return Random.Shared.NextDouble() < Math.Min(maximum, probability);
    }

    private bool TryBegin(long groupId, long userId)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (_pendingGroups.Contains(groupId) || !CanReplyUnderLock(groupId, userId, now))
                return false;

            _pendingGroups.Add(groupId);
            return true;
        }
    }

    private bool TryCommit(long groupId, long userId)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (!CanReplyUnderLock(groupId, userId, now))
                return false;

            if (!_sentByGroup.TryGetValue(groupId, out var sent))
            {
                sent = new Queue<DateTimeOffset>();
                _sentByGroup[groupId] = sent;
            }

            sent.Enqueue(now);
            _lastGroupReply[groupId] = now;
            _lastUserReply[(groupId, userId)] = now;
            return true;
        }
    }

    private bool CanReplyUnderLock(long groupId, long userId, DateTimeOffset now)
    {
        TrimHourlyQuota(groupId, now);
        var groupGap = TimeSpan.FromSeconds(Math.Max(0, _options.MinGroupReplyIntervalSeconds));
        var userGap = TimeSpan.FromSeconds(Math.Max(0, _options.MinUserReplyIntervalSeconds));
        var hourlyLimit = _options.MaxRepliesPerGroupPerHour;
        return (!_lastGroupReply.TryGetValue(groupId, out var lastGroup) || now - lastGroup >= groupGap) &&
               (!_lastUserReply.TryGetValue((groupId, userId), out var lastUser) || now - lastUser >= userGap) &&
               (hourlyLimit <= 0 ||
                !_sentByGroup.TryGetValue(groupId, out var sent) ||
                sent.Count < hourlyLimit);
    }

    private void TrimHourlyQuota(long groupId, DateTimeOffset now)
    {
        if (!_sentByGroup.TryGetValue(groupId, out var sent))
            return;

        var cutoff = now.AddHours(-1);
        while (sent.Count > 0 && sent.Peek() <= cutoff)
            sent.Dequeue();
    }

    private string Normalize(string? generated)
    {
        var clean = EmotionMarker.Replace(generated ?? string.Empty, string.Empty);
        clean = StickerIdMarker.Replace(clean, string.Empty);
        clean = StickerMarker.Replace(clean, string.Empty);
        clean = VisibleReplyTextSanitizer.Clean(clean);
        clean = UnsafeLink.Replace(clean, string.Empty)
            .Replace("@", string.Empty)
            .Replace("\uFF20", string.Empty)
            .Trim();
        var lines = clean
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('/'))
            .Take(2)
            .ToArray();
        if (lines.Length is < 1 or > 2)
            return string.Empty;

        var reply = string.Join(Environment.NewLine, lines);
        var max = Math.Clamp(_options.MaxReplyCharacters, 40, 300);
        return reply.Length <= max ? reply : string.Empty;
    }

    private string ExtractEmotion(string? generated)
    {
        var match = EmotionMarker.Match(generated ?? string.Empty);
        if (!match.Success)
            return _images.NormalizeEmotion(string.Empty);
        var value = match.Groups[1].Value.Trim().ToLowerInvariant();
        return _images.TryNormalizeEmotion(value, out var emotion)
            ? emotion
            : _images.NormalizeEmotion(string.Empty);
    }

    private string? ExtractSticker(string? generated)
    {
        var exact = StickerIdMarker.Match(generated ?? string.Empty);
        if (exact.Success)
            return _images.ResolveStickerId(exact.Groups[1].Value) ??
                   _images.ResolveEmotion(_images.NormalizeEmotion(string.Empty));

        var semantic = StickerMarker.Match(generated ?? string.Empty);
        if (semantic.Success)
            return _images.ResolveSticker(_images.NormalizeStickerTags([semantic.Groups[1].Value]));
        return null;
    }

    private static string BuildCandidateSummary(ReplyCandidateChoice choice)
    {
        var best = choice.Candidates
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        var primary = choice.Candidates.FirstOrDefault(candidate =>
            candidate.Source.Equals("primary", StringComparison.OrdinalIgnoreCase));
        var status = choice.Replaced ? "已替换首版" : "保留首版";
        var primaryScore = primary is null ? "?" : primary.Score.ToString("0.0");
        var bestScore = best is null ? "?" : best.Score.ToString("0.0");
        var reason = best?.Reasons.FirstOrDefault();
        return string.IsNullOrWhiteSpace(reason)
            ? $"{status}，候选 {choice.Candidates.Count}，首版 {primaryScore}，最佳 {bestScore}"
            : $"{status}，候选 {choice.Candidates.Count}，首版 {primaryScore}，最佳 {bestScore}，原因：{Trim(reason, 42)}";
    }

    private static string Trim(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }
}

public sealed record ReactiveConversationResult(bool Sent)
{
    public static readonly ReactiveConversationResult NotSent = new(false);
}
