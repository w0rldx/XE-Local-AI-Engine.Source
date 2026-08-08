namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     POST <c>preview/runs/cancel-all</c> — cancel every live run and report how many were cancelled. The operator's
///     recovery path when runs have leaked their concurrency slots: without it the only way back from a
///     <c>CapReached</c> 409 on an unreachable run was a node restart. Idempotent (0 when nothing is running).
///     Operator-gated.
/// </summary>
public sealed class CancelAllPreviewRunsEndpoint(IPreviewWorkflowExecutionService executionService)
    : EndpointWithoutRequest<CancelAllPreviewRunsResponse>
{
    private readonly IPreviewWorkflowExecutionService _executionService =
        executionService ?? throw new ArgumentNullException(nameof(executionService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Preview.RunsCancelAll);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var cancelled = await _executionService.CancelAllAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new CancelAllPreviewRunsResponse(cancelled), ct).ConfigureAwait(false);
    }
}
