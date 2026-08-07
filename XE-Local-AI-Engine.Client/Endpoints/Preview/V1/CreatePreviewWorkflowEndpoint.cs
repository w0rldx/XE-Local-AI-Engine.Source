namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Preview.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>POST <c>preview/workflows</c> — validate and persist a new workflow. Invalid graph → 400. Operator-gated.</summary>
public sealed class CreatePreviewWorkflowEndpoint(IPreviewWorkflowService previewWorkflowService)
    : Endpoint<CreatePreviewWorkflowRequest, PreviewWorkflowResponse>
{
    private readonly IPreviewWorkflowService _previewWorkflowService =
        previewWorkflowService ?? throw new ArgumentNullException(nameof(previewWorkflowService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Preview.Workflows);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreatePreviewWorkflowRequest req, CancellationToken ct)
    {
        var result = await _previewWorkflowService.CreateAsync(req.Name, req.Graph, ct).ConfigureAwait(false);
        if (result.Outcome == PreviewWorkflowMutationOutcome.Invalid)
        {
            foreach (var error in result.Validation!.Errors)
            {
                AddError(error);
            }

            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(result.Detail!.ToResponse(), ct).ConfigureAwait(false);
    }
}
