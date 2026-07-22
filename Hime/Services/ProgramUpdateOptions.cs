namespace Hime.Services;

/// <summary>程序版本与面向用户的更新日志配置。</summary>
public sealed class ProgramUpdateOptions
{
    public string CurrentVersion { get; set; } = string.Empty;

    public string ChangeLogFile { get; set; } = "data/program-updates.jsonl";

    public int DefaultEntries { get; set; } = 1;

    public int MaxEntries { get; set; } = 8;
}
