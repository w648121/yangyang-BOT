using System.Reflection;
using System.Text;
using Sora.Command.Attributes;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Commands;

/// <summary>
/// /help - 反射读取当前程序集中所有 [CommandGroup] / [Command] 特性，
/// 自动列出可用指令及其描述。
/// </summary>
[CommandGroup(Name = "help", Prefix = "/")]
public class HelpCommand
{
    [Command(
        Expressions = ["help", "帮助", "?"],
        MatchType = Sora.Core.Enums.MatchType.Full,
        Description = "查看所有可用指令")]
    public async ValueTask Help(MessageReceivedEvent e)
    {
        var text = BuildHelpText();
        var reply = new MessageBody(text);

        if (e.Message.SourceType == MessageSourceType.Group)
            await e.Api.SendGroupMessageAsync(e.Message.GroupId, reply);
        else
            await e.Api.SendFriendMessageAsync(e.Message.SenderId, reply);
    }

    /// <summary>
    /// 从当前程序集反射读取所有标记了 [CommandGroup] 的类及其内部的 [Command] 方法，
    /// 拼接成帮助文本。每个命令组按自己的 Prefix 原样展示，不强行补 /
    /// </summary>
    private static string BuildHelpText()
    {
        var assembly = typeof(HelpCommand).Assembly;
        var sb = new StringBuilder();
        sb.AppendLine("📖 可用指令：");

        // 找出所有带 [CommandGroup] 的类型
        var groupTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<CommandGroupAttribute>() is not null)
            .OrderBy(t => t.FullName);

        var any = false;
        foreach (var type in groupTypes)
        {
            var groupAttr = type.GetCustomAttribute<CommandGroupAttribute>()!;
            var prefix = groupAttr.Prefix ?? string.Empty;       // 不补默认值，原样
            var groupName = string.IsNullOrEmpty(groupAttr.Name) ? type.Name : groupAttr.Name;

            // 找出这个类型里所有带 [Command] 的方法（包括 static 和 instance）
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance
                                          | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<CommandAttribute>() is not null)
                .OrderBy(m => m.Name);

            if (!methods.Any()) continue;

            any = true;
            sb.AppendLine();
            sb.AppendLine($"【{groupName}】");

            foreach (var method in methods)
            {
                var cmdAttr = method.GetCustomAttribute<CommandAttribute>()!;
                var exprs = string.Join(" | ", cmdAttr.Expressions);
                // 注意：实际匹配时 Sora 会把 prefix 拼到 expression 前面（即 "prefix + expr"）
                // 这里只展示用户原始看到的触发词 + 完整形式，便于排查
                sb.AppendLine($"  {prefix}{exprs} - {cmdAttr.Description}");
            }
        }

        if (!any)
            sb.AppendLine("（暂无注册指令）");

        return sb.ToString().TrimEnd();
    }
}