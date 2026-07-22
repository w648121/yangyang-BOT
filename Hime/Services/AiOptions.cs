namespace Hime.Services;

/// <summary>
/// AI 服务配置（绑定 appsettings.json 的 "AI" 节点）
/// 人设内容已迁移到 PersonaRegistry / personas/*.md，本类只保留连接与模型参数。
/// </summary>
public class AiOptions
{
    /// <summary>API 协议：Anthropic 或 OpenAI。</summary>
    public string Protocol { get; set; } = "Anthropic";

    /// <summary>所选协议的 BaseUrl。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API Key</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>模型名。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>单次请求最大输出 token</summary>
    public int MaxOutputTokens { get; set; } = 1024;

    /// <summary>生成随机度；MiniMax 对话模型建议保留适度变化。</summary>
    public double Temperature { get; set; } = 0.85;

    /// <summary>核采样范围。</summary>
    public double TopP { get; set; } = 0.95;
}
