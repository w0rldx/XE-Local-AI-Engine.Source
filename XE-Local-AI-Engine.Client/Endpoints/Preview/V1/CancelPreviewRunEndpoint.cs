namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     POST <c>preview/runs/{runId}/cancel</c> — cancel a run. Idempotent; works while Running OR Paused. 404 unknown,
///     202 accepted. Operator-gated.
/// </summary>
public sealed class CancelPreviewRunEndpoint(IPreviewWorkflowExecutionService executionService)
    : Endpoint<PreviewRunRouteRequest>
{
    private readonly IPreviewWorkflowExecutionService _executionService =
        executionService ?? throw new ArgumentNullException(nameof(executionService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Preview.RunCancel);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(PreviewRunRouteRequest req, CancellationToken ct)
    {
        var outcome = await _executionService.CancelAsync(req.RunId, ct).ConfigureAwait(false);

        // Idempotent: an unknown/expired run is reported as 404 but never an error; a recorded cancel is 202.
        if (outcome == PreviewRunCommandOutcome.NotFound)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.ResultAsync(Results.Accepted()).ConfigureAwait(false);
    }
}
