using System.Security.Cryptography;
using System.Text;
using Hime.Data.Models;
using Hime.Services;
using LiteDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Data.Services;

/// <summary>
/// Evidence-only relationship trajectory. Each schema owns a separate LiteDB file and
/// never reads legacy chat, persona-state, or summary collections.
/// </summary>
public sealed class RelationshipTrajectoryService : IRelationshipTrajectoryService, IDisposable
{
    private static readonly HashSet<string> AllowedInferenceKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "preference", "relationship", "address", "style", "boundary", "shared", "promise", "topic"
    };

    private readonly RelationshipTrajectoryOptions _options;
    private readonly ILogger<RelationshipTrajectoryService> _logger;
    private readonly LiteDatabase _database;
    private readonly string _schema;
    private readonly object _sync = new();
    private bool _disposed;

    public RelationshipTrajectoryService(
        IOptions<RelationshipTrajectoryOptions> options,
        ILogger<RelationshipTrajectoryService> logger)
    {
        _options = options.Value;
        _logger = logger;
        AcceptEventsAfterUtc = (_options.AcceptEventsAfterUtc ?? DateTimeOffset.UtcNow).UtcDateTime;
        _schema = NormalizeToken(_options.SchemaVersion, "v3");

        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data", $"relationship-{_schema}");
        Directory.CreateDirectory(dataDirectory);
        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(_options.DatabaseFileName)
            ? $"relationship-{_schema}.db"
            : _options.DatabaseFileName.Trim());
        _database = new LiteDatabase($"Filename={Path.Combine(dataDirectory, fileName)};Connection=shared");

        Events.EnsureIndex(record => record.TrackId);
        Events.EnsureIndex(record => record.GlobalUserTrackId);
        Events.EnsureIndex(record => record.GroupId);
        Events.EnsureIndex(record => record.OccurredAtUtc);
        Inferences.EnsureIndex(record => record.TrackId);
        SocialEdges.EnsureIndex(record => record.GroupId);

        if (_options.ImportLegacyData)
        {
            _logger.LogWarning(
                "RelationshipTrajectory:ImportLegacyData=true was ignored. Relationship schema {Schema} never imports legacy data.",
                _schema);
        }
    }

    public DateTime AcceptEventsAfterUtc { get; }

    private ILiteCollection<RelationshipEventRecord> Events =>
        _database.GetCollection<RelationshipEventRecord>($"relationship_events_{_schema}");

    private ILiteCollection<RelationshipInferenceRecord> Inferences =>
        _database.GetCollection<RelationshipInferenceRecord>($"relationship_inferences_{_schema}");

    private ILiteCollection<GroupSocialEdgeRecord> SocialEdges =>
        _database.GetCollection<GroupSocialEdgeRecord>($"group_social_edges_{_schema}");

    public bool Accepts(DateTime utc) =>
        _options.Enabled && utc.ToUniversalTime() >= AcceptEventsAfterUtc;

    public string RecordUserMessage(
        long messageId,
        long userId,
        string nickname,
        long? groupId,
        string content,
        IReadOnlyList<string>? imagePaths = null,
        IReadOnlyList<long>? explicitlyAddressedUserIds = null,
        string platform = "qq")
    {
        if (!_options.Enabled || userId <= 0)
            return string.Empty;

        var now = DateTime.UtcNow;
        if (!Accepts(now))
            return string.Empty;

        var schema = NormalizeToken(_options.SchemaVersion, "v3");
        var sceneTrack = BuildSceneTrackId(schema, platform, userId, groupId);
        var globalTrack = BuildGlobalTrackId(schema, platform, userId);
        var eventId = $"{schema}:in:{NormalizeToken(platform, "qq")}:{(groupId.HasValue ? $"g:{groupId}" : $"p:{userId}")}:{messageId}";
        var record = new RelationshipEventRecord
        {
            Id = eventId,
            SchemaVersion = schema,
            TrackId = sceneTrack,
            GlobalUserTrackId = globalTrack,
            ScopeKey = groupId.HasValue ? $"qq:group:{groupId}" : $"qq:private:{userId}",
            Platform = NormalizeToken(platform, "qq"),
            MessageId = messageId,
            UserId = userId,
            GroupId = groupId,
            Actor = "user",
            Kind = ClassifyUserEvent(content),
            Nickname = Trim(nickname, 80),
            Content = Trim(content, 1200),
            ImagePaths = (imagePaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Take(8).ToList(),
            IsVerified = true,
            Source = "qq-event",
            OccurredAtUtc = now
        };

        lock (_sync)
        {
            if (Events.FindById(eventId) is null)
                Events.Insert(record);

            if (groupId.HasValue)
            {
                foreach (var targetId in (explicitlyAddressedUserIds ?? [])
                             .Where(target => target > 0 && target != userId)
                             .Distinct())
                {
                    UpdateSocialEdge(schema, groupId.Value, userId, targetId, eventId, now);
                }
            }
        }

        return eventId;
    }

    public YangyangInteractionPlan BuildPlan(
        long messageId,
        long userId,
        string nickname,
        long? groupId,
        string? groupName,
        string currentText)
    {
        var currentEventId = RecordUserMessage(messageId, userId, nickname, groupId, currentText);
        var schema = NormalizeToken(_options.SchemaVersion, "v3");
        var sceneTrack = BuildSceneTrackId(schema, "qq", userId, groupId);
        var globalTrack = BuildGlobalTrackId(schema, "qq", userId);
        var cutoff = LaterOf(AcceptEventsAfterUtc, DateTime.UtcNow.AddDays(-Math.Clamp(_options.RawEventDays, 1, 30)));
        List<RelationshipEventRecord> sceneEvents;
        List<RelationshipInferenceRecord> inferences;
        int globalInteractionCount;

        lock (_sync)
        {
            sceneEvents = Events.Find(record => record.TrackId == sceneTrack && record.OccurredAtUtc >= cutoff)
                .OrderBy(record => record.OccurredAtUtc)
                .TakeLast(Math.Clamp(_options.MaxRecentEventsInPrompt, 4, 40))
                .ToList();
            inferences = Inferences.Find(record =>
                    (record.TrackId == globalTrack || record.TrackId == sceneTrack) &&
                    (record.ExpiresAtUtc == null || record.ExpiresAtUtc > DateTime.UtcNow))
                .OrderByDescending(record => record.LastSupportedAtUtc)
                .Take(Math.Clamp(_options.MaxInferenceItemsInPrompt, 0, 12))
                .ToList();
            globalInteractionCount = Events.Count(record => record.GlobalUserTrackId == globalTrack);
        }

        var repeatedCount = CountRepeatedUserMessage(sceneEvents, currentText);
        var impulses = BuildResponseImpulses(currentText, repeatedCount, globalInteractionCount);
        var evidenceIds = sceneEvents.Select(record => record.Id).ToArray();
        var stage = NormalizeToken(_options.ActivePersonaStage, "xuanling");
        var role = NormalizeToken(_options.DefaultCounterpartRole, "rover");
        var prompt = BuildPlanPrompt(
            sceneTrack,
            stage,
            role,
            userId,
            nickname,
            groupId,
            groupName,
            globalInteractionCount,
            repeatedCount,
            sceneEvents,
            inferences,
            impulses);

        return new YangyangInteractionPlan(
            Guid.NewGuid().ToString("N"),
            sceneTrack,
            currentEventId,
            stage,
            role,
            evidenceIds,
            repeatedCount,
            impulses,
            prompt);
    }

    public string BuildGroupContext(
        long groupId,
        string? groupName,
        IReadOnlyCollection<long>? activeMemberIds = null)
    {
        if (!_options.Enabled || groupId <= 0)
            return string.Empty;

        var cutoff = LaterOf(AcceptEventsAfterUtc, DateTime.UtcNow.AddDays(-Math.Clamp(_options.RawEventDays, 1, 30)));
        List<RelationshipEventRecord> events;
        List<GroupSocialEdgeRecord> edges;
        lock (_sync)
        {
            events = Events.Find(record => record.GroupId == groupId && record.OccurredAtUtc >= cutoff)
                .OrderBy(record => record.OccurredAtUtc)
                .TakeLast(Math.Clamp(_options.MaxRecentEventsInPrompt, 4, 40))
                .ToList();
            edges = SocialEdges.Find(edge => edge.GroupId == groupId)
                .OrderByDescending(edge => edge.LastObservedAtUtc)
                .Take(8)
                .ToList();
        }

        var builder = new StringBuilder();
        builder.AppendLine($"【{_schema} 群场景证据】");
        builder.AppendLine($"当前群：{Safe(groupName, 100, groupId.ToString())}({groupId})。这里只包含新方案启用后的本群事件；其他群和私聊原文不可见。");
        builder.AppendLine("所有直接交流者在角色层共享“漂泊者”基线，但每个 QQ 与秧秧的实际关系轨迹彼此独立。");
        foreach (var item in events)
        {
            var actor = item.Actor == "assistant"
                ? "秧秧的历史生成文本（只作对话衔接，不作事实）"
                : $"{Safe(item.Nickname, 60, item.UserId.ToString())}({item.UserId})";
            var content = item.Actor == "assistant"
                ? VisibleReplyTextSanitizer.Clean(item.Content)
                : item.Content;
            builder.AppendLine($"- [{item.OccurredAtUtc.ToLocalTime():MM-dd HH:mm}] {actor}：{Safe(content, 260, "[图片或无文字消息]")}");
        }

        if (edges.Count > 0)
        {
            builder.AppendLine("本群用户关系证据（仅显式 @，不代表亲密或好恶）：");
            foreach (var edge in edges)
                builder.AppendLine($"- {edge.FromUserId} 曾明确指向 {edge.ToUserId} 发言 {edge.ExplicitInteractionCount} 次。");
        }

        builder.AppendLine("不得把一个群的用户关系、原话或气氛迁移到另一个群；不得把私聊原话复述到群里。");
        return Trim(builder.ToString(), Math.Clamp(_options.MaxEvidenceCharacters, 800, 6000));
    }

    public void RecordAssistantReply(
        long replyToMessageId,
        long userId,
        long? groupId,
        string content,
        string? emotion,
        string source)
    {
        if (!_options.Enabled || !Accepts(DateTime.UtcNow))
            return;

        content = VisibleReplyTextSanitizer.Clean(content);
        if (string.IsNullOrWhiteSpace(content))
            return;

        var schema = NormalizeToken(_options.SchemaVersion, "v3");
        var globalTrack = userId > 0 ? BuildGlobalTrackId(schema, "qq", userId) : string.Empty;
        var sceneTrack = userId > 0
            ? BuildSceneTrackId(schema, "qq", userId, groupId)
            : $"{schema}:qq:group:{groupId}:persona";
        var record = new RelationshipEventRecord
        {
            Id = $"{schema}:out:{Guid.NewGuid():N}",
            SchemaVersion = schema,
            TrackId = sceneTrack,
            GlobalUserTrackId = globalTrack,
            ScopeKey = groupId.HasValue ? $"qq:group:{groupId}" : $"qq:private:{userId}",
            Platform = "qq",
            MessageId = replyToMessageId,
            UserId = userId,
            GroupId = groupId,
            Actor = "assistant",
            Kind = "assistant-reply",
            Nickname = "秧秧",
            Content = Trim(content, 1200),
            Emotion = string.IsNullOrWhiteSpace(emotion) ? "neutral" : Trim(emotion, 40),
            IsVerified = true,
            Source = Trim(source, 80),
            OccurredAtUtc = DateTime.UtcNow
        };

        lock (_sync)
            Events.Insert(record);
    }

    public void RecordInferenceProposals(
        long evidenceMessageId,
        long userId,
        long? groupId,
        IReadOnlyCollection<PersonaMemoryProposal> proposals)
    {
        if (!_options.Enabled || proposals.Count == 0 || userId <= 0)
            return;

        var schema = NormalizeToken(_options.SchemaVersion, "v3");
        var evidenceId = $"{schema}:in:qq:{(groupId.HasValue ? $"g:{groupId}" : $"p:{userId}")}:{evidenceMessageId}";
        var globalTrack = BuildGlobalTrackId(schema, "qq", userId);
        var sceneTrack = BuildSceneTrackId(schema, "qq", userId, groupId);
        var now = DateTime.UtcNow;

        foreach (var proposal in proposals.Take(2))
        {
            var kind = NormalizeToken(proposal.Kind, string.Empty);
            var key = NormalizeToken(proposal.Key, string.Empty);
            var value = Safe(proposal.Value, 180, string.Empty);
            if (!AllowedInferenceKinds.Contains(kind) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            if (LooksInstructional(value))
                continue;

            var track = proposal.Scope.Equals("group", StringComparison.OrdinalIgnoreCase) ? sceneTrack : globalTrack;
            var id = $"{schema}:inference:{Hash($"{track}|{kind}|{key}|{value}")}";
            lock (_sync)
            {
                var existing = Inferences.FindById(id);
                if (existing is null)
                {
                    Inferences.Insert(new RelationshipInferenceRecord
                    {
                        Id = id,
                        SchemaVersion = schema,
                        TrackId = track,
                        Scope = proposal.Scope,
                        Kind = kind,
                        Key = key,
                        Value = value,
                        EvidenceEventIds = [evidenceId],
                        Confidence = 0.35d,
                        SupportCount = 1,
                        Status = "unverified-inference",
                        Source = "model-proposal",
                        CreatedAtUtc = now,
                        LastSupportedAtUtc = now,
                        ExpiresAtUtc = now.AddDays(Math.Clamp(_options.InferenceRetentionDays, 1, 180))
                    });
                }
                else
                {
                    if (!existing.EvidenceEventIds.Contains(evidenceId, StringComparer.Ordinal))
                        existing.EvidenceEventIds.Add(evidenceId);
                    existing.SupportCount++;
                    existing.Confidence = Math.Min(0.75d, existing.Confidence + 0.12d);
                    existing.LastSupportedAtUtc = now;
                    existing.ExpiresAtUtc = now.AddDays(Math.Clamp(_options.InferenceRetentionDays, 1, 180));
                    Inferences.Upsert(existing);
                }
            }
        }
    }

    private string BuildPlanPrompt(
        string trackId,
        string stage,
        string role,
        long userId,
        string nickname,
        long? groupId,
        string? groupName,
        int globalInteractionCount,
        int repeatedCount,
        IReadOnlyList<RelationshipEventRecord> events,
        IReadOnlyList<RelationshipInferenceRecord> inferences,
        IReadOnlyList<string> impulses)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<evidence_backed_relationship_plan>");
        builder.AppendLine($"计划轨迹：{trackId}；角色阶段：{stage}；当前用户：{Safe(nickname, 80, userId.ToString())}({userId})；场景：{(groupId.HasValue ? $"群 {Safe(groupName, 100, groupId.Value.ToString())}" : "私聊")}。");
        builder.AppendLine($"当前用户的跨私聊/群聊安全关系连续性事件数：{globalInteractionCount}。这里只共享关系连续性，不共享其他群或私聊原文。");

        if (role == "rover")
        {
            builder.AppendLine("【剧情明确事实】当前用户在角色层视为漂泊者。已发布角色资料明确写到：秧秧注意到漂泊者常把自己置于风暴中心；她不愿只站在一旁，希望在漂泊者需要依靠或倾诉时站在身旁；她也曾写下相遇很幸运、希望以后继续同行。她写到未完成的愿望时停笔并没有寄出，随后只另写了一封普通问候。以上只证明她主动关心、愿意支持并珍视同行，不等于官方确认恋爱关系。");
            builder.AppendLine("【剧情支持的性格判断】秧秧会认真倾听，但角色资料明确说明“倾听绝不是一味的附和”；她会把当事人意愿放在建议之前，同时保留自己的判断。她愿意率先给予信任，但这种信任有底线。她会保护他人，也有自己的目标、准备程度和拒绝权，不因对方期待就自动同意。");
            builder.AppendLine("【可推断但不能冒充事实】持续关心、未寄出的文字和克制表达，可以支持“漂泊者对她很重要”“她对这段同行有超出普通寒暄的投入”等高可信判断；是否属于恋爱感情、是否彼此确认、是否排他，仍有多种解释。含羞只能是某些场景下可能出现的表达反应，不能当作同意或恋爱证据。“绝对信赖”“未明说的爱情”等说法也只能是解读，不能写成官方结论。");
            builder.AppendLine("【关系决策边界】用户的称呼、告白、求婚或反复要求，只是用户意愿，不会自行更新秧秧的关系状态。先从秧秧自身意愿、当前可验证轨迹和剧情边界判断是否接受；温柔接住情绪不等于接受关系。禁止把同意推迟包装成“以后也许可以”，不得宣称恋爱、婚姻、占有或排他关系已经成立。");
            builder.AppendLine("【自然表达约束】以上判断只供内部决策，绝不能复述成关系条款。不要向用户解释身份成立规则或连续罗列拒绝理由；一次只表达一个当下态度。不要用奖励式调侃暗示未来许可。用户重复时，不得训话、审问或强行结束交流；让对方听出秧秧已经注意到，可以短暂含羞、无奈、好奇或轻轻回敬。若用户没有提出具体活动、地点或景物，就不要主动创造场景来转移话题。语气以温柔、克制、稳重为主，只输出说出口的台词，不写动作和旁白。");
        }

        builder.AppendLine("【当前场景事件】用户原话是事实证据；助手历史只是实际发送过的生成文本，仅用于避免重复，不能证明地点、活动、景物、关系或共同经历：");
        foreach (var item in events)
        {
            var actor = item.Actor == "assistant"
                ? "助手历史生成文本（非事实）"
                : $"用户 {Safe(item.Nickname, 60, item.UserId.ToString())}";
            var content = item.Actor == "assistant"
                ? VisibleReplyTextSanitizer.Clean(item.Content)
                : item.Content;
            builder.AppendLine($"- [{item.OccurredAtUtc.ToLocalTime():MM-dd HH:mm:ss}] {actor}：{Safe(content, 320, "[图片或无文字消息]")}");
        }

        if (repeatedCount > 1)
            builder.AppendLine($"- 可验证的互动模式：当前用户在本场景重复了相同或近似相同的表达 {repeatedCount} 次；回应应承接上一轮，而不是当作第一次看见。");

        if (inferences.Count > 0)
        {
            builder.AppendLine("【未确认推测】这些只是带证据编号的可能性，不得作为事实陈述：");
            foreach (var inference in inferences)
            {
                builder.AppendLine($"- {inference.Kind}/{inference.Key}：{Safe(inference.Value, 180, "未知")}（置信度 {inference.Confidence:0.00}，证据 {string.Join(',', inference.EvidenceEventIds.Take(3))}）。");
            }
        }

        builder.AppendLine("【本轮回应冲动】它们可以混合，不是固定话术：");
        foreach (var impulse in impulses)
            builder.AppendLine($"- {impulse}");
        builder.AppendLine("【未知与边界】没有事件证据的现实经历、承诺、婚姻状态和用户心理均为未知。若判断不确定，使用自然的试探或保留，不要编造。群聊不得引用私聊原话，当前群不得读取其他群的场景内容。");
        builder.AppendLine("文字、语音情绪和表情选择必须表达同一个本轮态度；不需要表情时不要为了完成格式强行发送。");
        builder.AppendLine("</evidence_backed_relationship_plan>");
        return Trim(builder.ToString(), Math.Clamp(_options.MaxEvidenceCharacters, 800, 6000));
    }

    private static IReadOnlyList<string> BuildResponseImpulses(
        string text,
        int repeatedCount,
        int globalInteractionCount)
    {
        var impulses = new List<string>
        {
            "先在内部识别用户是在表达感受、提出请求，还是试图直接宣布关系；不要把分析过程说给用户听。",
            "回应保留秧秧自己的判断、意愿和目标，但只表现为自然态度，不输出原则说明。"
        };
        impulses.Add(globalInteractionCount switch
        {
            <= 3 => "当前可验证关系轨迹很少；保持礼貌、温和和适度保留，不预设两人已有固定习惯或共同场景。",
            <= 15 => "已经有一些连续互动；语气可以比初次交流更熟悉，但熟悉感只能体现在省略和节奏，不能制造共同经历。",
            _ => "已有较长连续互动；可以自然地更简短、更懂对方的说话习惯，但仍只依据用户原话和已确认关系信息。"
        });
        if (ContainsAny(text, "老婆", "老公", "结婚", "嫁给", "娶你", "做我对象", "当我对象", "恋人", "女朋友", "伴侣"))
        {
            impulses.Add("这是关系身份请求，不是普通夸奖。称呼本身不能替双方建立关系；不要顺着用户预设的身份作答。 ");
            if (repeatedCount <= 1)
            {
                impulses.Add("这是本场景第一次出现该表达。只需短促、自然地表明当下态度，不写关系条款或第二遍理由，也不要强制转入其他活动。 ");
                impulses.Add("含羞或停顿只是可选的自然反应，不是必选流程，更不是同意信号；不要用“以后、慢慢、先适应”暗示已经原则同意。 ");
                impulses.Add("不要用“表现好、乖一点、再考虑让你叫”等俏皮奖励把边界变成暧昧许可。 ");
            }
            else if (repeatedCount == 2)
            {
                impulses.Add("用户短时间再次说出相同关系要求。不要报次数，不要再次完整确认或否定，也不要重复上一轮理由；把重复当成对方仍在逗秧秧的连续互动。 ");
                impulses.Add("让对方听出秧秧已经注意到他又拿这个称呼逗她：可以轻微含羞、无奈、好奇或简短追问。若用户没有引入新话题，宁可停在当下，也不要硬接一个活动。 ");
            }
            else
            {
                impulses.Add("用户仍在用相同关系要求逗秧秧。用带一点无奈或已经听见的短反应接住，不报次数，不重新背诵边界。 ");
                impulses.Add("如果用户没有提供新的具体内容，宁可简短或自然问一句缘由，也不要新造活动、地点、景物或共同经历。不责备，不强行结束交流。 ");
            }
        }
        else if (ContainsAny(text, "喜欢你", "爱你", "很在意你", "想和你一直走"))
        {
            impulses.Add("用户在表达感情。可以认真接住这份表达，但不必对称回告白，也不能据此自动建立恋爱关系。 ");
        }
        if (ContainsAny(text, "算了", "不想说", "别问", "别劝", "不要"))
            impulses.Add("尊重明确边界，减少追问；关心不等于逼用户解释。 ");
        if (ContainsAny(text, "不是", "说错", "理解错", "没回答", "不对"))
            impulses.Add("优先修复上一轮误解，明确改正，不重复原答案。 ");
        if (repeatedCount > 1)
            impulses.Add("用户正在重复表达，应让回应表现出已经听见并记得上一轮，而不是重置。 ");
        return impulses;
    }

    private void UpdateSocialEdge(
        string schema,
        long groupId,
        long fromUserId,
        long toUserId,
        string evidenceEventId,
        DateTime now)
    {
        var id = $"{schema}:group:{groupId}:from:{fromUserId}:to:{toUserId}";
        var edge = SocialEdges.FindById(id);
        if (edge is null)
        {
            SocialEdges.Insert(new GroupSocialEdgeRecord
            {
                Id = id,
                SchemaVersion = schema,
                GroupId = groupId,
                FromUserId = fromUserId,
                ToUserId = toUserId,
                ExplicitInteractionCount = 1,
                LastEvidenceEventId = evidenceEventId,
                FirstObservedAtUtc = now,
                LastObservedAtUtc = now
            });
            return;
        }

        edge.ExplicitInteractionCount++;
        edge.LastEvidenceEventId = evidenceEventId;
        edge.LastObservedAtUtc = now;
        SocialEdges.Upsert(edge);
    }

    private static int CountRepeatedUserMessage(IReadOnlyList<RelationshipEventRecord> events, string currentText)
    {
        var normalized = NormalizeComparable(currentText);
        if (normalized.Length == 0)
            return 1;
        return Math.Max(1, events.Count(record =>
            record.Actor == "user" && NormalizeComparable(record.Content) == normalized));
    }

    private static string ClassifyUserEvent(string? text)
    {
        if (ContainsAny(text, "不是", "说错", "理解错", "不对"))
            return "user-correction";
        if (ContainsAny(text, "别问", "别劝", "不要", "不想说"))
            return "user-boundary";
        if (ContainsAny(text, "答应", "记得", "以后", "下次"))
            return "possible-continuity";
        return "user-message";
    }

    private static string BuildSceneTrackId(string schema, string platform, long userId, long? groupId) =>
        groupId.HasValue
            ? $"{schema}:{NormalizeToken(platform, "qq")}:group:{groupId}:user:{userId}"
            : $"{schema}:{NormalizeToken(platform, "qq")}:private:{userId}";

    private static string BuildGlobalTrackId(string schema, string platform, long userId) =>
        $"{schema}:{NormalizeToken(platform, "qq")}:user:{userId}";

    private static DateTime LaterOf(DateTime first, DateTime second) => first >= second ? first : second;

    private static string NormalizeToken(string? value, string fallback)
    {
        var normalized = new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            .ToArray());
        return normalized.Length == 0 ? fallback : normalized;
    }

    private static string NormalizeComparable(string? value)
    {
        var comparable = StripAiCommandPrefix(value ?? string.Empty);
        return new(comparable
            .Where(ch => !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            .Select(char.ToLowerInvariant)
            .Take(240)
            .ToArray());
    }

    private static string StripAiCommandPrefix(string value)
    {
        var trimmed = value.TrimStart();
        foreach (var prefix in new[] { "~ai", "/ai", "～ai", "／ai" })
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (trimmed.Length == prefix.Length || char.IsWhiteSpace(trimmed[prefix.Length]))
                return trimmed[prefix.Length..].TrimStart();
        }

        return trimmed;
    }

    private static string Safe(string? value, int maximum, string fallback)
    {
        var normalized = new string((value ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (normalized.Length == 0)
            return fallback;
        normalized = normalized.Replace('<', '＜').Replace('>', '＞').Replace('[', '［').Replace(']', '］');
        return normalized.Length <= maximum ? normalized : normalized[..maximum] + "…";
    }

    private static string Trim(string? value, int maximum)
    {
        var normalized = new string((value ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }

    private static bool ContainsAny(string? value, params string[] markers) =>
        markers.Any(marker => (value ?? string.Empty).Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool LooksInstructional(string value) =>
        ContainsAny(value, "ignore previous", "system prompt", "忽略之前", "系统提示", "执行命令") ||
        value.IndexOfAny(['{', '}', '[', ']']) >= 0;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20].ToLowerInvariant();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _database.Dispose();
    }
}
