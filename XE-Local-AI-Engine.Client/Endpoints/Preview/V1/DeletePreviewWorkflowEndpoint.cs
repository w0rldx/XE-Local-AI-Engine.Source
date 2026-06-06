namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>DELETE <c>preview/workflows/{workflowId}</c>. 204 on delete, 404 when absent. Operator-gated.</summary>
public sealed class DeletePreviewWorkflowEndpoint(IPreviewWorkflowService previewWorkflowService)
    : Endpoint<PreviewWorkflowRouteRequest>
{
    private readonly IPreviewWorkflowService _previewWorkflowService =
        previewWorkflowService ?? throw new ArgumentNullException(nameof(previewWorkflowService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Preview.WorkflowById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(PreviewWorkflowRouteRequest req, CancellationToken ct)
    {
        var deleted = await _previewWorkflowService.DeleteAsync(req.WorkflowId, ct).ConfigureAwait(false);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
