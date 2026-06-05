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
    public static async Task RunAsync(ISchedulerDispatchExecutor dispatchExecutor,
        ILogger logger,
        IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(dispatchExecutor);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(context);

        var rawScheduledJobId = SafeGetString(context, SchedulerJobKeys.ScheduledJobIdKey);
        if (!Guid.TryParse(rawScheduledJobId, out var scheduledJobId))
        {
            logger.LogWarning("Scheduled job dispatch skipped: job {JobKey} (fire {FireInstanceId}) has no valid '{DataMapKey}' in its JobDataMap.",
                context.JobDetail.Key,
                context.FireInstanceId,
                SchedulerJobKeys.ScheduledJobIdKey);
            return;
        }

        // A manual fire may carry per-fire overrides on the firing trigger's data map (merged into MergedJobDataMap by
        // Quartz). Forward ONLY the whitelisted keys to the executor; a cron/no-override fire has none and dispatches the
        // stored parameters unchanged. The executor decides whether/how to apply them.
        var parameterOverrides = ExtractParameterOverrides(context);

        await dispatchExecutor.DispatchAsync(scheduledJobId,
            context.FireInstanceId,
            context.ScheduledFireTimeUtc,
            context.FireTimeUtc,
            context.CancellationToken,
            parameterOverrides).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads the whitelisted per-fire override keys (the model-fit use-case and breadth limit) from the merged data
    ///     map. Returns <c>null</c> when none are present so a normal (cron / no-override) fire dispatches the stored
    ///     parameters unchanged. No other data-map key is ever surfaced as an override.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ExtractParameterOverrides(IJobExecutionContext context)
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);

        var useCaseOverride = SafeGetString(context, SchedulerJobKeys.ModelFitUseCaseOverrideKey);
        if (!string.IsNullOrWhiteSpace(useCaseOverride))
        {
            overrides[SchedulerJobKeys.ModelFitUseCaseOverrideKey] = useCaseOverride;
        }

        var limitOverride = SafeGetString(context, SchedulerJobKeys.ModelFitLimitOverrideKey);
        if (!string.IsNullOrWhiteSpace(limitOverride))
        {
            overrides[SchedulerJobKeys.ModelFitLimitOverrideKey] = limitOverride;
        }

        return overrides.Count == 0 ? null : overrides;
    }

    /// <summary>
    ///     Reads a string value from the merged data map WITHOUT throwing when the key is absent. Quartz's
    ///     <see cref="JobDataMap" /> <c>GetString</c> throws <see cref="KeyNotFoundException" /> for a missing key, so a
    ///     no-override (cron) fire — which carries none of the optional keys — must be read defensively.
    /// </summary>
    private static string? SafeGetString(IJobExecutionContext context, string key)
    {
        return context.MergedJobDataMap.TryGetValue(key, out var value) ? value as string : null;
    }
}
