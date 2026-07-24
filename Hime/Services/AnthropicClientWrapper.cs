using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HimeChatMessage = Hime.Data.Models.ChatMessage;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// AI 客户端实现：直接用 HttpClient 调用兼容接口。
/// 同时支持 Anthropic Messages 与 OpenAI Chat Completions，作为 OpenCode 故障时的直连回退。
/// System prompt 由 PersonaRegistry 按 senderId 提供。
/// </summary>
public class AnthropicClientWrapper : IAiClient
{
    private readonly AiOptions _options;
    private readonly PersonaRegistry _personas;
    private readonly ImageService _imageService;
    private readonly HttpClient _http;

    public AnthropicClientWrapper(IOptions<AiOptions> options, PersonaRegistry personas, ImageService imageService, IHttpClientFactory httpFactory)
    {
        _options = options.Value;
        _personas = personas;
        _imageService = imageService;

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("AI:ApiKey 未配置，请在 appsettings.json 中填写。");

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("AI:BaseUrl 未配置，请在 appsettings.json 中填写。");

        _http = httpFactory.CreateClient("Anthropic");
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        if (UsesOpenAiProtocol())
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
        else
        {
            _http.DefaultRequestHeaders.Add("x-api-key", _options.ApiKey);
            _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> ChatAsync(
        IReadOnlyList<HimeChatMessage> history,
        long senderId,
        CancellationToken ct = default,
        bool applyBoundPersona = true,
        AiRequestProfile? requestProfile = null)
    {
        var persona = applyBoundPersona ? _personas.GetForUser(senderId) : null;
        var systemPrompt = persona?.BuildSystemPrompt();

        // Microsoft Agent Framework 通过 system message 注入固定 Agent 指令。
        // 常规用户消息不会以 system role 传入，因此不会被提升为系统提示。
        var agentInstructions = history
            .Where(message => string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(message => message.Content?.Trim())
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToList();
        if (agentInstructions.Count > 0)
        {
            var sections = new List<string>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                sections.Add(systemPrompt);
            sections.AddRange(agentInstructions!);
            systemPrompt = string.Join("\n\n", sections);
        }

        // 动态追加可用图片列表（让 AI 知道能发哪些本地图片）
        // Agent Framework 的规划器要求严格 JSON，不能再附加普通聊天的表情标记协议。
        var imageList = agentInstructions.Count == 0 ? _imageService.BuildPersonaImageList() : null;
        if (!string.IsNullOrEmpty(imageList))
        {
            systemPrompt = systemPrompt is null
                ? imageList
                : systemPrompt + "\n\n" + imageList;
        }

        var model = string.IsNullOrWhiteSpace(requestProfile?.ModelId)
            ? _options.Model
            : requestProfile!.ModelId;

        Dictionary<string, object?> body;
        string endpoint;
        if (UsesOpenAiProtocol())
        {
            body = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["temperature"] = Math.Clamp(_options.Temperature, 0.01, 1.0),
                ["top_p"] = Math.Clamp(_options.TopP, 0.01, 1.0),
                ["messages"] = BuildOpenAiMessages(history, systemPrompt)
            };
            if (IsGlmModel(model))
            {
                // GLM's compatible endpoint documents max_tokens rather than
                // max_completion_tokens. Ordinary chat disables deep thinking so
                // reasoning cannot consume the whole budget and leave content empty.
                body["max_tokens"] = _options.MaxOutputTokens;
                body["thinking"] = new { type = "disabled" };
            }
            else
            {
                body["max_completion_tokens"] = _options.MaxOutputTokens;
            }

            if (IsMiniMaxModel(model))
            {
                // MiniMax reasoning models otherwise embed <think> in content.
                // The compatible API can return it separately in reasoning_details.
                body["reasoning_split"] = true;
            }
            endpoint = "chat/completions";
        }
        else
        {
            body = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["max_tokens"] = _options.MaxOutputTokens,
                ["temperature"] = Math.Clamp(_options.Temperature, 0.01, 1.0),
                ["top_p"] = Math.Clamp(_options.TopP, 0.01, 1.0),
                ["messages"] = BuildMessages(history)
            };
            if (!string.IsNullOrEmpty(systemPrompt))
                body["system"] = systemPrompt;
            endpoint = "v1/messages";
        }

        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.PostAsync(endpoint, content, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"AI API 调用失败 ({(int)resp.StatusCode} {resp.StatusCode}): {respText}");

        return UsesOpenAiProtocol()
            ? ExtractOpenAiText(respText)
            : ExtractFirstText(respText);
    }

    private bool UsesOpenAiProtocol() =>
        string.Equals(_options.Protocol, "OpenAI", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_options.Protocol, "OpenAICompatible", StringComparison.OrdinalIgnoreCase);

    private static bool IsMiniMaxModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        (model.Equals("M2-her", StringComparison.OrdinalIgnoreCase) ||
         model.StartsWith("MiniMax-", StringComparison.OrdinalIgnoreCase));

    private static bool IsGlmModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        model.StartsWith("glm-", StringComparison.OrdinalIgnoreCase);

    private static List<object> BuildOpenAiMessages(
        IReadOnlyList<HimeChatMessage> history,
        string? systemPrompt)
    {
        var list = new List<object>(history.Count + 1);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            list.Add(new { role = "system", name = "秧秧", content = systemPrompt });

        foreach (var message in history)
        {
            if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
                continue;
            var role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "assistant"
                : "user";
            list.Add(new { role, content = message.Content });
        }
        return list;
    }

    /// <summary>
    /// 把 Hime 的 ChatMessage 列表转成 Anthropic 的 messages 数组。
    /// Anthropic 不允许 messages 里出现 system role，遇到直接跳过（应放到顶层 system 字段）。
    /// </summary>
    private static List<object> BuildMessages(IReadOnlyList<HimeChatMessage> history)
    {
        var list = new List<object>(history.Count);
        foreach (var m in history)
        {
            if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                continue;
            var role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
            list.Add(new { role, content = m.Content });
        }
        return list;
    }

    /// <summary>
    /// 从 Anthropic 响应 JSON 里提取第一个 text block 的 text 字段。
    /// 响应结构：{"content": [{"type": "text", "text": "..."}, ...]}
    /// </summary>
    private static string ExtractFirstText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() == "text" &&
                block.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static string ExtractOpenAiText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return StripThinkingBlocks(content.GetString());
    }

    private static string StripThinkingBlocks(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        while (text.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            var end = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                return string.Empty;
            text = text[(end + "</think>".Length)..].TrimStart();
        }
        return text.Trim();
    }
}
