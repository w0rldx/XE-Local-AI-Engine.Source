namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     POST <c>preview/workflows/{workflowId}/execute</c> — start a run from the saved graph. Invalid graph → 400,
///     unknown id → 404, concurrent-run cap → 409. Returns the new run id. Operator-gated.
/// </summary>
public sealed class ExecuteSavedPreviewWorkflowEndpoint(
    IPreviewWorkflowService previewWorkflowService,
    IPreviewWorkflowExecutionService executionService)
    : Endpoint<PreviewWorkflowRouteRequest, PreviewRunStartedResponse>
{
    private readonly IPreviewWorkflowExecutionService _executionService =
        executionService ?? throw new ArgumentNullException(nameof(executionService));

    private readonly IPreviewWorkflowService _previewWorkflowService =
        previewWorkflowService ?? throw new ArgumentNullException(nameof(previewWorkflowService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Preview.WorkflowExecute);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: WorkflowId comes from the route, so a well-behaved client sends no body — and therefore no
        // Content-Type. The default POST "Accepts" metadata only allows application/json, which FastEndpoints answers
        // with 415 when the header is absent. Overriding Accepts to accept any content-type lets a body-less request
        // through (the workflowId still binds from the route).
        // 409 = a run-cap rejection written by the global ConflictExceptionHandler (conflictType =
        // PreviewWorkflowCapReached / PreviewWorkflowModelCapExceeded).
        Description(x => x.Accepts<PreviewWorkflowRouteRequest>()
                          .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(PreviewWorkflowRouteRequest req, CancellationToken ct)
    {
        var detail = await _previewWorkflowService.GetAsync(req.WorkflowId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        var connectionId = PreviewExecuteHelper.ResolveConnectionId(HttpContext);

        try
        {
            var runId = await _executionService.StartAsync(detail.Graph, connectionId, ct).ConfigureAwait(false);
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
