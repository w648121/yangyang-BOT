using System.Text;
using System.Text.Json;
using Hime.Messaging;
using Hime.Messaging.Interactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Entities.Message;
using Sora.Entities.Segments;
using HimeMessageContext = Hime.Messaging.MessageContext;

namespace Hime.Jobs;

public sealed class JobRequestService(
    JobIntentDetector intentDetector,
    JobTimeParser parser,
    JobEditIntentParser editParser,
    ScheduledJobStore store,
    TemporalAnchorService temporalAnchors,
    IInteractionManager interactions,
    IOptions<JobOptions> options,
    ILogger<JobRequestService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly JobOptions _options = options.Value;

    public bool IsPotentialRequest(IncomingMessage message)
    {
        if (intentDetector.IsPotentialRequest(message.Text))
            return true;
        if (!editParser.IsEditRequest(message.Text))
            return false;
        return editParser.TryReadShortCode(message.Text, out _) ||
               editParser.TryReadShortCode(ReadQuotedText(message), out _);
    }

    public async Task<bool> TryHandleNaturalAsync(
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        if (await TryHandleEditAsync(message, cancellationToken))
            return true;
        if (!intentDetector.IsPotentialRequest(message.Text))
            return false;
        await CreateOrContinueAsync(
            message,
            message.Text,
            BuildCreationKey(message),
            cancellationToken);
        return true;
    }

    private async Task<bool> TryHandleEditAsync(
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        if (!editParser.IsEditRequest(message.Text))
            return false;

        var quotedText = ReadQuotedText(message);
        if (!editParser.TryReadShortCode(message.Text, out var code) &&
            !editParser.TryReadShortCode(quotedText, out code))
        {
            return false;
        }

        var existing = store.FindActiveByCode(
            message.AccountId,
            message.ScopeKey,
            message.SenderId,
            code);
        if (existing is null)
        {
            await message.ReplyChannel.SendTextAsync(
                $"没有找到你在当前会话中的未完成提醒 {code}。",
                cancellationToken);
            return true;
        }

        var edit = editParser.Parse(
            message.Text,
            existing,
            DateTimeOffset.UtcNow);
        if (!edit.Success || edit.Schedule is null)
        {
            await message.ReplyChannel.SendTextAsync(
                edit.Error,
                cancellationToken);
            return true;
        }

        var updated = store.UpdateSchedule(
            message.AccountId,
            message.ScopeKey,
            message.SenderId,
            code,
            edit.Schedule,
            message.Text);
        if (updated is null)
        {
            await message.ReplyChannel.SendTextAsync(
                $"提醒 {code} 正在执行或已经结束，暂时不能修改。",
                cancellationToken);
            return true;
        }

        await message.ReplyChannel.SendTextAsync(
            $"已修改提醒 {code}：{edit.Schedule.DisplayTime}提醒你“{edit.Schedule.Content}”。",
            cancellationToken);
        logger.LogInformation(
            "Updated reminder {JobId}/{ShortCode} from a natural reply edit (Account={AccountId}, Scope={ScopeKey}, User={UserId}, Recurrence={Recurrence}, RunAtUtc={RunAtUtc})",
            updated.Id,
            updated.ShortCode,
            updated.AccountId,
            updated.ScopeKey,
            updated.CreatorUserId,
            updated.Recurrence,
            updated.NextRunAtUtc);
        return true;
    }

    public async Task HandleReminderCommandAsync(
        IncomingMessage message,
        string rawText,
        CancellationToken cancellationToken)
    {
        var payload = StripCommand(rawText, "/提醒");
        if (string.IsNullOrWhiteSpace(payload))
        {
            await message.ReplyChannel.SendTextAsync(
                _options.ReminderUsageHint,
                cancellationToken);
            return;
        }

        await CreateOrContinueAsync(
            message,
            payload,
            BuildCreationKey(message),
            cancellationToken);
    }

    public async Task HandleTaskCommandAsync(
        IncomingMessage message,
        string rawText,
        CancellationToken cancellationToken)
    {
        var payload = StripCommand(rawText, "/任务");
        if (TryReadCancelCode(payload, out var code))
        {
            var cancelled = store.Cancel(
                message.AccountId,
                message.ScopeKey,
                message.SenderId,
                code);
            await message.ReplyChannel.SendTextAsync(
                cancelled
                    ? $"已取消提醒 {code}。"
                    : $"没有找到你在当前会话中的未完成提醒 {code}。",
                cancellationToken);
            return;
        }

        var jobs = store.ListActive(message.AccountId, message.ScopeKey, message.SenderId);
        if (jobs.Count == 0)
        {
            await message.ReplyChannel.SendTextAsync(
                "当前会话里没有你的未完成提醒。",
                cancellationToken);
            return;
        }

        var builder = new StringBuilder("你的提醒：");
        foreach (var job in jobs.Take(20))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(job.NextRunAtUtc, DateTimeKind.Utc),
                JobTimeZones.Beijing);
            var when = job.Recurrence switch
            {
                JobRecurrenceKind.Daily => $"每天 {job.LocalHour:00}:{job.LocalMinute:00}",
                JobRecurrenceKind.Weekly =>
                    $"每周{WeekdayText(job.LocalDayOfWeek)} {job.LocalHour:00}:{job.LocalMinute:00}",
                _ => local.ToString("yyyy-MM-dd HH:mm")
            };
            builder.Append($"\n{job.ShortCode}｜{when}｜{job.Content}");
        }
        builder.Append("\n取消：/任务 取消 4位编号");
        builder.Append("\n修改：引用提醒确认消息，说“改成每天”或“改成每周一上午9点”");
        await message.ReplyChannel.SendTextAsync(builder.ToString(), cancellationToken);
    }

    public async Task ResumeDraftAsync(
        HimeMessageContext context,
        PendingInteraction interaction,
        CancellationToken cancellationToken)
    {
        JobDraftState? state;
        try
        {
            state = JsonSerializer.Deserialize<JobDraftState>(interaction.StateJson ?? string.Empty, JsonOptions);
        }
        catch (JsonException)
        {
            state = null;
        }

        if (state is null)
        {
            await context.Message.ReplyChannel.SendTextAsync(
                "刚才的提醒草稿已经失效，请重新说一次。",
                cancellationToken);
            return;
        }

        var addition = context.Message.Text.Trim();
        if (state.Need == JobParseNeed.EventAnchor &&
            state.EventReference is not null)
        {
            temporalAnchors.LearnExpectedEvent(
                context.Message,
                state.EventReference.EventName,
                state.EventReference.RecurrenceHint,
                addition);
            await CreateOrContinueAsync(
                context.Message,
                state.OriginalText,
                state.CreationKey,
                cancellationToken);
            return;
        }

        var combined = state.Need == JobParseNeed.Content
            ? $"{state.OriginalText} 提醒我 {addition}"
            : $"{addition} {state.OriginalText}";
        await CreateOrContinueAsync(
            context.Message,
            combined,
            state.CreationKey,
            cancellationToken);
    }

    private async Task CreateOrContinueAsync(
        IncomingMessage message,
        string requestText,
        string creationKey,
        CancellationToken cancellationToken)
    {
        var parsed = parser.Parse(requestText, DateTimeOffset.UtcNow);
        JobScheduleSpec? schedule = parsed.Schedule;
        string? resolvedEventName = null;
        if (schedule is null && parsed.EventReference is not null)
        {
            var resolution = temporalAnchors.TryResolve(
                message,
                parsed.EventReference,
                DateTimeOffset.UtcNow);
            if (resolution is not null)
            {
                schedule = resolution.Schedule;
                resolvedEventName = parsed.EventReference.EventName;
                logger.LogInformation(
                    "Resolved reminder event anchor {EventName} from {Source} (UserId={UserId}, GroupId={GroupId})",
                    resolvedEventName,
                    resolution.Source,
                    message.SenderId,
                    message.GroupId);
            }
        }

        if (schedule is null)
        {
            var now = DateTimeOffset.UtcNow;
            var state = new JobDraftState(
                requestText,
                creationKey,
                parsed.Need,
                parsed.EventReference);
            await interactions.RegisterAsync(
                new PendingInteraction(
                    Guid.NewGuid().ToString("N"),
                    JobInteractionScopes.ForUser(message.ScopeKey, message.SenderId),
                    InteractionKinds.JobDraft,
                    InteractionMode.HardWait,
                    message.SenderId,
                    now,
                    now.AddMinutes(3),
                    JsonSerializer.Serialize(state, JsonOptions)),
                cancellationToken);
            await message.ReplyChannel.SendTextAsync(
                parsed.EventReference is null
                    ? $"{parsed.Error}\n3分钟内直接补充即可；发送“取消”可以退出。"
                    : $"我查过已保存的事件、结构化事实和聊天记忆，还没有找到“{parsed.EventReference.EventName}”的明确时间。\n" +
                      $"请告诉我它具体几点或哪天发生，例如“{_options.EventAnchorPromptExample}”；3分钟内有效。",
                cancellationToken);
            return;
        }

        try
        {
            var job = new ScheduledJobRecord
            {
                Platform = message.Platform,
                AccountId = message.AccountId,
                ScopeKey = message.ScopeKey,
                ConversationKey = message.ConversationKey,
                CreatorUserId = message.SenderId,
                IsGroup = message.IsGroup,
                TargetId = message.ReplyChannel.TargetId,
                Content = schedule.Content,
                OriginalText = requestText,
                CreationKey = creationKey,
                NextRunAtUtc = schedule.RunAtUtc,
                TimeZoneId = JobTimeZones.Beijing.Id,
                Recurrence = schedule.Recurrence,
                LocalHour = schedule.LocalHour,
                LocalMinute = schedule.LocalMinute,
                LocalDayOfWeek = schedule.LocalDayOfWeek
            };
            var result = store.Create(job, _options.MaxActiveJobsPerUser);
            var prefix = result.Created ? "记下了" : "这个提醒已经记过了";
            var memoryNote = string.IsNullOrWhiteSpace(resolvedEventName)
                ? string.Empty
                : $"（已按记忆中的“{resolvedEventName}”时间计算）";
            await message.ReplyChannel.SendTextAsync(
                $"{prefix}{memoryNote}，编号 {result.Job.ShortCode}：{schedule.DisplayTime}提醒你“{schedule.Content}”。",
                cancellationToken);
            logger.LogInformation(
                "Scheduled reminder {JobId}/{ShortCode} (Account={AccountId}, Scope={ScopeKey}, User={UserId}, RunAtUtc={RunAtUtc}, Created={Created})",
                result.Job.Id,
                result.Job.ShortCode,
                result.Job.AccountId,
                result.Job.ScopeKey,
                result.Job.CreatorUserId,
                result.Job.NextRunAtUtc,
                result.Created);
        }
        catch (InvalidOperationException ex)
        {
            await message.ReplyChannel.SendTextAsync(ex.Message, cancellationToken);
        }
    }

    private static string BuildCreationKey(IncomingMessage message) =>
        $"{message.Platform}:{message.AccountId}:{message.ScopeKey}:{message.MessageId}:reminder";

    private static string? ReadQuotedText(IncomingMessage message)
    {
        var reply = message.NativeEvent?.Message.Body?
            .OfType<ReplySegment>()
            .FirstOrDefault();
        return reply?.Content?.GetText();
    }

    private static string StripCommand(string rawText, string command)
    {
        var text = rawText.Trim();
        return text.StartsWith(command, StringComparison.OrdinalIgnoreCase)
            ? text[command.Length..].Trim()
            : text;
    }

    private static bool TryReadCancelCode(string value, out string code)
    {
        var tokens = value.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 2 &&
            (tokens[0].Equals("取消", StringComparison.OrdinalIgnoreCase) ||
             tokens[0].Equals("删除", StringComparison.OrdinalIgnoreCase)) &&
            tokens[1].Length == 4 &&
            tokens[1].All(char.IsDigit))
        {
            code = tokens[1];
            return true;
        }

        code = string.Empty;
        return false;
    }

    private static string WeekdayText(int? day) => (DayOfWeek)(day ?? 0) switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日"
    };

    private sealed record JobDraftState(
        string OriginalText,
        string CreationKey,
        JobParseNeed Need,
        JobEventReference? EventReference);
}

public sealed class JobDraftContinuationHandler(JobRequestService requests)
    : IInteractionContinuationHandler
{
    public string Kind => InteractionKinds.JobDraft;

    public Task ResumeAsync(
        HimeMessageContext context,
        PendingInteraction interaction,
        CancellationToken cancellationToken) =>
        requests.ResumeDraftAsync(context, interaction, cancellationToken);
}
