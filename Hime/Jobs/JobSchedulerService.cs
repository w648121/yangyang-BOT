using Hime.Data.Services;
using Hime.Hosting;
using Hime.Messaging;
using Hime.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Jobs;

public sealed record ExecuteScheduledJobCommand(string JobId) : ICommand;

public sealed class ExecuteScheduledJobCommandHandler(JobExecutionService execution)
    : ICommandHandler<ExecuteScheduledJobCommand>
{
    public Task HandleAsync(
        ExecuteScheduledJobCommand command,
        CancellationToken cancellationToken) =>
        execution.ExecuteAsync(command.JobId, cancellationToken);
}

public sealed class JobExecutionService(
    ScheduledJobStore store,
    IAccountMessageSender sender,
    GroupResponseStateService groupResponses,
    IOptions<JobOptions> options,
    ILogger<JobExecutionService> logger)
{
    private readonly JobOptions _options = options.Value;

    public async Task ExecuteAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = store.Get(jobId);
        if (job is null || job.Status != ScheduledJobStatus.Running)
            return;

        var now = DateTime.UtcNow;
        if (job.IsGroup && !groupResponses.IsEnabled(job.TargetId))
        {
            store.Postpone(
                job.Id,
                now.AddSeconds(Math.Clamp(_options.DisabledGroupPostponeSeconds, 10, 3600)),
                "群响应已停止，提醒等待恢复。");
            return;
        }

        if (!sender.IsAccountReady(job.AccountId))
        {
            store.Postpone(
                job.Id,
                now.AddSeconds(Math.Clamp(_options.OfflineAccountPostponeSeconds, 5, 600)),
                "原机器人账号未连接，等待恢复。");
            return;
        }

        var delivery = store.TryBeginDelivery(job);
        if (delivery == BeginDeliveryResult.AlreadyDelivered)
        {
            store.MarkDelivered(job.Id, now);
            return;
        }

        if (delivery == BeginDeliveryResult.PreviousAttemptUncertain)
        {
            store.MarkUncertainWithoutResend(job.Id, now);
            logger.LogWarning(
                "Reminder {JobId} was not resent because a previous QQ delivery attempt is uncertain",
                job.Id);
            return;
        }

        try
        {
            var text = $"到时间啦，别忘了{NormalizeContent(job.Content)}。";
            await sender.SendTextAsync(
                job.AccountId,
                job.IsGroup,
                job.TargetId,
                job.IsGroup ? job.CreatorUserId : null,
                text,
                cancellationToken);
            store.MarkDelivered(job.Id, DateTime.UtcNow);
            logger.LogInformation(
                "Delivered reminder {JobId}/{ShortCode} through account {AccountId}",
                job.Id,
                job.ShortCode,
                job.AccountId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            store.MarkAttemptFailed(job.Id, ex.Message, DateTime.UtcNow);
            logger.LogError(
                ex,
                "Reminder {JobId}/{ShortCode} delivery failed and was not automatically repeated",
                job.Id,
                job.ShortCode);
        }
    }

    private static string NormalizeContent(string value)
    {
        var text = value.Trim().TrimEnd('。', '！', '!', '.', '，', ',');
        return string.IsNullOrWhiteSpace(text) ? "看看刚才定下的事情" : text;
    }
}

public sealed class JobSchedulerService(
    ScheduledJobStore store,
    JobTimeParser parser,
    ConversationMessageDispatcher dispatcher,
    ICommandBus commandBus,
    IOptions<JobOptions> options,
    ILogger<JobSchedulerService> logger) : BackgroundService
{
    private readonly JobOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Persistent job scheduler is disabled");
            return;
        }

        var repaired = store.RepairCompositeRelativeSchedules(parser);
        if (repaired > 0)
        {
            logger.LogWarning(
                "Repaired {Count} legacy reminder(s) that previously ignored year/month duration components",
                repaired);
        }
        logger.LogInformation("Persistent job scheduler started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = store.ClaimDue(
                    DateTime.UtcNow,
                    _options.ClaimBatchSize,
                    TimeSpan.FromSeconds(Math.Clamp(_options.LeaseSeconds, 30, 600)));
                foreach (var job in due)
                {
                    try
                    {
                        await dispatcher.EnqueueAsync(
                            job.ConversationKey,
                            $"job:{job.Id}",
                            token => commandBus.SendAsync(
                                new ExecuteScheduledJobCommand(job.Id),
                                token));
                    }
                    catch (Exception ex)
                    {
                        store.Postpone(job.Id, DateTime.UtcNow.AddSeconds(10), "进入消息总线失败，稍后重试。");
                        logger.LogWarning(ex, "Unable to enqueue scheduled job {JobId}", job.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Persistent job scheduler iteration failed");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 1, 30)),
                stoppingToken);
        }
    }
}
