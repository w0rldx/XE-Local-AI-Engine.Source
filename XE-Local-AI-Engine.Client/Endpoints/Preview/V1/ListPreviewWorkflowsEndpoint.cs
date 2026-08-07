namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Preview.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>GET <c>preview/workflows</c> — workflow summaries (no graph). Operator-gated.</summary>
public sealed class ListPreviewWorkflowsEndpoint(IPreviewWorkflowService previewWorkflowService)
    : EndpointWithoutRequest<ListPreviewWorkflowsResponse>
{
    private readonly IPreviewWorkflowService _previewWorkflowService =
        previewWorkflowService ?? throw new ArgumentNullException(nameof(previewWorkflowService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Preview.Workflows);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var summaries = await _previewWorkflowService.ListAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListPreviewWorkflowsResponse
            {
                Items = [.. summaries.Select(static s => s.ToResponse())]
            },
            ct).ConfigureAwait(false);
    }
}
