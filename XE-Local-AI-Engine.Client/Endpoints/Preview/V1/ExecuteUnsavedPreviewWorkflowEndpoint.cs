namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     POST <c>preview/runs/execute</c> — start a run from an inline (unsaved) graph. Persists NOTHING. Invalid graph →
///     400, concurrent-run cap → 409. Returns the new run id. Operator-gated.
/// </summary>
public sealed class ExecuteUnsavedPreviewWorkflowEndpoint(IPreviewWorkflowExecutionService executionService)
    : Endpoint<ExecuteUnsavedPreviewWorkflowRequest, PreviewRunStartedResponse>
{
    private readonly IPreviewWorkflowExecutionService _executionService =
        executionService ?? throw new ArgumentNullException(nameof(executionService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Preview.RunExecute);
        Policies(NodeAuthorizationPolicies.Operator);
        // 409 = a run-cap rejection written by the global ConflictExceptionHandler (conflictType =
        // PreviewWorkflowCapReached / PreviewWorkflowModelCapExceeded).
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(ExecuteUnsavedPreviewWorkflowRequest req, CancellationToken ct)
    {
        var connectionId = PreviewExecuteHelper.ResolveConnectionId(HttpContext);

        try
        {
            var runId = await _executionService.StartAsync(req.Graph, connectionId, ct).ConfigureAwait(false);
            await Send.OkAsync(new PreviewRunStartedResponse(runId), ct).ConfigureAwait(false);
        }
        catch (PreviewWorkflowValidationException exception)
        {
            foreach (var error in exception.Result.Errors)
            {
                AddError(error);
            }

            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
