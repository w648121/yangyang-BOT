using LiteDB;

namespace Hime.Data.Models;

/// <summary>
/// 单条聊天消息（用户或 AI 的发言）
/// </summary>
public class ChatMessage
{
    /// <summary>
    /// Stable application turn identifier shared by the input and delivered reply.
    /// Legacy rows may leave this empty.
    /// </summary>
    public string? TurnId { get; set; }

    /// <summary>
    /// Reply origin such as explicit-ai, runtime-fact, reactive or proactive-agent.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Platform account that received or delivered this message.
    /// </summary>
    public string? AccountId { get; set; }

    /// <summary>
    /// Native platform message identifier when the turn originated from an incoming message.
    /// </summary>
    public string? PlatformMessageId { get; set; }

    /// <summary>
    /// 角色：user / assistant / system
    /// </summary>
    public string Role { get; set; } = "user";

    /// <summary>
    /// 消息文本
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 发言用户 QQ 号。assistant/system 消息为空。
    /// </summary>
    public long? UserId { get; set; }

    /// <summary>
    /// 发言用户昵称。assistant/system 消息为空。
    /// </summary>
    public string? Nickname { get; set; }

    /// <summary>
    /// 消息所属群号；私聊时为空。
    /// </summary>
    public long? GroupId { get; set; }

    /// <summary>
    /// 消息涉及的本地图片绝对路径。
    /// </summary>
    public List<string> ImagePaths { get; set; } = new();

    /// <summary>
    /// AI 回复的规范化情绪标签，例如 happy / sad / serious。
    /// </summary>
    public string? Emotion { get; set; }

    /// <summary>
    /// 消息时间（UTC）
    /// </summary>
    public DateTime Time { get; set; } = DateTime.UtcNow;
}
