using System.Text.RegularExpressions;
using Hime.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

public sealed record EmotionalReplyAssessment(
    bool RequiresRewrite,
    IReadOnlyList<string> Reasons)
{
    public static EmotionalReplyAssessment Clean { get; } = new(false, []);
}

/// <summary>
/// Performs a bounded second pass only when an emotionally sensitive reply has
/// a high-confidence defect. Ordinary replies and good emotional replies remain
/// single-call, keeping latency predictable.
/// </summary>
public sealed class EmotionalReplyRefinementService
{
    private static readonly Regex MediaMarker = new(
        @"\[(?:emotion|sticker(?:-id)?|voice):[^\]\r\n]+\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IAiClient _ai;
    private readonly PersonaRuntimeProfileService _runtime;
    private readonly IOptionsMonitor<EmotionalPragmaticsOptions> _options;
    private readonly ILogger<EmotionalReplyRefinementService> _logger;

    public EmotionalReplyRefinementService(
        IAiClient ai,
        PersonaRuntimeProfileService runtime,
        IOptionsMonitor<EmotionalPragmaticsOptions> options,
        ILogger<EmotionalReplyRefinementService> logger)
    {
        _ai = ai;
        _runtime = runtime;
        _options = options;
        _logger = logger;
    }

    public EmotionalReplyAssessment Assess(
        string? reply,
        string? userPrompt,
        EmotionalPragmaticsPlan plan)
    {
        if (!plan.IsActive || !_options.CurrentValue.ReplyQuality.Enabled)
            return EmotionalReplyAssessment.Clean;

        var visible = MediaMarker.Replace(reply ?? string.Empty, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(visible))
            return EmotionalReplyAssessment.Clean;

        var prompt = userPrompt ?? string.Empty;
        var quality = _options.CurrentValue.ReplyQuality;
        var reasons = new List<string>();

        if (ContainsAny(visible, quality.DismissiveMarkers))
            reasons.Add("否定、催停或套话化处理用户情绪");

        if (ContainsNewMarker(visible, prompt, quality.InventedSharedActionMarkers))
            reasons.Add("编造用户没有提供的共同动作、地点或现场条件");

        if (ContainsNewMarker(visible, prompt, quality.UnsupportedThirdPartyCertaintyMarkers))
            reasons.Add("替第三人断言未经证实的想法、感受或知情状态");

        if (plan.AvoidAdvice && ContainsAny(visible, quality.UnsolicitedAdviceMarkers))
            reasons.Add("用户没有索取建议却追加了指令或解决方案");

        if (plan.AvoidQuestions &&
            (visible.Contains('？') || visible.Contains('?')))
        {
            reasons.Add("低信息情绪信号后用问题追赶用户");
        }

        return reasons.Count == 0
            ? EmotionalReplyAssessment.Clean
            : new EmotionalReplyAssessment(true, reasons);
    }

    public async Task<string> RefineIfNeededAsync(
        string? raw,
        string userPrompt,
        string scene,
        long senderId,
        EmotionalPragmaticsPlan plan,
        CancellationToken cancellationToken = default)
    {
        var original = VisibleReplyTextSanitizer.Clean(raw?.Trim());
        var assessment = Assess(original, userPrompt, plan);
        if (!assessment.RequiresRewrite)
            return original;

        var markers = MediaMarker.Matches(original)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var markerText = markers.Length == 0
            ? "无"
            : string.Join(' ', markers);

        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "system",
                Content = _runtime.BuildFinalInstruction(scene, allowEmotionMarker: markers.Length > 0),
                Time = DateTime.UtcNow
            },
            new()
            {
                Role = "system",
                Content = plan.Instruction,
                Time = DateTime.UtcNow
            },
            new()
            {
                Role = "user",
                UserId = senderId,
                Time = DateTime.UtcNow,
                Content = $"""
                    只改写下面的回复草稿，不回答这段指令。
                    当前用户原话：<message>{Trim(userPrompt, 500)}</message>
                    草稿：<draft>{Trim(original, 700)}</draft>
                    已确认的问题：{string.Join('；', assessment.Reasons)}
                    原有媒体标记：{markerText}

                    保留草稿真正想表达的关心，但删除上述问题。只依据用户原话，不新增天气、
                    地点、物品、动作、共同活动、第三人的想法或过去经历；不要把“关心”写成建议。
                    使用一至两句自然简体中文，像熟人当面接话，不写括号动作、客服话术、鸡汤、
                    精致金句或心理分析。若原有媒体标记不是“无”，原样保留在末尾；否则不要新增标记。
                    """
            }
        };

        try
        {
            var rewritten = await _ai.ChatAsync(
                messages,
                senderId,
                cancellationToken,
                applyBoundPersona: true,
                requestProfile: new AiRequestProfile(PreferDirect: true));
            rewritten = VisibleReplyTextSanitizer.Clean(rewritten);
            if (string.IsNullOrWhiteSpace(rewritten))
                return original;

            var revisedAssessment = Assess(rewritten, userPrompt, plan);
            _logger.LogInformation(
                "Emotional reply quality rewrite completed (InitialIssues={InitialIssues}, RemainingIssues={RemainingIssues}, Scene={Scene}).",
                assessment.Reasons.Count,
                revisedAssessment.Reasons.Count,
                scene);

            return revisedAssessment.Reasons.Count <= assessment.Reasons.Count
                ? rewritten
                : original;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Emotional reply quality rewrite failed (Scene={Scene}).", scene);
            return original;
        }
    }

    private static bool ContainsAny(string value, IEnumerable<string> markers) =>
        markers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Any(marker => value.Contains(marker.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool ContainsNewMarker(
        string reply,
        string prompt,
        IEnumerable<string> markers) =>
        markers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Select(marker => marker.Trim())
            .Any(marker =>
                reply.Contains(marker, StringComparison.OrdinalIgnoreCase) &&
                !prompt.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";
}
