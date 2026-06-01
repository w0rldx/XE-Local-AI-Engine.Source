namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using Quartz;

/// <summary>
///     Shared fire-extraction logic for the two dispatch <see cref="IJob" /> variants (overlapping and
///     non-overlapping). Reads the definition id from the merged <c>JobDataMap</c>, then delegates to the executor
///     with the fire metadata. A malformed / missing id is logged (sanitized) and swallowed rather than thrown, so a
///     single corrupt trigger cannot fault the scheduler; <see cref="OperationCanceledException" /> from the executor
///     is allowed to propagate so Quartz observes interrupt / shutdown.
/// </summary>
internal static class SchedulerDispatchJobRunner
{
    public static async Task RunAsync(
        ISchedulerDispatchExecutor dispatchExecutor,
        ILogger logger,
        IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(dispatchExecutor);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(context);

        var rawScheduledJobId = context.MergedJobDataMap.GetString(SchedulerJobKeys.ScheduledJobIdKey);
        if (!Guid.TryParse(rawScheduledJobId, out var scheduledJobId))
        {
            logger.LogWarning(
                "Scheduled job dispatch skipped: job {JobKey} (fire {FireInstanceId}) has no valid '{DataMapKey}' in its JobDataMap.",
                context.JobDetail.Key,
                context.FireInstanceId,
                SchedulerJobKeys.ScheduledJobIdKey);
            return;
        }

        await dispatchExecutor.DispatchAsync(
            scheduledJobId,
            context.FireInstanceId,
            context.ScheduledFireTimeUtc,
            context.FireTimeUtc,
            context.CancellationToken).ConfigureAwait(false);
    }
}
