namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Template-guarded facade over the scheduler trigger service (plan Marker 4). This is NOT a new execution path: it
///     never runs llmfit directly and owns no run/cancellation/history state. It simply asks the scheduler to fire an
///     EXISTING <c>model-recommendation-check</c> job definition; the dispatcher then drives the Marker 3 handler, which
///     owns the run history and the actual utility run.
///     <para>
///         The guard is a security boundary: the endpoint accepts only a scheduled-job id (never an image reference,
///         command line or template id), and this facade refuses to trigger any job whose template is not
///         <c>model-recommendation-check</c>, so the model-fit refresh endpoint can never be used to fire an arbitrary
///         scheduled job of another template.
///     </para>
/// </summary>
public interface IModelFitRefreshTrigger
{
    /// <summary>
    ///     Triggers an immediate refresh by firing the scheduled job <paramref name="scheduledJobId" />. Throws
    ///     <see cref="ScheduledJobValidationException" /> when no definition has that id or when its template is not
    ///     <c>model-recommendation-check</c>; otherwise delegates to the scheduler's
    ///     <see cref="IScheduledJobManagementService.TriggerNowAsync" /> (which itself throws
    ///     <see cref="ScheduledJobValidationException" /> for a disabled/deleted/forbidden/unscheduled job).
    ///     <para>
    ///         <paramref name="useCaseOverride" /> optionally runs the refresh for a specific use-case instead of the
    ///         definition's baked one. It is validated against the fixed six-value llmfit allowlist before it reaches the
    ///         run; an unknown value throws <see cref="ScheduledJobValidationException" /> and nothing fires. A
    ///         <c>null</c>/empty value fires the definition's stored use-case unchanged (back-compat). Only the use-case is
    ///         widened — never an image reference, command line, or any other parameter.
    ///     </para>
    /// </summary>
    Task TriggerRecommendationRefreshAsync(
        Guid scheduledJobId,
        string? useCaseOverride = null,
        CancellationToken cancellationToken = default);
}
