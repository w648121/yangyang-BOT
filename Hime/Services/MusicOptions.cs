namespace Hime.Services;

/// <summary>Configuration for the local ncm-api-rs metadata search service.</summary>
public sealed class MusicOptions
{
    public bool Enabled { get; set; } = true;
    public bool AutoStartLocalServer { get; set; } = true;
    public string ServerExecutablePath { get; set; } = string.Empty;
    public string? ServerWorkingDirectory { get; set; }
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 3100;
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:3100";
    public int SearchResultLimit { get; set; } = 5;
    public int RequestTimeoutSeconds { get; set; } = 12;
    public int SelectionExpiryMinutes { get; set; } = 10;
    public int MaxKeywordLength { get; set; } = 80;
    public bool PreferRichCard { get; set; } = true;
    public bool UseOneBotMusicCard { get; set; } = true;
    public string OneBotApiBaseUrl { get; set; } = "http://127.0.0.1:3000";
    public string OneBotAccessToken { get; set; } = string.Empty;
}
