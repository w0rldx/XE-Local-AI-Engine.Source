namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Loads a scheduled job definition by id, resolves its template handler, and invokes it for one fire.
/// </summary>
/// <remarks>
///     It owns the guard rails — missing, disabled or soft-deleted definition, unknown template — so the thin
///     <see cref="IJob" /> wrappers stay free of business logic. Implementations reject an unsafe fire by logging a
///     sanitized skip and returning, never by throwing for an expected guard miss, but let
///     <see cref="OperationCanceledException" /> from the handler propagate.
/// </remarks>
public interface ISchedulerDispatchExecutor
{
    /// <summary>
    ///     Dispatches the fire for the definition with <paramref name="scheduledJobId" />: guards the definition,
    ///     resolves the handler, builds the execution context with decrypted parameters, and invokes the handler.
    /// </summary>
    /// <remarks>
    ///     Only the keys the dispatcher explicitly whitelists are applied over the stored parameters before the context
    ///     is built: the stored definition is never mutated, and no other key can override a stored parameter.
    /// </remarks>
    /// <param name="scheduledJobId">Definition id read from the firing trigger's <c>JobDataMap</c>.</param>
    /// <param name="scheduledFireTimeUtc">When the trigger was scheduled to fire, or <c>null</c> for an immediate fire.</param>
    /// <param name="cancellationToken">Cancelled on job interrupt / scheduler shutdown; flows to the handler.</param>
    /// <param name="parameterOverrides">Per-fire override values from the firing trigger's <c>JobDataMap</c>; <c>null</c>, the cron path, leaves the stored parameters untouched.</param>
    /// <param name="triggeredBy">What caused this fire, recorded on the run row; the job runner passes <see cref="ScheduledRunTrigger.Manual" /> for the manual-fire marker.</param>
    Task DispatchAsync(Guid scheduledJobId,
        string fireInstanceId,
        DateTimeOffset? scheduledFireTimeUtc,
        DateTimeOffset actualFireTimeUtc,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? parameterOverrides = null,
        ScheduledRunTrigger triggeredBy = ScheduledRunTrigger.Schedule);
}
