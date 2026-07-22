using Hime.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Command.Attributes;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Commands;

/// <summary>Trusted sticker-library maintenance commands for explicitly authorized QQ accounts.</summary>
[CommandGroup(Name = "sticker", Prefix = "/")]
public sealed class StickerCommand
{
    private const int PageSize = 5;
    private readonly AdminOptions _admin;
    private readonly StickerManagementService _stickers;
    private readonly ILogger<StickerCommand> _logger;

    public StickerCommand(
        IOptions<AdminOptions> admin,
        StickerManagementService stickers,
        ILogger<StickerCommand> logger)
    {
        _admin = admin.Value;
        _stickers = stickers;
        _logger = logger;
    }

    [Command(
        Expressions = ["sticker", "表情"],
        MatchType = Sora.Core.Enums.MatchType.Keyword,
        Description = "表情管理：/表情 帮助 | 添加 | 列表 | 重建 | 未识别 | 修改 | 标签")]
    public async ValueTask Execute(MessageReceivedEvent e)
    {
        if (!IsAuthorizedRequest(e))
            return;

        var argument = ExtractArgument(e.Message.Body?.GetText()).Trim();
        var (verb, remainder) = SplitVerb(argument);
        try
        {
            switch (verb.ToLowerInvariant())
            {
                case "":
                case "帮助":
                case "help":
                case "用法":
                    await ReplyAsync(e, BuildHelp());
                    break;
                case "添加":
                case "add":
                    await AddAsync(e, remainder);
                    break;
                case "重建":
                case "扫描":
                case "rebuild":
                case "scan":
                    await RebuildAsync(e);
                    break;
                case "未识别":
                case "unknown":
                    await ShowUnknownAsync(e, remainder);
                    break;
                case "列表":
                case "list":
                    await ShowListAsync(e, remainder);
                    break;
                case "标注":
                case "指定":
                case "修改":
                case "改标签":
                case "tag":
                    await SetTagAsync(e, remainder, append: false);
                    break;
                case "追加":
                case "增补":
                case "append":
                    await SetTagAsync(e, remainder, append: true);
                    break;
                case "标签":
                case "参考":
                case "labels":
                    await ReplyAsync(e, BuildEmotionReference(remainder));
                    break;
                default:
                    await ReplyAsync(e, "未知表情命令。发送 /表情 帮助 查看用法。");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sticker admin command failed (UserId={UserId}, Verb={Verb})", e.Message.SenderId, verb);
            await ReplyAsync(e, "表情管理操作失败，错误已写入日志。请稍后重试。");
        }
    }

    private async Task AddAsync(MessageReceivedEvent e, string labelText)
    {
        var requestedLabels = ParseLabels(labelText);
        if (!string.IsNullOrWhiteSpace(labelText) && StickerLabelVocabulary.ResolveMany(requestedLabels).Count == 0)
        {
            await ReplyAsync(e, "没有识别出有效标签。发送 /表情 标签 查看可用的动态标签。");
            return;
        }

        await ReplyAsync(e, requestedLabels.Count == 0
            ? "收到，正在去重并自动识别图片；GIF 会抽取首帧、中间帧和末帧……"
            : "收到，正在去重入库；你指定的标签会追加到现有及模型标签中……");
        var userId = e.Sender?.UserId ?? e.Message.SenderId;
        long? groupId = e.Message.SourceType == MessageSourceType.Group ? (long)e.Message.GroupId : null;
        var results = await _stickers.AddFromMessageAsync(e.Message.Body, userId, groupId, requestedLabels);
        if (results.Count == 0)
        {
            await ReplyAsync(e, "没有检测到支持的图片。请把 /表情 添加 和 GIF、PNG、JPG/JPEG 或 WebP 放在同一条消息里发送。");
            return;
        }

        foreach (var result in results)
        {
            var prefix = result.AlreadyExisted ? "已存在，重新检查完成" : "已添加";
            if (result.ManualLabel is not null)
            {
                await ReplyAsync(e,
                    $"{prefix}：ID {result.ReferenceId}\n" +
                    $"人工标签：{string.Join(" + ", result.ManualLabel.Emotions)}\n" +
                    $"视觉语义：{string.Join(", ", result.ManualLabel.SemanticTags)}\n" +
                    $"对话意图：{(result.ManualLabel.IntentTags.Count == 0 ? "无" : string.Join(", ", result.ManualLabel.IntentTags))}\n" +
                    "人工标签优先，重建不会覆盖。");
                continue;
            }
            if (result.Analysis is null)
            {
                await ReplyAsync(e, $"{prefix}：ID {result.ReferenceId}\n模型暂时无法识别，已加入 /表情 未识别 列表。");
                continue;
            }

            await ReplyAsync(e, $"{prefix}：ID {result.ReferenceId}\n{FormatAnalysis(result.Analysis)}");
        }
    }

    private async Task RebuildAsync(MessageReceivedEvent e)
    {
        await ReplyAsync(e, "开始重新分析全部已审核表情。GIF 会抽取首帧、中间帧和末帧；人工标注不会被覆盖。");
        var result = await _stickers.RebuildAllAsync();
        await ReplyAsync(e,
            $"表情目录重建完成\n" +
            $"总数：{result.Total}\n识别成功：{result.Recognized}\n未识别：{result.Unrecognized}\n" +
            $"保留人工标注：{result.PreservedManual}\n实际分析帧数：{result.AnalyzedFrames}\n" +
            "发送 /表情 未识别 查看需要人工处理的图片。");
    }

    private async Task ShowUnknownAsync(MessageReceivedEvent e, string argument)
    {
        var all = _stickers.GetUnrecognized();
        if (all.Count == 0)
        {
            await ReplyAsync(e, "当前没有未识别表情。所有已审核表情都有情绪标签。");
            return;
        }

        var pageCount = Math.Max(1, (int)Math.Ceiling(all.Count / (double)PageSize));
        var page = int.TryParse(argument.Trim(), out var requested) ? Math.Clamp(requested, 1, pageCount) : 1;
        var items = all.Skip((page - 1) * PageSize).Take(PageSize).ToList();
        await ReplyAsync(e, $"未识别表情 {all.Count} 张，第 {page}/{pageCount} 页。可用 /表情 修改 四位ID 标签1|标签2 完全替换标签。");
        foreach (var item in items)
            await SendStickerPreviewAsync(e, item);
    }

    private async Task ShowListAsync(MessageReceivedEvent e, string argument)
    {
        var parts = argument.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var page = 1;
        if (parts.Count > 0 && int.TryParse(parts[^1], out var requestedPage))
        {
            page = requestedPage;
            parts.RemoveAt(parts.Count - 1);
        }
        var filter = string.Join(' ', parts);
        var all = _stickers.GetManagedStickers(filter);
        if (all.Count == 0)
        {
            await ReplyAsync(e, string.IsNullOrWhiteSpace(filter) ? "当前没有可管理的表情图片。" : $"没有找到标签或关键词“{filter}”对应的表情。");
            return;
        }

        var pageCount = Math.Max(1, (int)Math.Ceiling(all.Count / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        await ReplyAsync(e,
            $"表情列表 {all.Count} 张，第 {page}/{pageCount} 页" +
            (string.IsNullOrWhiteSpace(filter) ? "" : $"，筛选：{filter}") +
            "。覆盖标签用：/表情 修改 四位ID 标签1|标签2；保留旧标签并增补用：/表情 追加 四位ID 标签1|标签2");
        foreach (var item in all.Skip((page - 1) * PageSize).Take(PageSize))
            await SendStickerPreviewAsync(e, item);
    }

    private async Task SetTagAsync(MessageReceivedEvent e, string argument, bool append)
    {
        var separator = argument.IndexOfAny([' ', '\t', '\r', '\n']);
        if (separator <= 0)
        {
            await ReplyAsync(e, append
                ? "用法：/表情 追加 4827 悲伤|害羞\n保留旧标签并添加本次标签。"
                : "用法：/表情 修改 4827 悲伤|害羞\n删除全部旧标签，仅保留本次提交的标签。\n四位 ID 可从 /表情 列表 或 /表情 未识别 中查看。");
            return;
        }

        var target = argument[..separator].Trim();
        var labels = ParseLabels(argument[(separator + 1)..]);
        var result = append
            ? await _stickers.AppendManualEmotionsAsync(target, labels)
            : await _stickers.SetManualEmotionsAsync(target, labels);
        if (result is null)
        {
            await ReplyAsync(e, "没有找到该四位 ID 或文件名。请先发送 /表情 列表。");
            return;
        }
        if (!result.Success)
        {
            await ReplyAsync(e, "没有有效的动态标签。发送 /表情 标签 查看允许使用的中英文标签。");
            return;
        }

        await ReplyAsync(e,
            $"人工标签已{(append ? "追加" : "覆盖")}：ID {target.Trim().TrimStart('#')}\n" +
            $"情绪：{string.Join(" + ", result.Emotions)}\n" +
            $"语义：{string.Join(", ", result.SemanticTags)}\n" +
            $"意图：{(result.IntentTags.Count == 0 ? "无" : string.Join(", ", result.IntentTags))}\n" +
            (append
                ? "新标签已与旧标签合并；人工标注优先，后台重建不会覆盖。"
                : "旧标签已全部删除，现在仅保留本次提交的标签；后台重建不会覆盖。"));
    }

    private bool IsAuthorizedRequest(MessageReceivedEvent e)
    {
        var userId = e.Sender?.UserId ?? e.Message.SenderId;
        return _admin.Enabled && _admin.AllowedUserIds.Contains(userId);
    }

    private static string FormatAnalysis(AnimeTagResult result)
    {
        var scores = string.Join("，", result.EmotionScores
            .OrderByDescending(pair => pair.Value)
            .Select(pair => $"{pair.Key} {pair.Value:P0}"));
        return $"主情绪：{result.Emotion}（{result.Confidence:P0}）\n" +
               $"多情绪：{scores}\n" +
               $"视觉语义：{string.Join(", ", result.SemanticTags)}\n" +
               $"对话意图：{(result.IntentTags.Count == 0 ? "无" : string.Join(", ", result.IntentTags))}\n" +
               $"动画帧：分析 {result.AnalyzedFrames}/{result.TotalFrames} 帧";
    }

    private static string BuildHelp() =>
        "表情管理指令（仅授权账号可用，支持群聊或私聊）\n" +
        "/表情 添加 + 图片：不写标签时由模型识别（GIF/PNG/JPG/WebP）\n" +
        "/表情 添加 开心|脸红 + 图片：把指定标签追加到模型标签\n" +
        "/表情 列表 [标签] [页码]：查看全部表情、四位 ID 和当前标签\n" +
        "/表情 重建：重新分析全部已审核表情\n" +
        "/表情 未识别 [页码]：显示模型无法识别的表情\n" +
        "/表情 修改 <四位ID> <标签1|标签2>：删除旧标签并完全替换\n" +
        "/表情 追加 <四位ID> <标签1|标签2>：保留旧标签并追加新标签\n" +
        "/表情 标签 [关键词]：查看完整动态标签或某类标签\n" +
        "/表情 帮助：显示本说明";

    private static string BuildEmotionReference(string query)
    {
        IEnumerable<StickerLabelDefinition> definitions = StickerLabelVocabulary.All;
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (StickerLabelVocabulary.TryResolve(query, out var selected))
                definitions = definitions.Where(item => item.BaseEmotion.Equals(selected.BaseEmotion, StringComparison.OrdinalIgnoreCase));
            else
                definitions = definitions.Where(item =>
                    item.Canonical.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.ChineseName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var items = definitions.ToList();
        if (items.Count == 0)
            return $"没有找到“{query}”相关标签。发送 /表情 标签 查看全部。";

        static string GroupName(StickerLabelKind kind) => kind switch
        {
            StickerLabelKind.Emotion => "基础情绪（决定兜底情绪池）",
            StickerLabelKind.Semantic => "视觉语义（画面具体表现）",
            _ => "对话意图（发送它的目的）"
        };
        var groups = items.GroupBy(item => item.Kind).Select(group =>
            GroupName(group.Key) + "\n" + string.Join("\n", group.Select(item =>
                $"{item.ChineseName}({item.Canonical})：{item.Description}")));
        return "动态表情标签参考\n" + string.Join("\n\n", groups) +
               "\n\n可组合不同层次，例如：开心|脸红|俏皮，悲伤|泪眼|安慰意图。" +
               "\n支持空格、|、｜、逗号、顿号、分号、斜杠、加号和 & 分隔。" +
               "\n添加图片时标签会与识别结果合并；修改会删除旧标签并完全替换；追加会保留旧标签并合并新标签。";
    }

    private static async Task SendStickerPreviewAsync(MessageReceivedEvent e, UnrecognizedSticker item)
    {
        var body = new MessageBody($"ID {item.ReferenceId}  未识别")
            .AddImage(new Uri(Path.GetFullPath(item.Path)).AbsoluteUri, ImageSubType.Sticker);
        if (e.Message.SourceType == MessageSourceType.Group)
            await e.Api.SendGroupMessageAsync(e.Message.GroupId, body);
        else
            await e.Api.SendFriendMessageAsync(e.Message.SenderId, body);
    }

    private static async Task SendStickerPreviewAsync(MessageReceivedEvent e, ManagedSticker item)
    {
        var summary = item.Recognized
            ? $"ID {item.ReferenceId}  {item.FallbackEmotion}  [{item.LabelSource}]\n{string.Join(", ", item.Tags.Concat(item.IntentTags).Distinct(StringComparer.OrdinalIgnoreCase))}"
            : $"ID {item.ReferenceId}  未识别";
        var body = new MessageBody(summary)
            .AddImage(new Uri(Path.GetFullPath(item.Path)).AbsoluteUri, ImageSubType.Sticker);
        if (e.Message.SourceType == MessageSourceType.Group)
            await e.Api.SendGroupMessageAsync(e.Message.GroupId, body);
        else
            await e.Api.SendFriendMessageAsync(e.Message.SenderId, body);
    }

    private static IReadOnlyList<string> ParseLabels(string text) =>
        StickerLabelVocabulary.SplitInput(text);

    private static async Task ReplyAsync(MessageReceivedEvent e, string text)
    {
        if (e.Message.SourceType == MessageSourceType.Group)
            await e.Api.SendGroupMessageAsync(e.Message.GroupId, new MessageBody(text));
        else
            await e.Api.SendFriendMessageAsync(e.Message.SenderId, new MessageBody(text));
    }

    private static string ExtractArgument(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        foreach (var prefix in new[] { "/sticker", "/表情" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return text[prefix.Length..].Trim();
        }
        return text;
    }

    private static (string Verb, string Remainder) SplitVerb(string argument)
    {
        var separator = argument.IndexOfAny([' ', '\t', '\r', '\n']);
        return separator < 0
            ? (argument, string.Empty)
            : (argument[..separator], argument[(separator + 1)..].Trim());
    }
}
