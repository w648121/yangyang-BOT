using Hime.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// 启动时一次性加载所有人设；按 QQ 号查询。
/// Bindings 中加载失败的条目降级为 Default；Default 加载失败则全 null（调用方按 null 处理 → 无 system prompt）。
/// </summary>
public sealed class PersonaRegistry
{
    private readonly ILogger<PersonaRegistry> _logger;
    private readonly Persona? _default;
    private readonly Dictionary<string, Persona> _bindings = new(StringComparer.Ordinal);

    public Persona? Default => _default;

    public PersonaRegistry(IOptions<PersonaOptions> options, ILogger<PersonaRegistry> logger)
    {
        _logger = logger;
        var opts = options.Value;

        // 解析 Directory 为绝对路径（相对程序运行目录）
        var dir = Path.IsPathRooted(opts.Directory)
            ? opts.Directory
            : Path.Combine(AppContext.BaseDirectory, opts.Directory);

        // 1. 加载默认人设
        try
        {
            _default = PersonaLoader.Load(dir, opts.DefaultPersonaFile);
            _logger.LogInformation("已加载默认人设: {Path}（{Count} 段，{Len} 字符）",
                Path.Combine(dir, opts.DefaultPersonaFile),
                _default.Sections.Count,
                _default.BuildSystemPrompt()?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载默认人设失败: {File}", opts.DefaultPersonaFile);
        }

        // 2. 加载 Bindings（失败不影响其他绑定）
        foreach (var (qq, fileName) in opts.Bindings)
        {
            try
            {
                var persona = PersonaLoader.Load(dir, fileName);
                _bindings[qq] = persona;
                _logger.LogInformation("已加载绑定人设: QQ {Qq} -> {File}", qq, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载绑定人设失败: QQ {Qq} -> {File}（将回退到默认人设）", qq, fileName);
            }
        }
    }

    /// <summary>
    /// 按 QQ 号取人设：Bindings 命中 → 用命中；未命中或命中失败 → Default；Default 也 null → 返回 null。
    /// </summary>
    public Persona? GetForUser(long qqId)
    {
        var key = qqId.ToString();
        if (_bindings.TryGetValue(key, out var persona))
            return persona;
        return _default;
    }
}