using Hime.Data.Models;
using Hime.Data.Services;
using Hime.Messaging;
using Hime.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Commands;

/// <summary>
/// Runtime reply learning. Admins can mark a delivered reply as good or bad;
/// later social turns retrieve these examples as dynamic guidance.
/// </summary>
public sealed class LearningCommand(
    ReplyLearningService learning,
    IGroupActivityService groupActivities,
    ConversationRouter router,
    SocialIntentAnalyzer intents,
    PersonaRuntimeProfileService runtime,
    IOptions<AdminOptions> adminOptions,
    ILogger<LearningCommand> logger)
{
    private readonly AdminOptions _admin = adminOptions.Value;

    public async Task ExecuteAsync(
        IncomingMessage incoming,
        CancellationToken cancellationToken = default)
    {
        var senderId = incoming.SenderId;
        if (!_admin.Enabled || !_admin.AllowedUserIds.Contains(senderId))
            return;

        var argument = ExtractArgument(incoming.Text);
        if (await TryHandleManagementAsync(incoming, argument, cancellationToken))
            return;

        var request = ParseLabelAndReason(argument);
        if (string.IsNullOrWhiteSpace(request.Label))
        {
            await incoming.ReplyChannel.SendTextAsync(
                BuildUsage(),
                cancellationToken);
            return;
        }

        var target = ResolveTarget(incoming, request.UseLatestBotReply);
        if (target is null || string.IsNullOrWhiteSpace(target.BotReply))
        {
            await incoming.ReplyChannel.SendTextAsync(
                "没有找到要学习的机器人回复。请引用秧秧那条消息，或在群里发送 /学习 好/坏 上一条 原因。",
                cancellationToken);
            return;
        }

        var route = router.Route(target.UserMessage);
        var decision = new DialogueDecision(
            route.Mode == ConversationMode.Casual ? DialogueAct.React : DialogueAct.Answer,
            IncludeRelationshipContext: route.Mode == ConversationMode.Casual,
            IncludePersonaState: route.Mode == ConversationMode.Casual,
            IncludePlotKnowledge: route.Mode == ConversationMode.Casual,
            IncludeCadenceExamples: route.Mode == ConversationMode.Casual,
            IncludeTrustedClock: route.Mode == ConversationMode.Factual,
            "manual-learning");
        var intent = intents.Analyze(target.UserMessage, decision, route);
        var scene = incoming.IsGroup ? "group_reply" : "private_reply";
        var record = learning.AddExample(new ReplyLearningExampleDraft(
            runtime.Current.ProfileId,
            request.Label,
            intent.IntentId,
            scene,
            target.UserMessage,
            target.BotReply,
            request.Reason,
            senderId,
            incoming.GroupId));

        logger.LogInformation(
            "Reply learning example added (Id={Id}, Label={Label}, Intent={Intent}, UserId={UserId}, GroupId={GroupId}).",
            record.Id,
            record.Label,
            record.IntentId,
            senderId,
            incoming.GroupId);

        var labelText = record.Label == "bad" ? "坏样例" : "好样例";
        var reasonText = string.IsNullOrWhiteSpace(record.Reason) ? "未写原因" : record.Reason;
        await incoming.ReplyChannel.SendTextAsync(
            $"已学习：{labelText}\nID：{DisplayId(record)}\n完整ID：{record.Id}\n意图：{record.IntentId}\n原因：{reasonText}",
            cancellationToken);
    }

    private async Task<bool> TryHandleManagementAsync(
        IncomingMessage incoming,
        string argument,
        CancellationToken cancellationToken)
    {
        var (action, rest) = SplitFirst(argument);
        if (string.IsNullOrWhiteSpace(action))
            return false;

        if (action.Equals("列表", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var (labelToken, countToken) = SplitFirst(rest);
            var label = TryParseLabel(labelToken, out var parsedLabel)
                ? parsedLabel
                : string.Empty;
            var countText = string.IsNullOrWhiteSpace(label) ? labelToken : countToken;
            var maximum = int.TryParse(countText, out var requested) ? requested : 8;
            var records = learning.ListExamples(incoming.GroupId, label, maximum);
            await incoming.ReplyChannel.SendTextAsync(BuildListReply(records), cancellationToken);
            return true;
        }

        if (action.Equals("查看", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("show", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("view", StringComparison.OrdinalIgnoreCase))
        {
            var (id, _) = SplitFirst(rest);
            var record = learning.FindById(id, incoming.GroupId);
            await incoming.ReplyChannel.SendTextAsync(
                record is null
                    ? "没有找到这个学习样例。可以先用 /学习 列表 看看 ID。"
                    : BuildDetailReply(record),
                cancellationToken);
            return true;
        }

        if (action.Equals("删除", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("delete", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            var (id, _) = SplitFirst(rest);
            var deleted = learning.DeleteExample(id, incoming.GroupId, out var record);
            await incoming.ReplyChannel.SendTextAsync(
                deleted && record is not null
                    ? $"已删除学习样例：{DisplayId(record)}（{LabelText(record.Label)}）"
                    : "没有找到可删除的学习样例。可以先用 /学习 列表 看看 ID。",
                cancellationToken);
            return true;
        }

        if (action.Equals("权重", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("weight", StringComparison.OrdinalIgnoreCase))
        {
            var (id, weightText) = SplitFirst(rest);
            if (string.IsNullOrWhiteSpace(id) ||
                !double.TryParse(weightText, out var weight))
            {
                await incoming.ReplyChannel.SendTextAsync(
                    "用法：/学习 权重 <ID> <0.1-5.0>，例如 /学习 权重 ab12cd34 2.0",
                    cancellationToken);
                return true;
            }

            var record = learning.SetWeight(id, incoming.GroupId, weight);
            await incoming.ReplyChannel.SendTextAsync(
                record is null
                    ? "没有找到这个学习样例。可以先用 /学习 列表 看看 ID。"
                    : $"已更新权重：{DisplayId(record)} → {record.Weight:0.##}",
                cancellationToken);
            return true;
        }

        if (action.Equals("统计", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("stats", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("stat", StringComparison.OrdinalIgnoreCase))
        {
            var stats = learning.GetStats(incoming.GroupId);
            await incoming.ReplyChannel.SendTextAsync(
                $"学习样例统计\n总数：{stats.Total}\n好样例：{stats.Good}\n坏样例：{stats.Bad}\n群内样例：{stats.GroupScoped}\n全局样例：{stats.Global}\n平均权重：{stats.AverageWeight:0.##}",
                cancellationToken);
            return true;
        }

        return false;
    }

    private LearningTarget? ResolveTarget(IncomingMessage incoming, bool forceLatestBotReply)
    {
        if (!forceLatestBotReply &&
            !string.IsNullOrWhiteSpace(incoming.QuotedText) &&
            (!incoming.ReplyToUserId.HasValue || incoming.ReplyToUserId == incoming.SelfId))
        {
            return new LearningTarget(
                FindPreviousHumanMessage(incoming.GroupId, DateTime.UtcNow) ?? incoming.Text,
                incoming.QuotedText.Trim());
        }

        if (!incoming.GroupId.HasValue)
            return null;

        var recent = groupActivities
            .GetRecentMessages(incoming.GroupId.Value, 40)
            .OrderBy(message => message.Time)
            .ToArray();
        var bot = recent.LastOrDefault(message => message.IsBot && !string.IsNullOrWhiteSpace(message.Content));
        if (bot is null)
            return null;

        var user = recent
            .Where(message => !message.IsBot && message.Time <= bot.Time)
            .LastOrDefault();
        return new LearningTarget(
            user?.Content ?? incoming.Text,
            bot.Content);
    }

    private string? FindPreviousHumanMessage(long? groupId, DateTime beforeUtc)
    {
        if (!groupId.HasValue)
            return null;
        return groupActivities
            .GetRecentMessages(groupId.Value, 40)
            .Where(message => !message.IsBot && message.Time <= beforeUtc)
            .OrderBy(message => message.Time)
            .LastOrDefault()
            ?.Content;
    }

    private static LearningRequest ParseLabelAndReason(string argument)
    {
        var (first, rest) = SplitFirst(argument);
        var (useLatest, reason) = StripLatestTarget(rest);
        if (TryParseLabel(first, out var label))
            return new LearningRequest(label, reason, useLatest);
        return new LearningRequest(string.Empty, argument.Trim(), false);
    }

    private static bool TryParseLabel(string value, out string label)
    {
        var normalized = value.Trim();
        if (normalized.Equals("好", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("good", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("正例", StringComparison.OrdinalIgnoreCase))
        {
            label = "good";
            return true;
        }

        if (normalized.Equals("坏", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("bad", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("差", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("反例", StringComparison.OrdinalIgnoreCase))
        {
            label = "bad";
            return true;
        }

        label = string.Empty;
        return false;
    }

    private static string ExtractArgument(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        foreach (var prefix in new[] { "/学习", "/learn" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return text[prefix.Length..].Trim();
        }

        return string.Empty;
    }

    private static (string First, string Remainder) SplitFirst(string value)
    {
        var normalized = value.Trim();
        var separator = normalized.IndexOfAny([' ', '\t', '\r', '\n']);
        return separator < 0
            ? (normalized, string.Empty)
            : (normalized[..separator], normalized[(separator + 1)..].Trim());
    }

    private static string BuildUsage() =>
        """
        用法：
        /学习 好 上一条 这个接梗自然
        /学习 坏 上一条 太客服腔
        /学习 列表 [好|坏] [数量]
        /学习 查看 <ID>
        /学习 删除 <ID>
        /学习 权重 <ID> <0.1-5.0>
        /学习 统计
        """;

    private static string BuildListReply(IReadOnlyList<ReplyLearningRecord> records)
    {
        if (records.Count == 0)
            return "本群还没有学习样例。可以引用秧秧回复，或用 /学习 好/坏 上一条 原因 添加。";

        var lines = records
            .Select((record, index) =>
                $"{index + 1}. {DisplayId(record)}｜{LabelText(record.Label)}｜权重 {record.Weight:0.##}｜{Trim(record.IntentId, 18)}｜{Trim(record.Reason, 22)}")
            .ToArray();
        return "最近学习样例：\n" + string.Join('\n', lines);
    }

    private static string BuildDetailReply(ReplyLearningRecord record) =>
        $"""
        学习样例：{DisplayId(record)}
        完整ID：{record.Id}
        类型：{LabelText(record.Label)}
        意图：{record.IntentId}
        场景：{record.Scene}
        权重：{record.Weight:0.##}
        使用次数：{record.UsedCount}
        原因：{(string.IsNullOrWhiteSpace(record.Reason) ? "未写" : record.Reason)}
        用户原文：{Trim(record.UserMessage, 120)}
        机器人回复：{Trim(record.BotReply, 180)}
        """;

    private static string DisplayId(ReplyLearningRecord record)
    {
        var id = record.Id.StartsWith("learn:", StringComparison.OrdinalIgnoreCase)
            ? record.Id["learn:".Length..]
            : record.Id;
        return id.Length <= 8 ? id : id[..8];
    }

    private static string LabelText(string label) =>
        label.Equals("bad", StringComparison.OrdinalIgnoreCase) ? "坏样例" : "好样例";

    private static string Trim(string? value, int maximum)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum] + "…";
    }

    private static (bool UseLatest, string Reason) StripLatestTarget(string value)
    {
        var normalized = TrimLearningReason(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return (false, string.Empty);

        foreach (var marker in LatestReplyMarkers)
        {
            if (!StartsWithTargetMarker(normalized, marker))
                continue;

            var reason = normalized[marker.Length..];
            return (true, TrimLearningReason(reason));
        }

        return (false, normalized);
    }

    private static bool StartsWithTargetMarker(string value, string marker)
    {
        if (!value.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            return false;
        if (value.Length == marker.Length)
            return true;
        if (marker.All(char.IsAsciiLetter))
            return char.IsWhiteSpace(value[marker.Length]) ||
                   value[marker.Length] is ':' or '：' or '-' or '—' or ',' or '，';
        return true;
    }

    private static string TrimLearningReason(string value) =>
        value.Trim(' ', '\t', '\r', '\n', ':', '：', '-', '—', ',', '，', '。');

    private static readonly string[] LatestReplyMarkers =
    [
        "最近一条",
        "最后一条",
        "上一条",
        "上一句",
        "上条",
        "上句",
        "上一个",
        "刚刚",
        "latest",
        "previous",
        "last"
    ];

    private sealed record LearningRequest(
        string Label,
        string Reason,
        bool UseLatestBotReply);

    private sealed record LearningTarget(
        string UserMessage,
        string BotReply);
}
