namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;

/// <summary>
///     Default <see cref="IModelFitRefreshTrigger" />: loads the scheduled job definition, guards that it is a
///     <c>model-recommendation-check</c> job, then delegates firing to the scheduler management service. It never runs
///     llmfit — the scheduler dispatcher and the model-fit handler own the run.
/// </summary>
public sealed class ModelFitRefreshTrigger(IScheduledJobManagementService scheduledJobManagementService) : IModelFitRefreshTrigger
{
    /// <summary>The minimum context-window target the advisor's KV-cache fit can be sized against (mirrors the handler schema).</summary>
    private const int MinCtxTarget = 256;

    private readonly IScheduledJobManagementService _scheduledJobManagementService = scheduledJobManagementService ?? throw new ArgumentNullException(nameof(scheduledJobManagementService));

    public async Task TriggerRecommendationRefreshAsync(Guid scheduledJobId,
        string? useCaseOverride = null,
        int? limitOverride = null,
        string? quantOverride = null,
        int? ctxTarget = null,
        CancellationToken cancellationToken = default)
    {
        var definition = await _scheduledJobManagementService.GetJobAsync(scheduledJobId, cancellationToken).ConfigureAwait(false);

        // Template guard: reject a missing job and any job that is not a model-recommendation-check job, so this facade
        // can fire only model-fit refresh jobs — never an arbitrary scheduled job of another template.
        if (definition is null
            || !string.Equals(definition.TemplateId, ModelRecommendationCheckHandler.TemplateIdValue, StringComparison.Ordinal))
        {
            throw new ScheduledJobValidationException("The scheduled job is not a model-recommendation-check job.");
        }

        // Widen the run ONLY by the whitelisted keys, each validated exactly as the run validator would BEFORE anything
        // fires — never free text into the run. Empty/null overrides fire the definition's baked values unchanged
        // (back-compat). The overrides ride the per-fire JobDataMap; the dispatcher merges only these whitelisted keys
        // over the stored parameters.
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

        // Delegate to the scheduler; it performs its own enabled/deleted/forbidden/unscheduled validation and fires the
        // existing definition. No override supplied → pass null so the dispatcher takes the unchanged cron/back-compat
        // path. The dispatcher → model-fit handler does the work and owns the run history.
        await _scheduledJobManagementService.TriggerNowAsync(scheduledJobId,
            parameterOverrides.Count == 0 ? null : parameterOverrides,
            cancellationToken).ConfigureAwait(false);
    }
}
