namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;

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
public sealed class ModelFitRefreshTrigger
{
    /// <summary>The minimum context-window target the advisor's KV-cache fit can be sized against (mirrors the handler schema).</summary>
    private const int MinCtxTarget = 256;

    private readonly IScheduledJobManagementService _scheduledJobManagementService;

    public ModelFitRefreshTrigger(IScheduledJobManagementService scheduledJobManagementService)
    {
        ArgumentNullException.ThrowIfNull(scheduledJobManagementService);
        _scheduledJobManagementService = scheduledJobManagementService;
    }

    /// <summary>
    ///     Triggers an immediate refresh by firing the scheduled job <paramref name="scheduledJobId" />, delegating to
    ///     <see cref="IScheduledJobManagementService.TriggerNowAsync" />; returns the fire id its run events carry.
    /// </summary>
    /// <param name="useCaseOverride">Runs a specific use-case instead of the definition's baked one, validated against the fixed six-value llmfit allowlist.</param>
    /// <param name="limitOverride">Widens the recommendation breadth (<c>--limit</c>) for this run, validated to the supported <c>1..50</c> range.</param>
    /// <param name="quantOverride">Replaces the advisor's default <c>Q4_K_M</c> quant for this run.</param>
    /// <param name="ctxTarget">Overrides the context window the KV-cache fit is sized against, validated to ≥256 before it is stamped.</param>
    /// <remarks>
    ///     Only these allowlisted parameters are widened — never an image reference or command line. Each is validated
    ///     BEFORE the run: an invalid value throws and nothing fires; a <c>null</c>/empty one fires the definition's
    ///     baked value unchanged. All ride the same per-fire JobDataMap.
    /// </remarks>
    /// <exception cref="ScheduledJobValidationException">No definition has that id, its template is not
    ///     <c>model-recommendation-check</c>, an override is invalid, or the scheduler rejects a disabled, deleted, forbidden or unscheduled job.</exception>
    public async Task<Guid> TriggerRecommendationRefreshAsync(Guid scheduledJobId,
        string? useCaseOverride = null,
        int? limitOverride = null,
        string? quantOverride = null,
        int? ctxTarget = null,
        CancellationToken cancellationToken = default)
    {
        var definition = await _scheduledJobManagementService.GetJobAsync(scheduledJobId, cancellationToken);

        // Template guard: a missing job already threw ScheduledJobNotFoundException (404); reject any job that is not a
        // model-recommendation-check job, so this facade can fire only model-fit refresh jobs, never another template's.
        if (!string.Equals(definition.TemplateId, ModelRecommendationCheckHandler.TemplateIdValue, StringComparison.Ordinal))
        {
            throw new ScheduledJobValidationException("The scheduled job is not a model-recommendation-check job.");
        }

        // Widen the run ONLY by the allowlisted keys, each validated exactly as the run validator would BEFORE anything fires:
        // never free text. Empty overrides fire the baked values; the rest ride the per-fire JobDataMap the dispatcher merges.
        var parameterOverrides = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(useCaseOverride))
        {
            if (!ModelFitRequestValidator.AllowedUseCases.Contains(useCaseOverride))
            {
                throw new ScheduledJobValidationException("Use case is not supported.");
            }

            parameterOverrides[SchedulerJobKeys.ModelFitUseCaseOverrideKey] = useCaseOverride;
        }

        if (limitOverride is { } limit)
        {
            if (limit is < ModelFitRequestValidator.MinLimit or > ModelFitRequestValidator.MaxLimit)
            {
                throw new ScheduledJobValidationException("Limit is out of range.");
            }

            parameterOverrides[SchedulerJobKeys.ModelFitLimitOverrideKey] = limit.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(quantOverride))
        {
            // The quant label rides the override unvalidated for content (the advisor falls back to its file selection
            // when no file matches the label); it is trimmed and never free-text-injected into a command.
            parameterOverrides[SchedulerJobKeys.ModelFitQuantOverrideKey] = quantOverride.Trim();
        }

        if (ctxTarget is { } ctx)
        {
            if (ctx < MinCtxTarget)
            {
                throw new ScheduledJobValidationException("Context target is out of range.");
            }

            parameterOverrides[SchedulerJobKeys.ModelFitCtxTargetOverrideKey] = ctx.ToString(CultureInfo.InvariantCulture);
        }

        // Delegate to the scheduler; it does its own enabled/deleted/forbidden/unscheduled validation and fires the existing
        // definition. No override supplied → pass null so the dispatcher takes the unchanged cron path and owns the run history.
        return await _scheduledJobManagementService.TriggerNowAsync(scheduledJobId,
            parameterOverrides.Count == 0 ? null : parameterOverrides,
            cancellationToken);
    }
}
