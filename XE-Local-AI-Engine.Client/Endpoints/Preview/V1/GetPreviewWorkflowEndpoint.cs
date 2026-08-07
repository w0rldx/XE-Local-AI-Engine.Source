namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Preview.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>GET <c>preview/workflows/{workflowId}</c> — the full workflow graph. Operator-gated.</summary>
public sealed class GetPreviewWorkflowEndpoint(IPreviewWorkflowService previewWorkflowService)
    : Endpoint<PreviewWorkflowRouteRequest, PreviewWorkflowResponse>
{
    private readonly IPreviewWorkflowService _previewWorkflowService =
        previewWorkflowService ?? throw new ArgumentNullException(nameof(previewWorkflowService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Preview.WorkflowById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(PreviewWorkflowRouteRequest req, CancellationToken ct)
    {
        var detail = await _previewWorkflowService.GetAsync(req.WorkflowId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(detail.ToResponse(), ct).ConfigureAwait(false);
    }
}
