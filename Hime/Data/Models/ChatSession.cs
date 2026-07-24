using LiteDB;

namespace Hime.Data.Models;

/// <summary>
/// 用户与 AI 的会话（按会话 ID 划分）
/// 私聊：SessionId = "p:{userId}"
/// 群聊：SessionId = "g:{groupId}"（同一群共享上下文）
/// </summary>
public class ChatSession
{
    /// <summary>
    /// 会话 ID（主键）：p:{userId} 或 g:{groupId}
    /// </summary>
    [BsonId]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 旧版会话的用户 QQ 号，仅用于迁移 g:{groupId}:{userId} 数据。
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// 群号（私聊时为 null）
    /// </summary>
    public long? GroupId { get; set; }

    /// <summary>
    /// 旧版会话的用户昵称，仅用于迁移；新数据记录在 ChatMessage 上。
    /// </summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>
    /// 历史消息（按时间顺序）
    /// </summary>
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>
    /// Compact, selected notes for turns that have aged out of the raw model-context
    /// window. This is not a transcript and is always supplied to the model as
    /// untrusted historical data.
    /// </summary>
    public string HistoricalSummary { get; set; } = string.Empty;

    /// <summary>
    /// Persona contract that produced assistant messages in this session. When it
    /// changes, user facts remain available but old assistant wording is removed.
    /// </summary>
    public string ActivePersonaVersion { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the newest turn included in <see cref="HistoricalSummary"/>.</summary>
    public DateTime? HistoricalSummaryThrough { get; set; }

    /// <summary>
    /// 最后活跃时间（UTC）
    /// </summary>
    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;
}
