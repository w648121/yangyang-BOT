using Hime.Data.Models;

namespace Hime.Data.Services;

/// <summary>
/// AI 聊天会话服务接口
/// </summary>
public interface IChatService
{
    /// <summary>
    /// 取出会话历史。群聊按群共享，私聊按用户区分。
    /// </summary>
    /// <param name="userId">用户 QQ 号</param>
    /// <param name="groupId">群号（null 表示私聊）</param>
    IReadOnlyList<ChatMessage> GetHistory(long userId, long? groupId = null, string? focus = null);

    /// <summary>
    /// 按当前会话范围、时间表达和话题相关度检索已经归档的长期记忆。
    /// </summary>
    IReadOnlyList<LongTermMemoryRecord> GetRelevantMemories(
        long userId,
        long? groupId,
        string? focus,
        int maximum = 8);

    /// <summary>
    /// 追加一对 user/assistant 消息。群聊按群持久化，用户身份记录在消息上。
    /// </summary>
    /// <param name="groupId">群号（null 表示私聊）</param>
    void AppendTurn(
        long userId,
        string nickname,
        string userMessage,
        IReadOnlyList<string> userImagePaths,
        string assistantMessage,
        IReadOnlyList<string> assistantImagePaths,
        string? assistantEmotion,
        long? groupId = null);

    /// <summary>
    /// 清空会话历史。群聊会清空整个群的 AI 上下文。
    /// </summary>
    /// <param name="groupId">群号（null 表示私聊）</param>
    void Clear(long userId, long? groupId = null);
}
