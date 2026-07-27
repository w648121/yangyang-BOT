using Hime.Data.Models;
using Hime.Data.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

public enum ReplyCandidateJudgeUsage
{
    ExplicitAi,
    ReactiveConversation,
    Proactive
}

public sealed record ReplyCandidateChoice(
    string Reply,
    bool Replaced,
    IReadOnlyList<ReplyCandidateEvaluation> Candidates);

public sealed record ReplyCandidateEvaluation(
    string Reply,
    double Score,
    int ComplianceScore,
    IReadOnlyList<string> Reasons,
    string Source);

/// <summary>
/// Selects the best visible reply from one or more candidates. The judge is a
/// bounded post-generation step: C# owns scoring and routing, while intent
/// vocabulary, bad habits and examples remain config/database driven.
/// </summary>
public sealed class ReplyCandidateJudgeService(
    IAiClient ai,
    PersonaComplianceService compliance,
    ReplyLearningService learning,
    PersonaRuntimeProfileService runtime,
    IOptionsMonitor<SocialIntelligenceOptions> options,
    ILogger<ReplyCandidateJudgeService> logger)
{
    public async Task<ReplyCandidateChoice> SelectBestAsync(
        string? primaryReply,
        SocialTurnPlan plan,
        string userPrompt,
        long senderId,
        HimeStyleScene scene,
        bool casual,
        bool requireEmotionMarker,
        ReplyCandidateJudgeUsage usage,
        CancellationToken cancellationToken = default)
    {
        var current = options.CurrentValue;
        var judge = current.CandidateJudge;
        var cleanedPrimary = VisibleReplyTextSanitizer.Clean(primaryReply?.Trim());
        if (!current.Enabled || !judge.Enabled || string.IsNullOrWhiteSpace(cleanedPrimary))
        {
            return new ReplyCandidateChoice(
                cleanedPrimary,
                Replaced: false,
                [Evaluate(cleanedPrimary, "primary", plan, userPrompt, scene, casual, requireEmotionMarker)]);
        }

        var candidates = new List<ReplyCandidateEvaluation>
        {
            Evaluate(cleanedPrimary, "primary", plan, userPrompt, scene, casual, requireEmotionMarker)
        };
        var primary = candidates[0];
        var canGenerateAlternatives = ShouldGenerateAlternatives(judge, usage, plan, primary);
        if (canGenerateAlternatives)
        {
            foreach (var reply in await GenerateAlternativesAsync(
                         plan,
                         senderId,
                         Math.Clamp(judge.AlternativeCount, 0, 3),
                         candidates.Select(candidate => candidate.Reply).ToArray(),
                         cancellationToken))
            {
                var clean = VisibleReplyTextSanitizer.Clean(reply?.Trim());
                if (string.IsNullOrWhiteSpace(clean))
                    continue;
                if (candidates.Any(candidate =>
                        ConversationTopicGraph.Similarity(candidate.Reply, clean) >= 0.92d))
                    continue;

                candidates.Add(Evaluate(clean, "alternative", plan, userPrompt, scene, casual, requireEmotionMarker));
            }
        }

        var best = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.ComplianceScore)
            .First();
        var replaced = best.Source != "primary" &&
                       best.Score >= primary.Score + judge.MinimumImprovementToReplace;
        if (replaced)
        {
            logger.LogInformation(
                "Reply candidate judge selected an alternative (Act={Act}, Primary={PrimaryScore:0.0}, Best={BestScore:0.0}).",
                plan.Decision.Act,
                primary.Score,
                best.Score);
        }

        return new ReplyCandidateChoice(
            replaced ? best.Reply : cleanedPrimary,
            replaced,
            candidates);
    }

    private ReplyCandidateEvaluation Evaluate(
        string reply,
        string source,
        SocialTurnPlan plan,
        string userPrompt,
        HimeStyleScene scene,
        bool casual,
        bool requireEmotionMarker)
    {
        var judge = options.CurrentValue.CandidateJudge;
        var assessment = compliance.Evaluate(
            reply,
            casual,
            requireEmotionMarker,
            plan.RecentAssistantReplies,
            userPrompt);
        var reasons = new List<string>(assessment.Reasons);
        var score = (double)assessment.Score;

        var visibleLength = VisibleReplyTextSanitizer.Clean(reply).Length;
        if (visibleLength > judge.MaxCandidateCharacters)
        {
            var overflow = visibleLength - judge.MaxCandidateCharacters;
            score -= Math.Min(24, overflow / 16d);
            reasons.Add("超过候选长度上限");
        }

        var sceneKey = SceneKey(scene);
        var personaId = runtime.Current.ProfileId;
        var goodExamples = learning.FindRelevant(
            personaId,
            plan.SocialIntent.IntentId,
            sceneKey,
            userPrompt,
            "good",
            options.CurrentValue.MaxGoodExamplesPerPrompt);
        var badExamples = learning.FindRelevant(
            personaId,
            plan.SocialIntent.IntentId,
            sceneKey,
            userPrompt,
            "bad",
            options.CurrentValue.MaxBadExamplesPerPrompt);
        var goodSimilarity = MaxExampleSimilarity(reply, goodExamples);
        var badSimilarity = MaxExampleSimilarity(reply, badExamples);
        if (goodSimilarity >= judge.ExampleSimilarityThreshold)
        {
            score += goodSimilarity * judge.GoodExampleBonus;
            reasons.Add("接近人工好样例结构");
        }
        if (badSimilarity >= judge.ExampleSimilarityThreshold)
        {
            score -= badSimilarity * judge.BadExamplePenalty;
            reasons.Add("接近人工坏样例模式");
        }

        return new ReplyCandidateEvaluation(
            reply,
            Math.Clamp(score, 0, 120),
            assessment.Score,
            reasons,
            source);
    }

    private async Task<IReadOnlyList<string>> GenerateAlternativesAsync(
        SocialTurnPlan plan,
        long senderId,
        int count,
        IReadOnlyList<string> existingCandidates,
        CancellationToken cancellationToken)
    {
        if (count <= 0)
            return [];

        var results = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            try
            {
                var messages = BuildAlternativeMessages(plan, existingCandidates.Concat(results));
                var alternative = await ai.ChatAsync(
                    messages,
                    senderId,
                    cancellationToken,
                    applyBoundPersona: true,
                    requestProfile: plan.RequestProfile with { PreferDirect = true });
                if (!string.IsNullOrWhiteSpace(alternative))
                    results.Add(alternative);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reply candidate alternative generation failed.");
                break;
            }
        }

        return results;
    }

    private IReadOnlyList<ChatMessage> BuildAlternativeMessages(
        SocialTurnPlan plan,
        IEnumerable<string> existingCandidates)
    {
        var prompt = options.CurrentValue.CandidateJudge.AlternativePrompt.Trim();
        var existing = existingCandidates
            .Select(candidate => VisibleReplyTextSanitizer.Clean(candidate).Trim())
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();

        var messages = new List<ChatMessage>(plan.Messages.Count + 1);
        messages.AddRange(plan.Messages);
        messages.Add(new ChatMessage
        {
            Role = "system",
            Content = $"""
                <reply_candidate_generation>
                {prompt}
                social_intent={plan.SocialIntent.IntentId}; dialogue_act={plan.Decision.Act}
                existing_candidates_to_avoid:
                {string.Join("\n", existing.Select((candidate, index) => $"{index + 1}. {Trim(candidate, 220)}"))}
                </reply_candidate_generation>
                """,
            Time = DateTime.UtcNow
        });
        return messages;
    }

    private bool ShouldGenerateAlternatives(
        ReplyCandidateJudgeOptions judge,
        ReplyCandidateJudgeUsage usage,
        SocialTurnPlan plan,
        ReplyCandidateEvaluation primary)
    {
        if (!judge.GenerateAlternatives || judge.AlternativeCount <= 0)
            return false;
        if (primary.ComplianceScore >= judge.AcceptFirstAtOrAboveScore &&
            !AlwaysGenerateForAct(judge, plan.Decision.Act))
            return false;

        return usage switch
        {
            ReplyCandidateJudgeUsage.ExplicitAi => judge.UseForExplicitAi &&
                                                   (primary.ComplianceScore < judge.GenerateAlternativesBelowScore ||
                                                    AlwaysGenerateForAct(judge, plan.Decision.Act)),
            ReplyCandidateJudgeUsage.ReactiveConversation => judge.UseForReactiveConversation &&
                                                             (primary.ComplianceScore < judge.GenerateAlternativesBelowScore ||
                                                              AlwaysGenerateForAct(judge, plan.Decision.Act)),
            _ => false
        };
    }

    private static bool AlwaysGenerateForAct(
        ReplyCandidateJudgeOptions judge,
        DialogueAct act) =>
        judge.AlwaysGenerateForDialogueActs.Any(value =>
            act.ToString().Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static double MaxExampleSimilarity(
        string reply,
        IReadOnlyList<ReplyLearningExampleMatch> examples) =>
        examples.Count == 0
            ? 0
            : examples.Max(example => ConversationTopicGraph.Similarity(reply, example.Record.BotReply));

    private static string SceneKey(HimeStyleScene scene) => scene switch
    {
        HimeStyleScene.PrivateReply => "private_reply",
        HimeStyleScene.ProactiveGroupPost => "proactive_group_post",
        _ => "group_reply"
    };

    private static string Trim(string? value, int maximum)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum] + "…";
    }
}
