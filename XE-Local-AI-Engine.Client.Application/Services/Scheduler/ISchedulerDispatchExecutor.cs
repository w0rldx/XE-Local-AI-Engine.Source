namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Loads a scheduled job definition by id, resolves its template handler, and invokes it for one fire. Owns the
///     guard rails (missing / disabled / soft-deleted definition, unknown template) so the thin <see cref="IJob" />
///     wrappers stay free of business logic. Implementations reject unsafe fires by logging a sanitized skip and
///     returning — they never throw for an expected guard miss — but let <see cref="OperationCanceledException" />
///     from the handler propagate.
/// </summary>
public interface ISchedulerDispatchExecutor
{
    /// <summary>
    ///     Dispatches the fire for the definition with <paramref name="scheduledJobId" />: guards the definition, resolves
    ///     the handler, builds the execution context (with decrypted parameters), and invokes the handler.
    /// </summary>
    /// <param name="scheduledJobId">Definition id read from the firing trigger's <c>JobDataMap</c>.</param>
    /// <param name="fireInstanceId">Quartz fire-instance id for this execution.</param>
    /// <param name="scheduledFireTimeUtc">When the trigger was scheduled to fire, or <c>null</c> for an immediate fire.</param>
    /// <param name="actualFireTimeUtc">When the trigger actually fired.</param>
    /// <param name="cancellationToken">Cancelled on job interrupt / scheduler shutdown; flows to the handler.</param>
    /// <param name="parameterOverrides">
    ///     Optional per-fire override values read from the firing trigger's <c>JobDataMap</c>. The dispatcher applies only
    ///     the keys it explicitly whitelists (today: the model-fit use-case) over the definition's stored parameters before
    ///     building the context — the stored definition is never mutated and no other key can override a stored parameter.
    ///     <c>null</c> (the recurring/cron path) leaves the stored parameters untouched.
    /// </param>
    /// <param name="triggeredBy">
    ///     What caused this fire, recorded on the run row and passed to the handler. Defaults to
    ///     <see cref="ScheduledRunTrigger.Schedule" />; the Quartz job runner passes
    ///     <see cref="ScheduledRunTrigger.Manual" /> when the firing trigger carries the manual-fire marker.
    /// </param>
    Task DispatchAsync(Guid scheduledJobId,
        string fireInstanceId,
        DateTimeOffset? scheduledFireTimeUtc,
        DateTimeOffset actualFireTimeUtc,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? parameterOverrides = null,
        ScheduledRunTrigger triggeredBy = ScheduledRunTrigger.Schedule);
}
