namespace XE_Local_AI_Engine.Client.Services.Scheduler;

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
    Task DispatchAsync(
        Guid scheduledJobId,
        string fireInstanceId,
        DateTimeOffset? scheduledFireTimeUtc,
        DateTimeOffset actualFireTimeUtc,
        CancellationToken cancellationToken);
}
