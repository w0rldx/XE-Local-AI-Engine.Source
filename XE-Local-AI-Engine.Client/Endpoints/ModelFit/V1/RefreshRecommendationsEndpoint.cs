namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;

/// <summary>
///     FastEndpoints handler for the manual recommendation refresh (POST model-fit/recommendations/refresh), a facade
///     over the scheduler trigger service.
/// </summary>
/// <remarks>
///     It accepts ONLY an existing scheduled-job id — never an image reference, command line or template id, and there
///     is no approved-image or provider-name param — and the trigger self-guards that the job is a
///     <c>model-recommendation-check</c> job. The optional <c>useCase</c>/<c>limit</c>/<c>quantOverride</c>/
///     <c>ctxTarget</c> are validated here BEFORE anything fires and ride the per-fire override map. The scheduler
///     dispatcher creates the run; this endpoint never executes the advisor nor owns run/cancellation/history state.
/// </remarks>
public sealed class RefreshRecommendationsEndpoint : Endpoint<RefreshRecommendationsRequest, RefreshRecommendationsResponse>
{
    /// <summary>The minimum context-window target the advisor's KV-cache fit can be sized against (mirrors the handler schema floor).</summary>
    private const int MinCtxTarget = 256;

    private readonly IModelFitRefreshTrigger _modelFitRefreshTrigger;

    public RefreshRecommendationsEndpoint(IModelFitRefreshTrigger modelFitRefreshTrigger)
    {
        ArgumentNullException.ThrowIfNull(modelFitRefreshTrigger);
        _modelFitRefreshTrigger = modelFitRefreshTrigger;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.RecommendationsRefresh);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(RefreshRecommendationsRequest req, CancellationToken ct)
    {
        // Reject an unsupported use-case override with a 400 BEFORE anything fires: the override widens to a validated enum only (the same six llmfit-supported
        // values the run validator enforces), never free text; an empty/null UseCase is a no-override refresh. The trigger re-checks it as a defence-in-depth boundary.
        if (!string.IsNullOrWhiteSpace(req.UseCase) && !ModelFitRequestValidator.AllowedUseCases.Contains(req.UseCase))
        {
            AddError("Use case is not supported.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // Reject an out-of-range breadth override with a 400 BEFORE anything fires. Like the use-case, the limit is
        // bounded to the same 1..50 the run validator (and the handler's JSON schema) enforces. Null = baked limit.
        if (req.Limit is { } limit && limit is < ModelFitRequestValidator.MinLimit or > ModelFitRequestValidator.MaxLimit)
        {
            AddError("Limit is out of range.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // Reject an out-of-range context-window target with a 400 BEFORE anything fires (mirrors the handler schema's ≥256 floor). The quant override carries no
        // range to bound: it is a label the advisor matches against the repo's files, falling back to file selection when none matches, never free text into a command.
        if (req.CtxTarget is { } ctxTarget && ctxTarget < MinCtxTarget)
        {
            AddError("Context target is out of range.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        await _modelFitRefreshTrigger
            .TriggerRecommendationRefreshAsync(req.ScheduledJobId, req.UseCase, req.Limit, req.QuantOverride, req.CtxTarget, ct);
        await Send.OkAsync(new RefreshRecommendationsResponse
            {
                ScheduledJobId = req.ScheduledJobId
            },
            ct);
    }
}
