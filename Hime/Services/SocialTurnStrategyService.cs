using System.Text;
using Hime.Data.Models;
using Hime.Data.Services;
using Microsoft.Extensions.Options;

namespace Hime.Services;

public sealed record SocialTurnStrategy(
    SocialIntentResult Intent,
    string PromptContext);

/// <summary>
/// Builds the dynamic social brief placed before generation. The brief combines
/// intent classification, current route, relationship continuity and human-rated
/// examples. It teaches structure and constraints without prescribing a canned line.
/// </summary>
public sealed class SocialTurnStrategyService(
    SocialIntentAnalyzer intents,
    ReplyLearningService learning,
    PersonaRuntimeProfileService runtime,
    IOptionsMonitor<SocialIntelligenceOptions> options)
{
    public SocialTurnStrategy Build(
        SocialTurnRequest request,
        ConversationRoute route,
        DialogueDecision decision,
        ConversationFocusDecision focus,
        YangyangInteractionPlan interaction)
    {
        var current = options.CurrentValue;
        var intent = intents.Analyze(request.UserPrompt, decision, route);
        if (!current.Enabled)
            return new SocialTurnStrategy(intent, string.Empty);

        var personaId = runtime.Current.ProfileId;
        var scene = SceneName(request.Scene);
        var goodExamples = learning.FindRelevant(
            personaId,
            intent.IntentId,
            scene,
            request.UserPrompt,
            "good",
            Math.Clamp(current.MaxGoodExamplesPerPrompt, 0, 8));
        var badExamples = learning.FindRelevant(
            personaId,
            intent.IntentId,
            scene,
            request.UserPrompt,
            "bad",
            Math.Clamp(current.MaxBadExamplesPerPrompt, 0, 8));

        var builder = new StringBuilder();
        builder.AppendLine("<dynamic_social_turn_strategy>");
        builder.AppendLine("This section is planning context only. Do not quote it, do not output JSON, and do not expose internal labels.");
        builder.AppendLine($"persona={personaId}; scene={scene}; route={route.Mode}; dialogue_act={decision.Act}; focus_target={focus.Target}; focus_reply_mode={focus.ReplyMode}");
        builder.AppendLine($"social_intent={intent.IntentId}; confidence={intent.Confidence:0.00}; description={Escape(intent.Description)}");
        if (intent.MatchedMarkers.Count > 0)
            builder.AppendLine($"matched_user_cues={string.Join(", ", intent.MatchedMarkers.Select(Escape))}");
        builder.AppendLine($"yangyang_posture={Escape(intent.Posture)}");
        builder.AppendLine($"reply_goal={Escape(intent.ReplyGoal)}");
        if (!string.IsNullOrWhiteSpace(intent.SocialAction))
            builder.AppendLine($"social_action={Escape(intent.SocialAction)}");
        if (!string.IsNullOrWhiteSpace(intent.StickerIntent))
            builder.AppendLine($"media_intent={Escape(intent.StickerIntent)}");
        builder.AppendLine($"verified_repeated_current_message_count={interaction.RepeatedCurrentMessageCount}");

        AppendRules(builder, "common_rules", current.CommonStrategyRules);
        AppendRules(builder, "persona_boundaries", current.PersonaBoundaryRules);
        AppendRules(
            builder,
            "avoid_this_turn",
            current.NaturalnessAvoidRules
                .Concat(intent.Avoid)
                .Distinct(StringComparer.OrdinalIgnoreCase));

        AppendExamples(builder, "good_reply_examples", goodExamples, "Reference their social structure and rhythm only; never copy names, facts, images, or private claims.");
        AppendExamples(builder, "bad_reply_examples", badExamples, "Avoid these failure patterns and reasons; do not paraphrase them.");

        builder.AppendLine("Before answering, silently choose one concrete conversational move: answer, tease back softly, clarify, repair, comfort, refuse gently, or stay brief. Then write only the final visible reply.");
        builder.AppendLine("</dynamic_social_turn_strategy>");

        return new SocialTurnStrategy(intent, builder.ToString());
    }

    private static void AppendRules(
        StringBuilder builder,
        string tag,
        IEnumerable<string> rules)
    {
        var values = rules
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length == 0)
            return;

        builder.AppendLine($"<{tag}>");
        foreach (var value in values)
            builder.AppendLine($"- {Escape(value)}");
        builder.AppendLine($"</{tag}>");
    }

    private static void AppendExamples(
        StringBuilder builder,
        string tag,
        IReadOnlyList<ReplyLearningExampleMatch> examples,
        string note)
    {
        if (examples.Count == 0)
            return;

        builder.AppendLine($"<{tag} note=\"{Escape(note)}\">");
        foreach (var example in examples)
        {
            var item = example.Record;
            builder.AppendLine($"- score={example.Score:0.00}; intent={Escape(item.IntentId)}; reason={Escape(item.Reason)}");
            if (!string.IsNullOrWhiteSpace(item.UserMessage))
                builder.AppendLine($"  user: {Escape(Trim(item.UserMessage, 220))}");
            builder.AppendLine($"  reply: {Escape(Trim(item.BotReply, 260))}");
        }
        builder.AppendLine($"</{tag}>");
    }

    private static string SceneName(HimeStyleScene scene) => scene switch
    {
        HimeStyleScene.PrivateReply => "private_reply",
        HimeStyleScene.ProactiveGroupPost => "proactive_group_post",
        _ => "group_reply"
    };

    private static string Escape(string? value) =>
        (value ?? string.Empty)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Trim();

    private static string Trim(string? value, int maximum)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum] + "…";
    }
}
