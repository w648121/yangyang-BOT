using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Hime.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Command.Attributes;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Commands;

/// <summary>/help renders a compact adaptive command menu with a text fallback.</summary>
[CommandGroup(Name = "help", Prefix = "/")]
public sealed class HelpCommand
{
    private readonly ILogger<HelpCommand> _logger;
    private readonly IOptionsMonitor<PersonaOptions> _personaOptions;

    public HelpCommand(
        ILogger<HelpCommand> logger,
        IOptionsMonitor<PersonaOptions> personaOptions)
    {
        _logger = logger;
        _personaOptions = personaOptions;
    }

    [Command(
        Expressions = ["help", "帮助", "?"],
        MatchType = Sora.Core.Enums.MatchType.Full,
        Description = "查看自适应指令菜单")]
    [SupportedOSPlatform("windows6.1")]
    public async ValueTask Help(MessageReceivedEvent e)
    {
        var sections = BuildSections();
        MessageBody reply;
        try
        {
            var imagePath = HelpMenuRenderer.Render(sections, _personaOptions.CurrentValue.DisplayName);
            reply = new MessageBody().AddImage(new Uri(imagePath).AbsoluteUri, ImageSubType.Normal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Help menu rendering failed; using the text fallback.");
            reply = new MessageBody(BuildHelpText(sections, _personaOptions.CurrentValue.DisplayName));
        }

        if (e.Message.SourceType == MessageSourceType.Group)
            await e.Api.SendGroupMessageAsync(e.Message.GroupId, reply);
        else
            await e.Api.SendFriendMessageAsync(e.Message.SenderId, reply);
    }

    internal static IReadOnlyList<HelpSection> BuildSections()
    {
        var sections = new List<HelpSection>
        {
            new(
                "二次元图片",
                [
                    new HelpEntry("来张涩图  来3张色图", "Lolicon 非 R18、非 AI 原图，最多 10 张"),
                    new HelpEntry("来3张萝莉 白丝涩图", "优先按多个 tag 检索，无结果才降级 keyword"),
                    new HelpEntry("随机涩图  要涩图", "从 DMOE / LoliAPI 随机图库合并转发")
                ])
        };
        var groupTypes = typeof(HelpCommand).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<CommandGroupAttribute>() is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        foreach (var type in groupTypes)
        {
            var group = type.GetCustomAttribute<CommandGroupAttribute>()!;
            var prefix = group.Prefix ?? string.Empty;
            var commands = type.GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(method => method.GetCustomAttribute<CommandAttribute>())
                .Where(attribute => attribute is not null)
                .Cast<CommandAttribute>()
                .Select(attribute => new HelpEntry(
                    string.Join("  ", attribute.Expressions.Take(2).Select(expression => prefix + expression)),
                    string.IsNullOrWhiteSpace(attribute.Description) ? "暂无说明" : attribute.Description.Trim()))
                .OrderBy(entry => entry.Command, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (commands.Count == 0)
                continue;

            var name = string.IsNullOrWhiteSpace(group.Name) ? type.Name : group.Name;
            sections.Add(new HelpSection(name, commands));
        }

        return sections;
    }

    internal static string BuildHelpText(IReadOnlyList<HelpSection>? sections = null, string? personaDisplayName = null)
    {
        sections ??= BuildSections();
        personaDisplayName = string.IsNullOrWhiteSpace(personaDisplayName) ? "HIME" : personaDisplayName.Trim();
        var text = new StringBuilder($"{personaDisplayName} 可用指令\n");
        foreach (var section in sections)
        {
            text.AppendLine().Append('[').Append(section.Name).AppendLine("]");
            foreach (var entry in section.Entries)
                text.Append("  ").Append(entry.Command).Append(" - ").AppendLine(entry.Description);
        }
        return text.ToString().TrimEnd();
    }
}

internal sealed record HelpEntry(string Command, string Description);
internal sealed record HelpSection(string Name, IReadOnlyList<HelpEntry> Entries);
