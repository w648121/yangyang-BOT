using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Answers a small set of runtime questions from program state instead of asking a
/// language model to guess. Responses here intentionally expose no credentials,
/// local paths, configuration values, or private chat data.
/// </summary>
public sealed class RuntimeFactResponder
{
    private readonly AdminOptions _admin;

    public RuntimeFactResponder(IOptions<AdminOptions> admin)
    {
        _admin = admin.Value;
    }

    public bool TryRespond(
        string prompt,
        ConversationRoute route,
        long senderId,
        bool isGroup,
        out string response)
    {
        response = string.Empty;
        var text = (prompt ?? string.Empty).Trim().ToLowerInvariant();
        if (route.Mode == ConversationMode.Factual && IsTimeQuestion(text))
        {
            response = $"现在是北京时间 {GetBeijingTime():yyyy-MM-dd HH:mm:ss}。";
            return true;
        }

        if (route.Mode != ConversationMode.Technical)
            return false;

        if (ContainsAny(text, "c盘", "磁盘", "硬盘", "磁盘大小", "磁盘容量"))
        {
            response = IsPrivateAdmin(senderId, isGroup)
                ? "请使用 /admin 磁盘 C 查询真实容量。"
                : "本机磁盘容量只向管理员私聊的 /admin 磁盘 C 提供；我不会猜测具体数值。";
            return true;
        }

        if (ContainsAny(text, "表情库", "表情包数量", "表情统计"))
        {
            response = IsPrivateAdmin(senderId, isGroup)
                ? "请使用 /admin 表情 查看当前已加载的真实统计。"
                : "表情库统计仅向管理员私聊提供；普通对话只能按情绪发送已配置的表情。";
            return true;
        }

        if (ContainsAny(text, "工具", "权限", "能做什么", "可以做什么", "有什么功能"))
        {
            response = "我能收发 QQ 消息、进行对话、发送已配置表情和语音、点歌，并保存必要的聊天上下文。" +
                       "我不能自行执行命令、读取任意文件、检查本机磁盘或访问未授权网页；需要真实状态时会使用明确的程序命令。";
            return true;
        }

        return false;
    }

    private bool IsPrivateAdmin(long senderId, bool isGroup) =>
        !isGroup && _admin.Enabled && _admin.AllowedUserIds.Contains(senderId);

    private static bool IsTimeQuestion(string text) =>
        ContainsAny(text, "现在几点", "几点了", "当前时间", "什么时间", "今天几号", "今天星期", "星期几", "日期");

    private static bool ContainsAny(string text, params string[] markers) =>
        markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));

    private static DateTime GetBeijingTime()
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateTime.UtcNow.AddHours(8);
        }
    }
}
