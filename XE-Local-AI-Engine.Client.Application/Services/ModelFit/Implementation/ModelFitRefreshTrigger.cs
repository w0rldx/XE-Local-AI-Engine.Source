namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;

/// <summary>
///     Default <see cref="IModelFitRefreshTrigger" />: loads the scheduled job definition, guards that it is a
///     <c>model-recommendation-check</c> job, then delegates firing to the scheduler management service. It never runs
///     llmfit — the scheduler dispatcher and the Marker 3 handler own the run.
/// </summary>
public sealed class ModelFitRefreshTrigger(IScheduledJobManagementService scheduledJobManagementService) : IModelFitRefreshTrigger
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService = scheduledJobManagementService ?? throw new ArgumentNullException(nameof(scheduledJobManagementService));

    public async Task TriggerRecommendationRefreshAsync(Guid scheduledJobId, CancellationToken cancellationToken = default)
    {
        var definition = await _scheduledJobManagementService.GetJobAsync(scheduledJobId, cancellationToken).ConfigureAwait(false);

        // Template guard: reject a missing job and any job that is not a model-recommendation-check job, so this facade
        // can fire only model-fit refresh jobs — never an arbitrary scheduled job of another template.
        if (definition is null
            || !string.Equals(definition.TemplateId, ModelRecommendationCheckHandler.TemplateIdValue, StringComparison.Ordinal))
        {
            throw new ScheduledJobValidationException("The scheduled job is not a model-recommendation-check job.");
        }

        // Delegate to the scheduler; it performs its own enabled/deleted/forbidden/unscheduled validation and fires the
        // existing definition. The dispatcher → Marker 3 handler does the work and owns the run history.
        await _scheduledJobManagementService.TriggerNowAsync(scheduledJobId, cancellationToken).ConfigureAwait(false);
    }
}
