namespace Hime.Services;

/// <summary>LLBot 暴露的本机 OneBot V11 HTTP API 公共连接配置。</summary>
public sealed class OneBotOptions
{
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:3000";

    public string AccessToken { get; set; } = string.Empty;

    public int RequestTimeoutSeconds { get; set; } = 30;
}
