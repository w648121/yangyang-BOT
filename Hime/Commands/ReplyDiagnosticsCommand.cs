using Hime.Data;
using Hime.Data.Models;
using Hime.Messaging;
using Hime.Services;
using LiteDB;
using Microsoft.Extensions.Options;

namespace Hime.Commands;

/// <summary>
/// Administrator-facing explanation for the most recent delivered bot reply.
/// It reads the local turn ledger only; it never calls the language model.
/// </summary>
public sealed class ReplyDiagnosticsCommand(
    HimeDbContext database,
    IOptions<AdminOptions> adminOptions)
{
    private readonly AdminOptions _admin = adminOptions.Value;

    public async Task ExecuteAsync(
        IncomingMessage incoming,
        CancellationToken cancellationToken = default)
    {
        if (!_admin.Enabled || !_admin.AllowedUserIds.Contains(incoming.SenderId))
            return;

        var argument = ExtractArgument(incoming.Text);
        if (!IsLastArgument(argument))
        {
            await incoming.ReplyChannel.SendTextAsync(
                "用法：/诊断 上一条\n会查看本群或当前私聊最近一次机器人回复的来源、意图、情绪和可学习建议。",
                cancellationToken);
            return;
        }

        var latest = FindLatestTurn(incoming);
        await incoming.ReplyChannel.SendTextAsync(
            latest is null
                ? "还没有找到最近的机器人回复记录。等秧秧正常回复一条后再试试。"
                : BuildReply(latest),
            cancellationToken);
    }

    private ConversationTurnRecord? FindLatestTurn(IncomingMessage incoming)
    {
        var turns = database.Database.GetCollection<ConversationTurnRecord>("conversation_turns");
        return incoming.GroupId.HasValue
            ? turns
                .Find(record => record.GroupId == incoming.GroupId)
                .OrderByDescending(record => record.DeliveredAtUtc)
                .FirstOrDefault()
            : turns
                .Find(record => record.ScopeKey == incoming.ScopeKey)
                .OrderByDescending(record => record.DeliveredAtUtc)
                .FirstOrDefault();
    }

    private static string BuildReply(ConversationTurnRecord record)
    {
        var beijing = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(record.DeliveredAtUtc, DateTimeKind.Utc),
            BeijingTimeZone());
        var assistantMessageId = record.AssistantMessageId.HasValue && record.AssistantMessageId > 0
            ? record.AssistantMessageId.Value.ToString()
            : "未记录";
        var socialIntent = string.IsNullOrWhiteSpace(record.SocialIntentId)
            ? "未记录"
            : record.SocialIntentId;
        var dialogueAct = string.IsNullOrWhiteSpace(record.DialogueAct)
            ? "未记录"
            : record.DialogueAct;
        var candidateSummary = string.IsNullOrWhiteSpace(record.CandidateSummary)
            ? "未记录"
            : record.CandidateSummary;

        return $"""
        上一条回复诊断
        时间：{beijing:yyyy-MM-dd HH:mm:ss} 北京时间
        触发：{record.Trigger}
        来源：{record.Source}
        回复消息ID：{assistantMessageId}
        话题：{Blank(record.TopicId)}
        社交意图：{socialIntent}
        对话动作：{dialogueAct}
        情绪：{Blank(record.Emotion)}
        候选裁判：{candidateSummary}
        用户：{Blank(record.Nickname)}({record.UserId})
        用户原文：{Trim(record.UserText, 120)}
        机器人回复：{Trim(record.AssistantText, 180)}
        媒体：图片 {record.AssistantImagePaths.Count} 张

        如果这条不好：/学习 坏 上一条 原因
        如果这条很好：/学习 好 上一条 原因
        """;
    }

    private static string ExtractArgument(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        foreach (var prefix in new[] { "/诊断", "/diagnose" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return text[prefix.Length..].Trim();
        }

        return string.Empty;
    }

    private static bool IsLastArgument(string argument)
    {
        var normalized = argument.Trim();
        return string.IsNullOrWhiteSpace(normalized) ||
               normalized.Equals("上一条", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("上条", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("最近一条", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("last", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("latest", StringComparison.OrdinalIgnoreCase);
    }

    private static string Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未记录" : value.Trim();

    private static string Trim(string? value, int maximum)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum] + "…";
    }

    private static TimeZoneInfo BeijingTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        }
    }
}
