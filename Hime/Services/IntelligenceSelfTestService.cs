using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Deterministic health checks for the intelligence boundary. It intentionally makes
/// no model or network request, so the owner can run it safely at any time.
/// </summary>
public sealed class IntelligenceSelfTestService
{
    private readonly ConversationRouter _router;
    private readonly ChatHistoryOptions _history;
    private readonly OpenCodeAgentOptions _openCode;
    private readonly AgentToolsOptions _agentTools;
    private readonly StickerVisionOptions _vision;
    private readonly LocalImageInspector _imageInspector;
    private readonly PersonaPlotKnowledgeService _plotKnowledge;
    private readonly PersonaComplianceService _personaCompliance;

    public IntelligenceSelfTestService(
        ConversationRouter router,
        IOptions<ChatHistoryOptions> history,
        IOptions<OpenCodeAgentOptions> openCode,
        IOptions<AgentToolsOptions> agentTools,
        IOptions<StickerVisionOptions> vision,
        LocalImageInspector imageInspector,
        PersonaPlotKnowledgeService plotKnowledge,
        PersonaComplianceService personaCompliance)
    {
        _router = router;
        _history = history.Value;
        _openCode = openCode.Value;
        _agentTools = agentTools.Value;
        _vision = vision.Value;
        _imageInspector = imageInspector;
        _plotKnowledge = plotKnowledge;
        _personaCompliance = personaCompliance;
    }

    public string RunReport()
    {
        var checks = new List<(string Name, bool Passed, string Detail)>();

        var casual = _router.Route("hello there");
        checks.Add(("scene/casual", casual.Mode == ConversationMode.Casual && casual.AllowDecorativeMedia, "ordinary chat keeps persona mode"));

        var technical = _router.Route("opencode configuration check");
        var technicalProfile = _router.SelectModel(technical, "opencode configuration check");
        checks.Add(("scene+model", technical.Mode == ConversationMode.Technical && !technical.AllowDecorativeMedia && technicalProfile.HasExplicitModel,
            technicalProfile.HasExplicitModel ? $"{technicalProfile.ProviderId}/{technicalProfile.ModelId}" : "default model"));

        var defaultProfile = _router.SelectModel(casual, "hello there");
        checks.Add(("model/default", !defaultProfile.HasExplicitModel, "casual conversation remains on the default model"));

        var historyOk = _history.RawContextDays == 3 &&
                        _history.MaxSummaryItems is >= 3 and <= 30 &&
                        _history.MaxSummaryCharacters is >= 400 and <= 4000;
        checks.Add(("memory/3-day", historyOk, $"raw={_history.RawContextDays}d, summary={_history.MaxSummaryItems}/{_history.MaxSummaryCharacters}"));

        var loopback = Uri.TryCreate(_openCode.BaseUrl, UriKind.Absolute, out var baseUri) && baseUri.IsLoopback;
        checks.Add(("tools/loopback", loopback, _openCode.BaseUrl));

        var toolPolicy = CheckToolPolicy();
        checks.Add(("tools/restricted", toolPolicy.Passed, toolPolicy.Detail));

        var modelPath = Path.IsPathRooted(_vision.ModelPath)
            ? _vision.ModelPath
            : Path.Combine(AppContext.BaseDirectory, _vision.ModelPath);
        checks.Add(("vision/model", _vision.Enabled && File.Exists(modelPath), Path.GetFileName(modelPath)));

        var sample = FindSampleImage();
        var inspection = sample is null ? null : _imageInspector.Inspect(sample);
        checks.Add(("vision/local", inspection is not null, inspection?.ToCompactText() ?? "no readable local image"));

        var firstMeeting = _plotKnowledge.Retrieve("秧秧还记得云灵谷的初见吗", 3);
        var plotAliasOk = firstMeeting.Corrections.Any(item => item.Canonical == "云陵谷") &&
                          firstMeeting.Events.Any(item => item.Id == "yangyang-plot-001");
        checks.Add(("plot/alias", plotAliasOk, plotAliasOk ? "云灵谷 -> 云陵谷，命中初见事件" : "别名纠正或初见检索失败"));

        var versionLetter = _plotKnowledge.Retrieve("秧秧 2.3 版本来信写了什么", 3);
        var versionLetterOk = versionLetter.Events.Any(item => item.Version == "2.3" && item.Id == "yangyang-plot-038");
        checks.Add(("plot/version-letter", versionLetterOk, versionLetterOk ? "2.3 版本来信可精确检索" : "2.3 版本来信未命中"));

        var unknownGuard = _plotKnowledge.BuildInstruction("秧秧还记得玉星湖的任务吗", 3);
        checks.Add(("plot/anti-hallucination", unknownGuard.Contains("<plot_knowledge_guard>", StringComparison.Ordinal),
            "未知剧情应进入明确的不补造分支"));

        const string policyReply = "与你同行是我的选择，但是否成为恋人或夫妻，也该由我自己确认，不能因为你这样叫我就算成立。";
        var relationshipPolicy = _personaCompliance.Evaluate(policyReply, userPrompt: "秧秧做我老婆");
        checks.Add(("persona/natural-boundary", relationshipPolicy.Reasons.Contains("用规则说明代替自然关系回应"),
            $"score={relationshipPolicy.Score}"));

        const string rewardTeasing = "先看你表现，乖一点我再考虑让你叫老婆。";
        var relationshipTeasing = _personaCompliance.Evaluate(rewardTeasing, userPrompt: "秧秧做我老婆");
        checks.Add(("persona/no-reward-teasing", relationshipTeasing.Reasons.Contains("用奖励式调侃暗示关系许可"),
            $"score={relationshipTeasing.Score}"));

        const string shutdownReply = "再说多少次，我的回答也不会改变。今天先到这吧。";
        var relationshipShutdown = _personaCompliance.Evaluate(shutdownReply, userPrompt: "秧秧做我老婆");
        checks.Add(("persona/no-conversation-shutdown", relationshipShutdown.Reasons.Contains("把重复关系请求写成训话或终止对话"),
            $"score={relationshipShutdown.Score}"));

        var gentleRepeat = _personaCompliance.Evaluate("你啊……又拿这两个字逗我。先跟上吧，别把这一路全用来说这个。", userPrompt: "秧秧做我老婆");
        checks.Add(("persona/gentle-repeat", gentleRepeat.Score == 100, $"score={gentleRepeat.Score}"));

        var passed = checks.Count(check => check.Passed);
        var lines = checks.Select(check => $"{(check.Passed ? "PASS" : "FAIL")} {check.Name}: {check.Detail}");
        return $"Intelligence self-test: {passed}/{checks.Count} passed\n" + string.Join('\n', lines);
    }

    private (bool Passed, string Detail) CheckToolPolicy()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "opencode.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("permission", out var policy))
                return (false, "missing permission policy");

            var keys = new[] { "read", "edit", "bash", "webfetch", "skill", "question" };
            var denied = keys.All(key =>
                policy.TryGetProperty(key, out var value) &&
                string.Equals(value.GetString(), "deny", StringComparison.OrdinalIgnoreCase));
            var wildcardDenied = policy.TryGetProperty("*", out var wildcard) &&
                                 string.Equals(wildcard.GetString(), "deny", StringComparison.OrdinalIgnoreCase);
            var stickerAllowed = policy.TryGetProperty("hime_sticker_search", out var sticker) &&
                                 string.Equals(sticker.GetString(), "allow", StringComparison.OrdinalIgnoreCase);
            var webSearchAllowed = policy.TryGetProperty("websearch", out var webSearch) &&
                                   string.Equals(webSearch.GetString(), "allow", StringComparison.OrdinalIgnoreCase);
            var configuredTools = _agentTools.EnabledDefinitions()
                .Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var configuredAllowed = configuredTools.All(name =>
                policy.TryGetProperty(name, out var value) &&
                string.Equals(value.GetString(), "allow", StringComparison.OrdinalIgnoreCase));
            var expectedBuiltIns = stickerAllowed && webSearchAllowed;
            var safe = denied && wildcardDenied && configuredAllowed && expectedBuiltIns;
            return (safe, safe
                ? $"dangerous tools denied; configured tools allowed: {string.Join(',', configuredTools)}"
                : "tool permission boundary is incomplete");
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name);
        }
    }

    private static string? FindSampleImage()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "resources", "images");
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(path => new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" }
                    .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            : null;
    }
}
