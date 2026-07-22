using Hime.Services;
using Sora.Command.Attributes;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;

namespace Hime.Commands;

/// <summary>把短文本合成为本地 RVC、GPT-SoVITS 或 IndexTTS2 语音。</summary>
[CommandGroup(Name = "voice", Prefix = "/")]
public sealed class VoiceCommand
{
    private readonly VoiceSynthesisService _voiceSynthesis;

    public VoiceCommand(VoiceSynthesisService voiceSynthesis)
    {
        _voiceSynthesis = voiceSynthesis;
    }

    [Command(
        Expressions = ["voice"],
        MatchType = Sora.Core.Enums.MatchType.Keyword,
        Description = "本地语音：/voice [音色] 文本；/voice compare 文本")]
    public async ValueTask Speak(MessageReceivedEvent e)
    {
        var raw = e.Message.Body?.GetText()?.Trim() ?? string.Empty;
        var input = raw.StartsWith("/voice", StringComparison.OrdinalIgnoreCase)
            ? raw[6..].Trim()
            : raw;

        if (string.IsNullOrWhiteSpace(input))
        {
            await AiCommand.Reply(
                e,
                $"用法：/voice [音色] [emotion=情绪] 文本\n引擎对比：/voice compare 文本\nIndex 音色对比：/voice compare-index 文本\n情绪强度对比：/voice compare-emotion [emotion=happy] 文本\n可用音色：{string.Join(", ", _voiceSynthesis.AvailableVoices)}");
            return;
        }

        string? voice = null;
        string? emotion = null;
        var text = input;
        var firstSpace = input.IndexOfAny([' ', '\t', '\r', '\n']);
        var firstToken = firstSpace < 0 ? input : input[..firstSpace];
        if (string.Equals(firstToken, "compare-emotion", StringComparison.OrdinalIgnoreCase) || firstToken == "情绪强度对比")
        {
            text = firstSpace < 0 ? string.Empty : input[(firstSpace + 1)..].Trim();
            emotion = "happy";
            var optionSpace = text.IndexOfAny([' ', '\t', '\r', '\n']);
            var optionToken = optionSpace < 0 ? text : text[..optionSpace];
            if (optionToken.StartsWith("emotion=", StringComparison.OrdinalIgnoreCase) ||
                optionToken.StartsWith("情绪=", StringComparison.Ordinal))
            {
                var prefixLength = optionToken.StartsWith("emotion=", StringComparison.OrdinalIgnoreCase)
                    ? "emotion=".Length
                    : "情绪=".Length;
                var candidate = optionToken[prefixLength..].Trim();
                if (candidate.Length is > 0 and <= 32)
                    emotion = candidate;
                text = optionSpace < 0 ? string.Empty : text[(optionSpace + 1)..].Trim();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                await AiCommand.Reply(e, "请填写测试文本。例：/voice compare-emotion emotion=happy 今天的风真舒服。 ");
                return;
            }

            await RunEmotionStrengthComparisonAsync(e, text, emotion);
            return;
        }

        if (string.Equals(firstToken, "compare-index", StringComparison.OrdinalIgnoreCase) || firstToken == "音色对比")
        {
            text = firstSpace < 0 ? string.Empty : input[(firstSpace + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await AiCommand.Reply(e, "请在 compare-index 后填写测试文本。例：/voice compare-index 今州城外的风有些凉。");
                return;
            }

            await RunIndexComparisonAsync(e, text);
            return;
        }

        if (string.Equals(firstToken, "compare", StringComparison.OrdinalIgnoreCase) || firstToken == "对比")
        {
            text = firstSpace < 0 ? string.Empty : input[(firstSpace + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await AiCommand.Reply(e, "请在 compare 后填写测试文本。例：/voice compare 今州城外的风有些凉。");
                return;
            }

            await RunComparisonAsync(e, text);
            return;
        }

        if (_voiceSynthesis.AvailableVoices.Contains(firstToken, StringComparer.OrdinalIgnoreCase))
        {
            voice = firstToken;
            text = firstSpace < 0 ? string.Empty : input[(firstSpace + 1)..].Trim();

            var emotionSpace = text.IndexOfAny([' ', '\t', '\r', '\n']);
            var emotionToken = emotionSpace < 0 ? text : text[..emotionSpace];
            const string englishPrefix = "emotion=";
            const string chinesePrefix = "情绪=";
            if (emotionToken.StartsWith(englishPrefix, StringComparison.OrdinalIgnoreCase) ||
                emotionToken.StartsWith(chinesePrefix, StringComparison.Ordinal))
            {
                var prefixLength = emotionToken.StartsWith(englishPrefix, StringComparison.OrdinalIgnoreCase)
                    ? englishPrefix.Length
                    : chinesePrefix.Length;
                var candidate = emotionToken[prefixLength..].Trim();
                if (candidate.Length is > 0 and <= 32)
                    emotion = candidate;
                text = emotionSpace < 0 ? string.Empty : text[(emotionSpace + 1)..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            await AiCommand.Reply(e, "请在音色名称后填写要说的内容。");
            return;
        }

        // 秧秧中文模型在当前 CPU 上首次合成通常需要一分钟左右。先确认
        // 指令已收到，避免用户因没有即时消息而重复发送同一条请求。
        if (voice?.StartsWith("yangyang", StringComparison.OrdinalIgnoreCase) == true)
        {
            await AiCommand.Reply(e, "已收到，正在准备秧秧中文语音；切换模型时约需 20～40 秒，请勿重复发送。");
        }

        var result = await _voiceSynthesis.SynthesizeAsync(text, voice, emotion: emotion);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.FilePath))
        {
            var detail = string.IsNullOrWhiteSpace(result.Error)
                ? string.Empty
                : $"：{result.Error[..Math.Min(result.Error.Length, 180)]}";
            await AiCommand.Reply(e, $"语音生成失败（{result.Status}）{detail}");
            return;
        }

        await SendAudioAsync(e, result.FilePath);
    }

    private async Task RunComparisonAsync(MessageReceivedEvent e, string text)
    {
        var voices = new[]
        {
            (Name: "yangyang-v2proplus", Label: "① GPT-SoVITS v2ProPlus（零样本秧秧参考）"),
            (Name: "yangyang-indextts2", Label: "② IndexTTS2（音色/情绪分离，低显存模式）")
        };

        await AiCommand.Reply(e, "开始语音对比：先合成 v2ProPlus，再释放 GPT 显存并启动 IndexTTS2。");
        foreach (var item in voices)
        {
            await AiCommand.Reply(e, item.Label);
            var result = await _voiceSynthesis.SynthesizeAsync(text, item.Name);
            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.FilePath))
            {
                var detail = string.IsNullOrWhiteSpace(result.Error)
                    ? string.Empty
                    : $"：{result.Error[..Math.Min(result.Error.Length, 180)]}";
                await AiCommand.Reply(e, $"{item.Label} 生成失败（{result.Status}）{detail}");
                continue;
            }

            await SendAudioAsync(e, result.FilePath);
        }
    }

    private async Task RunIndexComparisonAsync(MessageReceivedEvent e, string text)
    {
        var voices = new[]
        {
            (Name: "yangyang-indextts2", Label: "① IndexTTS2 当前版：原参考＋动态情绪向量"),
            (Name: "yangyang-indextts2-faithful-a", Label: "② 本音优先 A：原参考，不注入情绪向量"),
            (Name: "yangyang-indextts2-faithful-b", Label: "③ 本音优先 B：官方长参考，不注入情绪向量")
        };

        await AiCommand.Reply(e, "开始 IndexTTS2 音色 A/B/C 对比；三条使用完全相同的文本和确定性采样。");
        foreach (var item in voices)
        {
            await AiCommand.Reply(e, item.Label);
            var result = await _voiceSynthesis.SynthesizeAsync(text, item.Name);
            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.FilePath))
            {
                var detail = string.IsNullOrWhiteSpace(result.Error)
                    ? string.Empty
                    : $"：{result.Error[..Math.Min(result.Error.Length, 180)]}";
                await AiCommand.Reply(e, $"{item.Label} 生成失败（{result.Status}）{detail}");
                continue;
            }

            await SendAudioAsync(e, result.FilePath);
        }
    }

    private async Task RunEmotionStrengthComparisonAsync(
        MessageReceivedEvent e,
        string text,
        string emotion)
    {
        double[] strengths = [1.00, 1.10, 1.15, 1.20, 1.25, 1.30, 1.35, 1.40, 1.45, 1.50];
        const string voice = "yangyang-indextts2-faithful-a-emotion15";
        await AiCommand.Reply(
            e,
            $"开始 faithful-a 情绪强度对比：{string.Join("、", strengths.Select(value => value.ToString("0.00")))}；情绪={emotion}。每个强度各生成一条相同文本的语音。");

        foreach (var strength in strengths)
        {
            var label = $"情绪强度 {strength:0.00}";
            await AiCommand.Reply(e, label);
            var result = await _voiceSynthesis.SynthesizeAsync(
                text,
                voice,
                emotion: emotion,
                emotionAlphaOverride: strength);
            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.FilePath))
            {
                var detail = string.IsNullOrWhiteSpace(result.Error)
                    ? string.Empty
                    : $"：{result.Error[..Math.Min(result.Error.Length, 180)]}";
                await AiCommand.Reply(e, $"{label} 生成失败（{result.Status}）{detail}");
                continue;
            }

            await SendAudioAsync(e, result.FilePath);
        }
    }

    private static async Task SendAudioAsync(MessageReceivedEvent e, string filePath)
    {
        var message = new MessageBody().AddAudio(
            new Uri(Path.GetFullPath(filePath)).AbsoluteUri);
        if (e.Message.SourceType == MessageSourceType.Group)
            await e.Api.SendGroupMessageAsync(e.Message.GroupId, message);
        else
            await e.Api.SendFriendMessageAsync(e.Message.SenderId, message);
    }
}
