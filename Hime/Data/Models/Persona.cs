namespace Hime.Data.Models;

/// <summary>
/// 单个人设的一个段落（小标题 + 原始 Markdown 内容）
/// </summary>
public sealed record PersonaSection(string Title, string Content);

/// <summary>
/// 单个人设（从 Markdown 文件解析得到）。
/// 通用 N 段结构：每个 Markdown 二级标题（## xxx）作为一个段落，
/// 段落内的所有 Markdown 结构（表格、列表、引用、### 子标题等）原样保留。
/// </summary>
public sealed class Persona
{
    /// <summary>人设来源文件名（仅日志用）</summary>
    public string SourceFile { get; init; } = "";

    /// <summary>所有段落，按文件中出现顺序排列；Title 已去掉 "## " 前缀并 Trim</summary>
    public IReadOnlyList<PersonaSection> Sections { get; init; } = Array.Empty<PersonaSection>();

    /// <summary>
    /// 按 【Title】 模板拼接所有非空段落；全空返回 null。
    /// 段落间用 "\n\n" 分隔，段落内保留原 Markdown 结构。
    /// </summary>
    public string? BuildSystemPrompt()
    {
        var parts = Sections
            .Where(s => !string.IsNullOrWhiteSpace(s.Content))
            .Select(s => $"【{s.Title}】\n{s.Content.TrimEnd()}");
        var joined = string.Join("\n\n", parts);
        return joined.Length == 0 ? null : joined;
    }
}