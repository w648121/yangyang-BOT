using HimeChatMessage = Hime.Data.Models.ChatMessage;

namespace Hime.Services;

/// <summary>
/// AI 客户端抽象
/// </summary>
public interface IAiClient
{
    /// <summary>
    /// 发送多轮对话历史，返回 AI 的回复文本
    /// </summary>
    /// <param name="history">完整对话历史（含 system 提示词）</param>
    /// <param name="senderId">发送者 QQ 号（用于按账号选人设）</param>
    /// <param name="applyBoundPersona">
    /// True for ordinary Hime conversation. False for a specialized workflow that supplies
    /// its own complete system prompt and must not inherit the default Hime persona.
    /// </param>
    Task<string> ChatAsync(
        IReadOnlyList<HimeChatMessage> history,
        long senderId,
        CancellationToken ct = default,
        bool applyBoundPersona = true,
        AiRequestProfile? requestProfile = null);
}
