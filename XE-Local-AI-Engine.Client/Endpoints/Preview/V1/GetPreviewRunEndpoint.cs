namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     GET <c>preview/runs/{runId}</c> — one run by id, live or still-replayable. A client that reloads with a runId in
///     its route calls this to decide whether to reattach (200) or drop the stale id (404). Operator-gated.
/// </summary>
public sealed class GetPreviewRunEndpoint(IPreviewWorkflowExecutionService executionService)
    : Endpoint<PreviewRunRouteRequest, PreviewRunSummaryResponse>
{
    private readonly IPreviewWorkflowExecutionService _executionService =
        executionService ?? throw new ArgumentNullException(nameof(executionService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Preview.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(PreviewRunRouteRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var run = _executionService.GetRun(req.RunId);
        if (run is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(run.ToResponse(), ct).ConfigureAwait(false);
    }
}
