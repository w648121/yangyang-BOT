using System.Runtime.CompilerServices;
using HimeChatMessage = Hime.Data.Models.ChatMessage;
using Microsoft.Extensions.AI;

namespace Hime.Services;

/// <summary>
/// 将现有的 Anthropic 兼容客户端适配为 Microsoft.Extensions.AI IChatClient，
/// 从而让 Microsoft Agent Framework 能复用当前的模型配置。
/// </summary>
public sealed class AnthropicChatClientAdapter : IChatClient
{
    private readonly IAiClient _inner;

    public AnthropicChatClientAdapter(IAiClient inner)
    {
        _inner = inner;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var history = messages.Select(message => new HimeChatMessage
        {
            Role = ToHimeRole(message.Role),
            Content = message.Text ?? string.Empty,
            Time = DateTime.UtcNow
        }).ToList();

        // 0 不会命中 QQ 专属绑定，因而会使用默认 Hime 人设。
        var text = await _inner.ChatAsync(history, senderId: 0, cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
            yield return new ChatResponseUpdate(message.Role, message.Text ?? string.Empty);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType == typeof(IChatClient) ? this : null;
    }

    public void Dispose()
    {
    }

    private static string ToHimeRole(ChatRole role)
    {
        if (role == ChatRole.Assistant)
            return "assistant";
        return role == ChatRole.System ? "system" : "user";
    }
}
