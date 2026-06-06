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
                await Send.ResultAsync(Results.Conflict(new { message = "The run is not paused and cannot be continued." }))
                          .ConfigureAwait(false);
                return;

            default:
                throw new InvalidOperationException($"Unhandled continue outcome '{outcome}'.");
        }
    }
}
