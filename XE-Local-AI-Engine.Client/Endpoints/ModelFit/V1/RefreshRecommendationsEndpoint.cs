namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     FastEndpoints handler for the manual recommendation refresh (POST model-fit/recommendations/refresh). This is a
///     facade over the scheduler trigger service: it accepts ONLY an existing scheduled-job id (never an image reference,
///     command line or template id) and the trigger self-guards that the job is a <c>model-recommendation-check</c> job.
///     The run is created asynchronously by the scheduler dispatcher; this endpoint never executes llmfit and never
///     owns run/cancellation/history state.
/// </summary>
public sealed class RefreshRecommendationsEndpoint(IModelFitRefreshTrigger modelFitRefreshTrigger)
    : Endpoint<RefreshRecommendationsRequest, RefreshRecommendationsResponse>
{
    private readonly IModelFitRefreshTrigger _modelFitRefreshTrigger = modelFitRefreshTrigger ?? throw new ArgumentNullException(nameof(modelFitRefreshTrigger));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.RecommendationsRefresh);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(RefreshRecommendationsRequest req, CancellationToken ct)
    {
        try
        {
            await _modelFitRefreshTrigger.TriggerRecommendationRefreshAsync(req.ScheduledJobId, ct).ConfigureAwait(false);
            await Send.OkAsync(new RefreshRecommendationsResponse
                {
                    ScheduledJobId = req.ScheduledJobId
                },
                ct).ConfigureAwait(false);
        }
        catch (ScheduledJobValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
