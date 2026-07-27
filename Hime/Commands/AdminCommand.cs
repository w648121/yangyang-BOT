using System.Text;
using Hime.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Command.Attributes;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Commands;

/// <summary>
/// Owner-only, deterministic diagnostics. These requests bypass the language model so
/// operational facts cannot be hallucinated or altered by the chat persona.
/// </summary>
[CommandGroup(Name = "admin", Prefix = "/")]
public sealed class AdminCommand
{
    private readonly AdminOptions _options;
    private readonly ImageService _images;
    private readonly StickerTagCatalog _stickerTags;
    private readonly OpenCodeServerService _openCode;
    private readonly VoiceSynthesisOptions _voice;
    private readonly MusicOptions _music;
    private readonly IntelligenceSelfTestService _selfTest;
    private readonly PersonaRuntimeProfileService _runtimeProfile;
    private readonly PersonaCorpusService _personaCorpus;
    private readonly PersonaComplianceService _personaCompliance;
    private readonly RuntimeStatusService _runtimeStatus;
    private readonly ILogger<AdminCommand> _logger;

    public AdminCommand(
        IOptions<AdminOptions> options,
        ImageService images,
        StickerTagCatalog stickerTags,
        OpenCodeServerService openCode,
        IOptions<VoiceSynthesisOptions> voice,
        IOptions<MusicOptions> music,
        IntelligenceSelfTestService selfTest,
        PersonaRuntimeProfileService runtimeProfile,
        PersonaCorpusService personaCorpus,
        PersonaComplianceService personaCompliance,
        RuntimeStatusService runtimeStatus,
        ILogger<AdminCommand> logger)
    {
        _options = options.Value;
        _images = images;
        _stickerTags = stickerTags;
        _openCode = openCode;
        _voice = voice.Value;
        _music = music.Value;
        _selfTest = selfTest;
        _runtimeProfile = runtimeProfile;
        _personaCorpus = personaCorpus;
        _personaCompliance = personaCompliance;
        _runtimeStatus = runtimeStatus;
        _logger = logger;
    }

    [Command(
        Expressions = ["admin", "管理"],
        MatchType = Sora.Core.Enums.MatchType.Keyword,
        Description = "管理员诊断：/admin 状态 | 磁盘 [C] | 表情 | 权限 | 文件")]
    public async ValueTask Execute(MessageReceivedEvent e)
    {
        if (!IsAuthorizedPrivateRequest(e))
            return;

        var argument = ExtractArgument(e.Message.Body?.GetText()).Trim();
        var (verb, remainder) = SplitVerb(argument);
        var response = verb.ToLowerInvariant() switch
        {
            "" or "帮助" or "help" => BuildHelp(),
            "状态" or "status" => BuildStatus(),
            "权限" or "permissions" => BuildPermissions(),
            "磁盘" or "disk" => BuildDiskStatus(remainder),
            "表情" or "表情库" or "stickers" => BuildStickerStatus(),
            "人格" or "persona" => BuildPersonaStatus(),
            "文件" or "file" => await ReadAllowedFileAsync(remainder),
            "selftest" => _selfTest.RunReport(),
            _ => "未知管理员命令。发送 /admin 查看可用命令。"
        };

        _logger.LogInformation("Admin command executed (UserId={UserId}, Command={Command}).", e.Message.SenderId, verb);
        await SendAsync(e, response);
    }

    private bool IsAuthorizedPrivateRequest(MessageReceivedEvent e)
    {
        var userId = e.Sender?.UserId ?? e.Message.SenderId;
        if (!_options.Enabled || !_options.AllowedUserIds.Contains(userId))
            return false;

        return !_options.PrivateChatOnly || e.Message.SourceType != MessageSourceType.Group;
    }

    private string BuildHelp() =>
        "管理员诊断（仅私聊）\n" +
        "/admin 状态\n" +
        "/admin 权限\n" +
        "/admin selftest\n" +
        "/admin 人格\n" +
        "/admin 磁盘 [C]\n" +
        "/admin 表情\n" +
        $"/admin 文件 personas/{_runtimeProfile.Current.PersonaFile}";

    private string BuildPersonaStatus()
    {
        var profile = _runtimeProfile.Current;
        var last = _personaCompliance.LastEvaluation;
        var reasons = last.Reasons.Count == 0 ? "无" : string.Join("、", last.Reasons);
        var selection = _personaCorpus.LastSelection;
        var selected = selection.Count == 0
            ? "尚未检索"
            : string.Join(" | ", selection.Select(item => $"{item.Scene}:{TrimForStatus(item.Text, 30)}"));
        return $"""
            当前人格：{profile.ProfileId}
            版本：{profile.Version}
            语言：{profile.Language}
            人格文件：{profile.PersonaFile}
            语气卡：{profile.StyleCardFile}
            语音：{profile.Voice}
            真实台词语料：{_personaCorpus.Count} 条
            最近检索：{selected}
            最近一致性评分：{last.Score}/100（重写={last.Rewritten}，原因={reasons}）
            """;
    }

    private string BuildStatus()
    {
        var enabledVoices = _voice.Voices
            .Where(pair => pair.Value.Enabled)
            .Select(pair => pair.Key)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return $"""
            {_runtimeStatus.BuildTextReport()}
            表情文件：{_images.AvailableImages.Count} 个
            语音：{(_voice.Enabled ? string.Join(", ", enabledVoices) : "未启用")}
            点歌：{(_music.Enabled ? "已启用" : "未启用")}
            """;
    }

    private static string BuildPermissions() =>
        """
        当前管理员能力（只读、安全范围）：
        - /admin 磁盘：查看本机盘符容量
        - /admin 表情：查看已加载表情统计
        - /admin 状态：查看机器人组件状态
        - /admin 文件：仅读取允许目录下的文本文件

        不提供：任意命令执行、文件写入/删除、项目外路径、配置/数据库/密钥读取、任意网页抓取。
        """;

    private static string BuildDiskStatus(string argument)
    {
        var letter = string.IsNullOrWhiteSpace(argument) ? 'C' : char.ToUpperInvariant(argument.Trim()[0]);
        if (letter is < 'A' or > 'Z' || argument.Trim().Length > 2)
            return "盘符格式应为 /admin 磁盘 C。";

        var root = $"{letter}:\\";
        try
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return $"{letter} 盘当前未就绪。";

            return $"{letter} 盘：总计 {ToGiB(drive.TotalSize)} GiB，剩余 {ToGiB(drive.AvailableFreeSpace)} GiB，可用 {drive.AvailableFreeSpace * 100d / drive.TotalSize:F1}% 。";
        }
        catch (Exception)
        {
            return $"无法读取 {letter} 盘状态。";
        }
    }

    private string BuildStickerStatus()
    {
        var paths = _images.AvailableImages.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var entries = _stickerTags.GetEntries(paths)
            .Where(entry => entry.CatalogVersion >= StickerTagCatalog.CurrentCatalogVersion)
            .ToList();
        var counts = entries
            .Select(entry => entry.EmotionScores.OrderByDescending(pair => pair.Value).FirstOrDefault().Key)
            .Where(emotion => !string.IsNullOrWhiteSpace(emotion))
            .GroupBy(emotion => emotion, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key} {group.Count()} 个");

        var details = string.Join("，", counts);
        return $"已加载 {paths.Count} 个图片文件；动态标签 {entries.Count} 个；未识别 {paths.Count - entries.Count} 个。" +
               (string.IsNullOrWhiteSpace(details) ? string.Empty : $"主情绪：{details}。");
    }

    private async Task<string> ReadAllowedFileAsync(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
            return $"用法：/admin 文件 personas/{_runtimeProfile.Current.PersonaFile}";

        if (!TryResolveAllowedTextFile(argument, out var path))
            return "该文件不在允许的只读目录内，或不是可读取的文本文件。";

        if (!File.Exists(path))
            return "文件不存在。";

        try
        {
            var maxCharacters = Math.Clamp(_options.MaxFileReadCharacters, 500, 12000);
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[maxCharacters + 1];
            var count = await reader.ReadAsync(buffer);
            var content = new string(buffer, 0, Math.Min(count, maxCharacters)).Trim();
            var suffix = count > maxCharacters ? "\n…（内容已截断）" : string.Empty;
            return $"{Path.GetRelativePath(AppContext.BaseDirectory, path)}\n{content}{suffix}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Admin file read failed for {Path}.", argument);
            return "文件读取失败。";
        }
    }

    private bool TryResolveAllowedTextFile(string argument, out string path)
    {
        path = string.Empty;
        var relative = argument.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(relative) || relative.Contains("..", StringComparison.Ordinal))
            return false;

        var extension = Path.GetExtension(relative);
        if (!string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
            return false;

        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var candidate = Path.GetFullPath(relative, baseDirectory);
        foreach (var allowedDirectory in _options.AllowedReadDirectories)
        {
            if (string.IsNullOrWhiteSpace(allowedDirectory) || Path.IsPathFullyQualified(allowedDirectory))
                continue;

            var allowedRoot = Path.GetFullPath(allowedDirectory, baseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (candidate.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            {
                path = candidate;
                return true;
            }
        }

        return false;
    }

    private static string ExtractArgument(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        foreach (var prefix in new[] { "/admin", "/管理" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return text[prefix.Length..].Trim();
        }

        var separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
        return separator < 0 ? string.Empty : text[(separator + 1)..].Trim();
    }

    private static (string Verb, string Remainder) SplitVerb(string argument)
    {
        var separator = argument.IndexOfAny([' ', '\t', '\r', '\n']);
        return separator < 0
            ? (argument, string.Empty)
            : (argument[..separator], argument[(separator + 1)..].Trim());
    }

    private static string ToGiB(long bytes) => (bytes / 1024d / 1024d / 1024d).ToString("F1");

    private static string TrimForStatus(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";

    private static async Task SendAsync(MessageReceivedEvent e, string text)
    {
        if (e.Message.SourceType == MessageSourceType.Group)
            _ = await e.Api.SendGroupMessageAsync(e.Message.GroupId, new MessageBody(text));
        else
            _ = await e.Api.SendFriendMessageAsync(e.Message.SenderId, new MessageBody(text));
    }
}
