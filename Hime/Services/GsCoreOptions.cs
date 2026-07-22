namespace Hime.Services;

public sealed class GsCoreOptions
{
    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = "http://127.0.0.1:8765/";

    public bool AutoStartLocalServer { get; set; } = true;

    public string CoreExecutablePath { get; set; } =
        @"D:\PycharmProjects\Hime\GsCore\.venv\Scripts\core.exe";

    public string WorkingDirectory { get; set; } =
        @"D:\PycharmProjects\Hime\GsCore";

    public int StartupTimeoutSeconds { get; set; } = 120;

    public int RequestTimeoutSeconds { get; set; } = 30;

    public string BotId { get; set; } = "onebot";

    public string[] CommandPrefixes { get; set; } = ["ww", "/ww"];

    public string OutputDirectory { get; set; } = "data/gscore-output";

    public int MaxMediaMegabytes { get; set; } = 32;
}
