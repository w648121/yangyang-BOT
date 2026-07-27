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
    private readonly RelationshipTrajectoryOptions _options;
    private readonly IOptionsMonitor<RelationshipLanguageOptions> _language;
    private readonly ILogger<RelationshipTrajectoryService> _logger;
    private readonly LiteDatabase _database;
    private readonly string _schema;
    private readonly object _sync = new();
    private bool _disposed;

    public RelationshipTrajectoryService(
        IOptions<RelationshipTrajectoryOptions> options,
        IOptionsMonitor<RelationshipLanguageOptions> language,
        ILogger<RelationshipTrajectoryService> logger)
    {
        _options = options.Value;
        _language = language;
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
        foreach (var rule in _language.CurrentValue.GroupContextRules.Where(IsPresent))
            builder.AppendLine(rule.Trim());
        foreach (var item in events)
        {
            var actor = item.Actor == "assistant"
                ? $"{_language.CurrentValue.AssistantDisplayName}的历史生成文本（只作对话衔接，不作事实）"
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
            Nickname = _language.CurrentValue.AssistantDisplayName,
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
            if (!_options.AllowedInferenceKinds.Contains(kind, StringComparer.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(key) ||
                string.IsNullOrWhiteSpace(value))
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

        if (_language.CurrentValue.RoleEvidenceRules.TryGetValue(role, out var roleRules))
        {
            foreach (var rule in roleRules.Where(IsPresent))
                builder.AppendLine(rule.Trim());
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
        foreach (var rule in _language.CurrentValue.FinalEvidenceRules.Where(IsPresent))
            builder.AppendLine(rule.Trim());
        builder.AppendLine("</evidence_backed_relationship_plan>");
        return Trim(builder.ToString(), Math.Clamp(_options.MaxEvidenceCharacters, 800, 6000));
    }

    private IReadOnlyList<string> BuildResponseImpulses(
        string text,
        int repeatedCount,
        int globalInteractionCount)
    {
        var language = _language.CurrentValue;
        var impulses = language.DefaultResponseImpulses
            .Where(IsPresent)
            .Select(item => item.Trim())
            .ToList();
        var familiarity = language.FamiliarityRules
            .Where(rule => rule.MaximumInteractionCount > 0 && IsPresent(rule.Instruction))
            .OrderBy(rule => rule.MaximumInteractionCount)
            .FirstOrDefault(rule => globalInteractionCount <= rule.MaximumInteractionCount) ??
            language.FamiliarityRules
                .Where(rule => rule.MaximumInteractionCount > 0 && IsPresent(rule.Instruction))
                .OrderByDescending(rule => rule.MaximumInteractionCount)
                .FirstOrDefault();
        if (familiarity is not null)
            impulses.Add(familiarity.Instruction.Trim());
        if (ContainsAny(text, language.IdentityRequestMarkers))
        {
            impulses.AddRange(language.IdentityRequestRules.Where(IsPresent).Select(item => item.Trim()));
            if (repeatedCount <= 1)
            {
                impulses.AddRange(language.FirstIdentityRequestRules.Where(IsPresent).Select(item => item.Trim()));
            }
            else if (repeatedCount == 2)
            {
                impulses.AddRange(language.SecondIdentityRequestRules.Where(IsPresent).Select(item => item.Trim()));
            }
            else
            {
                impulses.AddRange(language.LaterIdentityRequestRules.Where(IsPresent).Select(item => item.Trim()));
            }
        }
        else if (ContainsAny(text, language.AffectionMarkers))
        {
            impulses.Add(language.AffectionRule.Trim());
        }
        if (ContainsAny(text, language.BoundaryMarkers))
            impulses.Add(language.BoundaryRule.Trim());
        if (ContainsAny(text, language.RepairMarkers))
            impulses.Add(language.RepairRule.Trim());
        if (repeatedCount > 1)
            impulses.Add(language.ContinuityRule.Trim());
        return impulses;
    }

    private static bool IsPresent(string? value) => !string.IsNullOrWhiteSpace(value);

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

    private string ClassifyUserEvent(string? text)
    {
        var language = _language.CurrentValue;
        if (ContainsAny(text, language.RepairMarkers))
            return "user-correction";
        if (ContainsAny(text, language.BoundaryMarkers))
            return "user-boundary";
        if (ContainsAny(text, language.ContinuityMarkers))
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

    private static bool ContainsAny(string? value, IEnumerable<string> markers) =>
        markers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Any(marker => (value ?? string.Empty).Contains(
                marker.Trim(),
                StringComparison.OrdinalIgnoreCase));

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
