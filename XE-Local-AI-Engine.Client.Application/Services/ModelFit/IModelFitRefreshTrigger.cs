namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Template-guarded facade over the scheduler trigger service: it asks the scheduler to fire an EXISTING
///     <c>model-recommendation-check</c> job definition, and the dispatcher then drives the model-fit handler.
/// </summary>
/// <remarks>
///     This is NOT a new execution path — it never runs llmfit directly and owns no run, cancellation or history
///     state; the handler owns the run history and the actual utility run. The guard is a security boundary: the
///     endpoint accepts only a scheduled-job id (never an image reference, command line or template id), and this
///     facade refuses to trigger any job whose template is not <c>model-recommendation-check</c>, so the model-fit
///     refresh endpoint can never fire an arbitrary scheduled job of another template.
/// </remarks>
public interface IModelFitRefreshTrigger
{
    /// <summary>
    ///     Triggers an immediate refresh by firing the scheduled job <paramref name="scheduledJobId" />, delegating to
    ///     <see cref="IScheduledJobManagementService.TriggerNowAsync" />.
    /// </summary>
    /// <param name="useCaseOverride">Runs a specific use-case instead of the definition's baked one, validated against the fixed six-value llmfit allowlist.</param>
    /// <param name="limitOverride">Widens the recommendation breadth (<c>--limit</c>) for this run, validated to the supported <c>1..50</c> range.</param>
    /// <param name="quantOverride">Replaces the advisor's default <c>Q4_K_M</c> quant for this run.</param>
    /// <param name="ctxTarget">Overrides the context window the KV-cache fit is sized against, validated to ≥256 before it is stamped.</param>
    /// <remarks>
    ///     Only these whitelisted parameters are widened — never an image reference or command line. Each is validated
    ///     BEFORE the run: an invalid value throws and nothing fires; a <c>null</c>/empty one fires the definition's
    ///     baked value unchanged. All ride the same per-fire JobDataMap.
    /// </remarks>
    /// <exception cref="ScheduledJobValidationException">No definition has that id, its template is not
    ///     <c>model-recommendation-check</c>, an override is invalid, or the scheduler rejects a disabled, deleted, forbidden or unscheduled job.</exception>
    Task TriggerRecommendationRefreshAsync(Guid scheduledJobId,
        string? useCaseOverride = null,
        int? limitOverride = null,
        string? quantOverride = null,
        int? ctxTarget = null,
        CancellationToken cancellationToken = default);
}
