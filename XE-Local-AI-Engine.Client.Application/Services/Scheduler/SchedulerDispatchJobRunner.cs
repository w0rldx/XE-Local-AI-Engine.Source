namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using Quartz;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Shared fire-extraction logic for the two dispatch <see cref="IJob" /> variants: it reads the definition id from
///     the merged <c>JobDataMap</c>, then delegates to the executor with the fire metadata.
/// </summary>
/// <remarks>
///     A malformed or missing id is logged sanitized and swallowed rather than thrown, so a single corrupt trigger
///     cannot fault the scheduler. <see cref="OperationCanceledException" /> from the executor propagates instead, so
///     Quartz observes an interrupt or shutdown.
/// </remarks>
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

        // A manual fire may carry per-fire overrides on the firing trigger's data map. Forward ONLY the whitelisted keys, which the
        // executor decides how to apply; a cron fire has none and dispatches the stored parameters unchanged.
        var parameterOverrides = ExtractParameterOverrides(context);

        // Only TriggerNowAsync stamps the manual marker, so its absence means Quartz fired the definition's own trigger.
        var triggeredBy = string.Equals(SafeGetString(context, SchedulerJobKeys.ManualFireKey), bool.TrueString, StringComparison.OrdinalIgnoreCase)
            ? ScheduledRunTrigger.Manual
            : ScheduledRunTrigger.Schedule;

        await dispatchExecutor.DispatchAsync(scheduledJobId,
            context.FireInstanceId,
            context.ScheduledFireTimeUtc,
            context.FireTimeUtc,
            context.CancellationToken,
            parameterOverrides,
            triggeredBy);
    }

    /// <summary>
    ///     Reads the whitelisted per-fire override keys — the model-fit use-case, breadth limit, quant and context
    ///     target — from the merged data map.
    /// </summary>
    /// <remarks>
    ///     It returns <c>null</c> when none are present, so a normal cron fire dispatches the stored parameters
    ///     unchanged. No other data-map key is ever surfaced as an override.
    /// </remarks>
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

        var quantOverride = SafeGetString(context, SchedulerJobKeys.ModelFitQuantOverrideKey);
        if (!string.IsNullOrWhiteSpace(quantOverride))
        {
            overrides[SchedulerJobKeys.ModelFitQuantOverrideKey] = quantOverride;
        }

        var ctxTargetOverride = SafeGetString(context, SchedulerJobKeys.ModelFitCtxTargetOverrideKey);
        if (!string.IsNullOrWhiteSpace(ctxTargetOverride))
        {
            overrides[SchedulerJobKeys.ModelFitCtxTargetOverrideKey] = ctxTargetOverride;
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
