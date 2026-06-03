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

        // A manual fire may carry a per-fire use-case override on the firing trigger's data map (merged into
        // MergedJobDataMap by Quartz). Forward ONLY this whitelisted key to the executor; a cron/no-override fire has no
        // such entry and dispatches the stored parameters unchanged. The executor decides whether/how to apply it.
        var parameterOverrides = ExtractParameterOverrides(context);

        await dispatchExecutor.DispatchAsync(
            scheduledJobId,
            context.FireInstanceId,
            context.ScheduledFireTimeUtc,
            context.FireTimeUtc,
            context.CancellationToken,
            parameterOverrides).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads the single whitelisted per-fire override key (the model-fit use-case) from the merged data map. Returns
    ///     <c>null</c> when it is absent or blank so a normal (cron / no-override) fire dispatches the stored parameters
    ///     unchanged. No other data-map key is ever surfaced as an override.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ExtractParameterOverrides(IJobExecutionContext context)
    {
        var useCaseOverride = context.MergedJobDataMap.GetString(SchedulerJobKeys.ModelFitUseCaseOverrideKey);
        if (string.IsNullOrWhiteSpace(useCaseOverride))
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SchedulerJobKeys.ModelFitUseCaseOverrideKey] = useCaseOverride
        };
    }
}
