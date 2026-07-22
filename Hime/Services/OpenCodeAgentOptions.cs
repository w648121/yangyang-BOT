namespace Hime.Services;

/// <summary>Configuration for the local, tool-restricted OpenCode agent service.</summary>
public sealed class OpenCodeAgentOptions
{
    public bool Enabled { get; set; } = true;

    public bool AutoStartLocalServer { get; set; } = true;

    public string ServerExecutablePath { get; set; } = "opencode";

    public string BaseUrl { get; set; } = "http://127.0.0.1:42116";

    public string AgentName { get; set; } = "hime-qq";

    public int StartupTimeoutSeconds { get; set; } = 20;

    public int RequestTimeoutSeconds { get; set; } = 90;

    public int CircuitBreakerFailureThreshold { get; set; } = 3;

    public int CircuitBreakerDurationSeconds { get; set; } = 45;

    public int MaxTranscriptCharacters { get; set; } = 12000;
}
