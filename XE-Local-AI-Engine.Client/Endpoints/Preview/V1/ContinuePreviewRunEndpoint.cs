namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     POST <c>preview/runs/{runId}/continue</c> — resume a Paused run. 404 unknown/expired, 409 wrong state, 202
///     accepted. Operator-gated.
/// </summary>
public sealed class ContinuePreviewRunEndpoint(IPreviewWorkflowExecutionService executionService)
    : Endpoint<PreviewRunRouteRequest>
{
    private readonly IPreviewWorkflowExecutionService _executionService =
        executionService ?? throw new ArgumentNullException(nameof(executionService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Preview.RunContinue);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: RunId comes from the route, so a well-behaved client sends no body — and therefore no
        // Content-Type. The default POST "Accepts" metadata only allows application/json, which FastEndpoints answers
        // with 415 when the header is absent. Overriding Accepts to accept any content-type lets a body-less request
        // through (the runId still binds from the route).
        Description(x => x.Accepts<PreviewRunRouteRequest>()
                          .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(PreviewRunRouteRequest req, CancellationToken ct)
    {
        var outcome = await _executionService.ContinueAsync(req.RunId, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case PreviewRunCommandOutcome.Accepted:
                await Send.ResultAsync(Results.Accepted()).ConfigureAwait(false);
                return;

            case PreviewRunCommandOutcome.NotFound:
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;

            case PreviewRunCommandOutcome.WrongState:
                await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict,
                              detail: "The run is not paused and cannot be continued."))
                          .ConfigureAwait(false);
                return;

            default:
                throw new InvalidOperationException($"Unhandled continue outcome '{outcome}'.");
        }
    }
}
