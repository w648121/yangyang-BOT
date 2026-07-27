using Hime.Data.Models;
using Hime.Data.Services;
using Hime.Messaging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Configurable, deterministic signals used before a social turn reaches the
/// language model. These signals choose context; they never generate a reply.
/// </summary>
public sealed class DialoguePlanningOptions
{
    public bool Enabled { get; set; } = true;

    public bool RelationshipContextOnlyWhenRelevant { get; set; } = true;

    public List<string> RelationshipContextMarkers { get; set; } = [];

    public List<string> RepairMarkers { get; set; } = [];

    public List<string> DistressMarkers { get; set; } = [];

    public List<string> QuestionMarkers { get; set; } = [];

    /// <summary>Dialogue-act policies keyed by <see cref="DialogueAct"/> name.</summary>
    public Dictionary<string, string> ActPolicies { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Trigger policies keyed by <see cref="TurnTrigger"/> name.</summary>
    public Dictionary<string, string> TriggerPolicies { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cross-cutting focus rules appended to every planned social turn.</summary>
    public List<string> CommonPolicies { get; set; } = [];

    public string RepetitionPolicy { get; set; } = string.Empty;

    public string DefaultStickerEmotion { get; set; } = string.Empty;

    public bool IsValid() =>
        RelationshipContextMarkers.Count > 0 &&
        RepairMarkers.Count > 0 &&
        DistressMarkers.Count > 0 &&
        QuestionMarkers.Count > 0 &&
        Enum.GetNames<DialogueAct>().All(name =>
            ActPolicies.TryGetValue(name, out var value) &&
            !string.IsNullOrWhiteSpace(value)) &&
        Enum.GetNames<TurnTrigger>().All(name =>
            TriggerPolicies.TryGetValue(name, out var value) &&
            !string.IsNullOrWhiteSpace(value)) &&
        CommonPolicies.Any(value => !string.IsNullOrWhiteSpace(value)) &&
        !string.IsNullOrWhiteSpace(RepetitionPolicy) &&
        !string.IsNullOrWhiteSpace(DefaultStickerEmotion);
}

public enum DialogueAct
{
    Answer,
    Continue,
    Repair,
    Support,
    Relationship,
    React
}

/// <summary>
/// One compact decision shared by explicit AI and natural group participation.
/// It describes what context is relevant without prescribing a fixed sentence.
/// </summary>
public sealed record DialogueDecision(
    DialogueAct Act,
    bool IncludeRelationshipContext,
    bool IncludePersonaState,
    bool IncludePlotKnowledge,
    bool IncludeCadenceExamples,
    bool IncludeTrustedClock,
    string Reason);

public sealed record SocialTurnRequest(
    TurnContext Turn,
    long SourceMessageId,
    string? GroupName,
    string UserPrompt,
    string ModelPrompt,
    HimeStyleScene Scene,
    int RequestedStickerCount = 0,
    string? RequestedStickerEmotion = null,
    bool RequireEmotionMarker = false,
    int? CorpusMaximum = null,
    int? PlotMaximum = null,
    IReadOnlyList<string>? RequestedStickerEmotions = null,
    ConversationFocusDecision? Focus = null);

public sealed record SocialTurnPlan(
    ConversationRoute Route,
    DialogueDecision Decision,
    ConversationFocusDecision Focus,
    YangyangInteractionPlan Interaction,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<string> RecentAssistantReplies,
    EmotionalPragmaticsPlan EmotionalPragmatics,
    SocialIntentResult SocialIntent,
    AiRequestProfile RequestProfile);

/// <summary>
/// Shared planning and context boundary for every user-triggered social reply.
/// It performs no network call, so using it does not add model latency.
/// </summary>
public sealed class SocialTurnCoordinator(
    ConversationRouter router,
    IRelationshipTrajectoryService relationshipTrajectory,
    IPersonaStateService personaStates,
    ConversationContextAssembler contextAssembler,
    ConversationStyleService conversationStyle,
    SocialTurnStrategyService socialStrategyService,
    GroupSceneAwarenessService groupSceneAwareness,
    GroupChatInvestigatorService groupChatInvestigator,
    EmotionalPragmaticsPlanner emotionalPragmatics,
    PersonaRuntimeProfileService runtimeProfile,
    PersonaCorpusService personaCorpus,
    PersonaPlotKnowledgeService plotKnowledge,
    IOptions<DialoguePlanningOptions> options)
{
    private readonly DialoguePlanningOptions _options = options.Value;

    public SocialTurnPlan Build(SocialTurnRequest request)
    {
        var turn = request.Turn;
        var focus = request.UserPrompt?.Trim() ?? string.Empty;
        var modelPrompt = string.IsNullOrWhiteSpace(request.ModelPrompt)
            ? focus
            : request.ModelPrompt.Trim();

        if (turn.UserId > 0)
        {
            personaStates.ObserveConversation(
                turn.UserId,
                turn.Nickname,
                turn.GroupId,
                request.GroupName);
            if (turn.Trigger == TurnTrigger.ExplicitAi)
                personaStates.CaptureExplicitFacts(turn.UserId, turn.GroupId, focus);
        }

        var interaction = relationshipTrajectory.BuildPlan(
            request.SourceMessageId,
            turn.UserId,
            turn.Nickname,
            turn.GroupId,
            request.GroupName,
            focus);
        var route = router.Route(focus);
        var emotionalPlan = emotionalPragmatics.Plan(focus, request.Scene);
        var conversationFocus = request.Focus ?? DefaultFocus(turn);
        var decision = Decide(
            turn.Trigger,
            route,
            focus,
            emotionalPlan,
            conversationFocus);
        var socialStrategy = socialStrategyService.Build(
            request,
            route,
            decision,
            conversationFocus,
            interaction);
        var assembled = contextAssembler.Build(
            turn.UserId,
            turn.Nickname,
            turn.GroupId,
            focus,
            conversationFocus.TopicId,
            conversationFocus.ConversationParticipants);

        var messages = new List<ChatMessage>(assembled.Messages.Count + 10);
        if (decision.IncludeRelationshipContext &&
            !string.IsNullOrWhiteSpace(interaction.PromptContext))
        {
            AddSystem(messages, interaction.PromptContext, turn.GroupId);
        }

        if (decision.IncludePersonaState)
        {
            var personaState = personaStates.BuildPromptContext(
                turn.UserId,
                turn.Nickname,
                turn.GroupId,
                request.GroupName,
                focus);
            AddSystem(messages, personaState, turn.GroupId);
        }

        if (decision.IncludeTrustedClock)
            AddSystem(messages, BuildBeijingTimeContext(), turn.GroupId);

        AddSystem(messages, router.BuildSystemPolicy(route), turn.GroupId);
        AddSystem(
            messages,
            groupSceneAwareness.BuildPromptContext(
                turn.GroupId,
                conversationFocus.TopicId,
                conversationFocus.ConversationParticipants),
            turn.GroupId);
        AddSystem(
            messages,
            groupChatInvestigator.BuildPromptContext(
                turn.GroupId,
                request.SourceMessageId,
                focus,
                conversationFocus),
            turn.GroupId);
        AddSystem(messages, socialStrategy.PromptContext, turn.GroupId);
        AddSystem(messages, BuildTurnPolicy(request, decision, conversationFocus), turn.GroupId);

        var style = conversationStyle.BuildInstruction(route, request.Scene, focus);
        AddSystem(messages, style, turn.GroupId);

        if (decision.IncludePlotKnowledge)
            AddSystem(messages, plotKnowledge.BuildInstruction(focus, request.PlotMaximum), turn.GroupId);

        if (decision.IncludeCadenceExamples)
            AddSystem(messages, personaCorpus.BuildInstruction(focus, request.CorpusMaximum), turn.GroupId);

        // Keep the pragmatic plan after optional style/corpus sections so examples
        // cannot reintroduce advice-first wording or an invented comforting scene.
        AddSystem(messages, emotionalPlan.Instruction, turn.GroupId);

        if (interaction.RepeatedCurrentMessageCount > 1 &&
            !decision.IncludeRelationshipContext)
        {
            AddSystem(messages, _options.RepetitionPolicy, turn.GroupId);
        }

        AddSystem(messages, BuildMediaInstruction(request, route), turn.GroupId);
        messages.AddRange(assembled.Messages);
        AddSystem(
            messages,
            runtimeProfile.BuildFinalInstruction(
                SceneName(request.Scene),
                route.AllowDecorativeMedia,
                request.RequestedStickerCount > 0
                    ? request.RequestedStickerCount
                    : request.RequireEmotionMarker
                        ? 1
                        : null),
            turn.GroupId);
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = modelPrompt,
            UserId = turn.UserId,
            Nickname = turn.Nickname,
            GroupId = turn.GroupId,
            ImagePaths = turn.UserImagePaths.ToList(),
            TurnId = turn.TurnId,
            Source = turn.Trigger.ToString(),
            AccountId = turn.AccountId,
            PlatformMessageId = turn.SourceMessageId,
            Time = DateTime.UtcNow
        });

        var requestProfile = router.SelectModel(route, focus);
        if (decision.Act == DialogueAct.Relationship || emotionalPlan.IsActive)
            requestProfile = requestProfile with { PreferDirect = true };

        return new SocialTurnPlan(
            route,
            decision,
            conversationFocus,
            interaction,
            messages,
            assembled.RecentAssistantReplies,
            emotionalPlan,
            socialStrategy.Intent,
            requestProfile);
    }

    public void ApplyMemoryProposals(
        long evidenceMessageId,
        long userId,
        long? groupId,
        IReadOnlyCollection<PersonaMemoryProposal> proposals)
    {
        relationshipTrajectory.RecordInferenceProposals(
            evidenceMessageId,
            userId,
            groupId,
            proposals);
        personaStates.ApplyMemoryProposals(userId, groupId, proposals);
    }

    private DialogueDecision Decide(
        TurnTrigger trigger,
        ConversationRoute route,
        string focus,
        EmotionalPragmaticsPlan emotionalPlan,
        ConversationFocusDecision conversationFocus)
    {
        if (!_options.Enabled)
        {
            return new DialogueDecision(
                trigger == TurnTrigger.Reactive ? DialogueAct.React : DialogueAct.Answer,
                IncludeRelationshipContext: true,
                IncludePersonaState: true,
                IncludePlotKnowledge: true,
                IncludeCadenceExamples: route.Mode == ConversationMode.Casual,
                IncludeTrustedClock: true,
                "兼容模式");
        }

        var relationship = route.Mode == ConversationMode.Casual &&
                           ContainsAny(focus, _options.RelationshipContextMarkers);
        var repair = ContainsAny(focus, _options.RepairMarkers);
        var distress = route.Mode == ConversationMode.Casual &&
                       ContainsAny(focus, _options.DistressMarkers);
        var act = conversationFocus.ReplyMode == FocusReplyMode.Clarify
            ? DialogueAct.Repair
            : route.Mode is ConversationMode.Factual or ConversationMode.Technical
            ? DialogueAct.Answer
            : repair
                ? DialogueAct.Repair
                : relationship
                    ? DialogueAct.Relationship
                    : distress || emotionalPlan.NeedsSupport
                        ? DialogueAct.Support
                        : conversationFocus.ReplyMode == FocusReplyMode.React ||
                          trigger == TurnTrigger.Reactive
                            ? DialogueAct.React
                            : LooksLikeQuestion(focus)
                                ? DialogueAct.Answer
                                : DialogueAct.Continue;

        return new DialogueDecision(
            act,
            IncludeRelationshipContext:
                route.Mode == ConversationMode.Casual &&
                (!_options.RelationshipContextOnlyWhenRelevant || relationship),
            IncludePersonaState: route.Mode == ConversationMode.Casual,
            IncludePlotKnowledge: route.Mode == ConversationMode.Casual && !emotionalPlan.IsActive,
            IncludeCadenceExamples: route.Mode == ConversationMode.Casual && !emotionalPlan.IsActive,
            IncludeTrustedClock: route.Mode == ConversationMode.Factual,
            $"route={route.Mode}; trigger={trigger}; act={act}");
    }

    private string BuildTurnPolicy(
        SocialTurnRequest request,
        DialogueDecision decision,
        ConversationFocusDecision focus)
    {
        var actName = decision.Act.ToString();
        var continuity = _options.ActPolicies.TryGetValue(actName, out var actPolicy)
            ? actPolicy.Trim()
            : throw new InvalidOperationException($"DialoguePlanning:ActPolicies:{actName} is required.");
        var triggerName = request.Turn.Trigger.ToString();
        var trigger = _options.TriggerPolicies.TryGetValue(triggerName, out var triggerPolicy)
            ? triggerPolicy.Trim()
            : throw new InvalidOperationException($"DialoguePlanning:TriggerPolicies:{triggerName} is required.");
        var common = string.Join(
            Environment.NewLine,
            _options.CommonPolicies
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()));
        return $"""
            <dialogue_decision act="{decision.Act}">
            Audience target: {focus.Target}; reply mode: {focus.ReplyMode}; topic: {focus.TopicId}.
            {common}
            {continuity}
            {trigger}
            </dialogue_decision>
            """;
    }

    private static ConversationFocusDecision DefaultFocus(TurnContext turn) =>
        new(
            ConversationTargetKind.Bot,
            null,
            string.IsNullOrWhiteSpace(turn.TopicId) ? turn.ScopeKey : turn.TopicId,
            turn.ConversationParticipants,
            1,
            1,
            turn.Trigger == TurnTrigger.Reactive
                ? FocusReplyMode.React
                : FocusReplyMode.Answer,
            1,
            "explicit-or-proactive-turn",
            UsedSemanticFallback: false);

    private string BuildMediaInstruction(
        SocialTurnRequest request,
        ConversationRoute route)
    {
        if (!route.AllowDecorativeMedia)
            return string.Empty;

        if (request.RequestedStickerCount > 0)
        {
            var emotions = (request.RequestedStickerEmotions ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
            if (emotions.Count == 0)
            {
                emotions.Add(string.IsNullOrWhiteSpace(request.RequestedStickerEmotion)
                    ? _options.DefaultStickerEmotion
                    : request.RequestedStickerEmotion.Trim());
            }
            var compoundRule = emotions.Count > 1
                ? $"The user explicitly combined these emotions: {string.Join(", ", emotions)}. Pass every one as an independent hime_sticker_search emotion weight; do not collapse them into only the strongest emotion. If no combined match is strong enough, prefer the tool's calm/neutral fallback."
                : $"The requested base emotion is {emotions[0]}.";
            return $"""
                The user explicitly requested {request.RequestedStickerCount} stickers.
                Reply naturally, then end with exactly {request.RequestedStickerCount} consecutive
                [sticker:tag], [emotion:label], or approved [sticker-id:...] markers.
                Do not explain the marker protocol or claim the feature is unavailable.
                {compoundRule}
                """;
        }

        return request.RequireEmotionMarker
            ? """
              End with exactly one supported [emotion:...], [sticker:...], or approved
              [sticker-id:...] marker. The application removes the marker before sending.
              """
            : string.Empty;
    }

    private static string SceneName(HimeStyleScene scene) => scene switch
    {
        HimeStyleScene.PrivateReply => "私聊回复",
        HimeStyleScene.ProactiveGroupPost => "主动群聊发言",
        _ => "普通群聊回复"
    };

    private static void AddSystem(
        ICollection<ChatMessage> messages,
        string? content,
        long? groupId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;
        messages.Add(new ChatMessage
        {
            Role = "system",
            Content = content.Trim(),
            GroupId = groupId,
            Time = DateTime.UtcNow
        });
    }

    private static bool ContainsAny(string text, IEnumerable<string> markers) =>
        markers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Any(marker => text.Contains(marker.Trim(), StringComparison.OrdinalIgnoreCase));

    private bool LooksLikeQuestion(string text) =>
        text.Contains('？') ||
        text.Contains('?') ||
        ContainsAny(text, _options.QuestionMarkers);

    private static string BuildBeijingTimeContext()
    {
        TimeZoneInfo beijingZone;
        try
        {
            beijingZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            beijingZone = TimeZoneInfo.CreateCustomTimeZone(
                "Beijing",
                TimeSpan.FromHours(8),
                "Beijing Time",
                "Beijing Time");
        }

        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, beijingZone);
        return $"""
            Trusted runtime clock: the current Beijing Time (China Standard Time, UTC+8)
            is {now:yyyy-MM-dd HH:mm:ss}. Answer time and date questions from this value,
            not from role-play or stored conversation.
            """;
    }
}
