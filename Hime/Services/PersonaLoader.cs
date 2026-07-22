using Hime.Data.Models;

namespace Hime.Services;

/// <summary>
/// 从 Markdown 文件加载人设（通用 N 段解析：扫描所有 "## xxx" 顶层段落）。
/// </summary>
public static class PersonaLoader
{
    /// <summary>
    /// 从指定目录下加载文件并解析。文件不存在或读失败抛异常，由上层决定是否降级。
    /// </summary>
    public static Persona Load(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"人设文件不存在: {path}", path);

        var text = File.ReadAllText(path, System.Text.Encoding.UTF8);
        return new Persona
        {
            SourceFile = fileName,
            Sections = ParseSections(text)
        };
    }

    /// <summary>
    /// 扫描 Markdown，提取所有 "## xxx" 顶层段落。
    /// - 仅匹配以 "## " 开头的行（不是 "### " 或 "# "）
    /// - 段落内容从该行下一行起，到下一个 "## xxx" 或文件末为止
    /// - 段落内原样保留（含 ###、表格、列表、引用、代码、加粗）
    /// - 第一个 "## xxx" 之前的元信息（一级标题、引用、分隔线）忽略
    /// </summary>
    private static List<PersonaSection> ParseSections(string text)
    {
        // 统一换行符，避免 \r\n 干扰
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sections = new List<PersonaSection>();
        string? currentTitle = null;
        var currentLines = new List<string>();

        foreach (var line in lines)
        {
            // 仅匹配 "## "（两个 # + 空格），排除 "### " 和 "# "
            if (line.StartsWith("## ", StringComparison.Ordinal) &&
                !line.StartsWith("### ", StringComparison.Ordinal))
            {
                // 提交上一段
                if (currentTitle is not null)
                    sections.Add(new PersonaSection(
                        currentTitle,
                        string.Join("\n", currentLines).TrimEnd()));

                currentTitle = line[3..].Trim();
                currentLines = new List<string>();
            }
            else if (currentTitle is not null)
            {
                // 在段落内：保留所有内容（含 ### 子标题、表格、列表、引用等）
                currentLines.Add(line);
            }
            // 在第一个 "## xxx" 之前的元信息直接忽略
        }

        // 提交最后一段
        if (currentTitle is not null)
            sections.Add(new PersonaSection(
                currentTitle,
                string.Join("\n", currentLines).TrimEnd()));

        return sections;
    }
}