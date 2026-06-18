namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     FastEndpoints handler for the manual recommendation refresh (POST model-fit/recommendations/refresh). This is a
///     facade over the scheduler trigger service: it accepts ONLY an existing scheduled-job id (never an image reference,
///     command line or template id) and the trigger self-guards that the job is a <c>model-recommendation-check</c> job.
///     The optional <c>useCase</c>/<c>limit</c>/<c>quantOverride</c>/<c>ctxTarget</c> are validated here BEFORE anything
///     fires and ride the per-fire override map (the approved-image + provider-name params are gone — non-additive,
///     plan §8). The run is created asynchronously by the scheduler dispatcher; this endpoint never executes the advisor
///     and never owns run/cancellation/history state.
/// </summary>
public sealed class RefreshRecommendationsEndpoint(IModelFitRefreshTrigger modelFitRefreshTrigger)
    : Endpoint<RefreshRecommendationsRequest, RefreshRecommendationsResponse>
{
    /// <summary>The minimum context-window target the advisor's KV-cache fit can be sized against (mirrors the handler schema floor).</summary>
    private const int MinCtxTarget = 256;

    private readonly IModelFitRefreshTrigger _modelFitRefreshTrigger = modelFitRefreshTrigger ?? throw new ArgumentNullException(nameof(modelFitRefreshTrigger));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.RecommendationsRefresh);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(RefreshRecommendationsRequest req, CancellationToken ct)
    {
        // Reject an unsupported use-case override with a 400 BEFORE anything fires. The override is widened to a
        // validated enum only (the same six llmfit-supported values the run validator enforces) — never free text. An
        // empty/null UseCase is a no-override refresh (back-compat). The trigger re-checks this as a defense-in-depth
        // boundary, but failing fast here keeps an invalid request from reaching the scheduler at all.
        if (!string.IsNullOrWhiteSpace(req.UseCase) && !ModelFitRequestValidator.AllowedUseCases.Contains(req.UseCase))
        {
            AddError("Use case is not supported.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        // Reject an out-of-range breadth override with a 400 BEFORE anything fires. Like the use-case, the limit is
        // bounded to the same 1..50 the run validator (and the handler's JSON schema) enforces. Null = baked limit.
        if (req.Limit is { } limit && limit is < ModelFitRequestValidator.MinLimit or > ModelFitRequestValidator.MaxLimit)
        {
            AddError("Limit is out of range.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        // Reject an out-of-range context-window target with a 400 BEFORE anything fires (mirrors the handler schema's
        // ≥256 floor). The quant override carries no range to bound — it is a label the advisor matches against the
        // repo's files (falling back to file selection when none matches), never free text into a command.
        if (req.CtxTarget is { } ctxTarget && ctxTarget < MinCtxTarget)
        {
            AddError("Context target is out of range.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await _modelFitRefreshTrigger
                  .TriggerRecommendationRefreshAsync(req.ScheduledJobId, req.UseCase, req.Limit, req.QuantOverride, req.CtxTarget, ct)
                  .ConfigureAwait(false);
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
