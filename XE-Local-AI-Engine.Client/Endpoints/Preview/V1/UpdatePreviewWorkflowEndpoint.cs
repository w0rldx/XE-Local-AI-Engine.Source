namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using FastEndpoints;
using Microsoft.AspNetCore.Http;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     PUT <c>preview/workflows/{workflowId}</c> — validate and apply an optimistic-concurrency update. Invalid graph →
///     400, stale version → 409, unknown id → 404. Operator-gated.
/// </summary>
public sealed class UpdatePreviewWorkflowEndpoint(IPreviewWorkflowService previewWorkflowService)
    : Endpoint<UpdatePreviewWorkflowRequest, PreviewWorkflowResponse>
{
    private readonly IPreviewWorkflowService _previewWorkflowService =
        previewWorkflowService ?? throw new ArgumentNullException(nameof(previewWorkflowService));

    public override void Configure()
    {
        Put(LocalApiRoutes.Preview.WorkflowById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdatePreviewWorkflowRequest req, CancellationToken ct)
    {
        var result = await _previewWorkflowService
                          .UpdateAsync(req.WorkflowId, req.Version, req.Name, req.Graph, ct)
                          .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case PreviewWorkflowMutationOutcome.Updated:
                await Send.OkAsync(result.Detail!.ToResponse(), ct).ConfigureAwait(false);
                return;

            case PreviewWorkflowMutationOutcome.NotFound:
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;

            case PreviewWorkflowMutationOutcome.Conflict:
                await Send.ResultAsync(Results.Conflict(new { message = "The workflow was modified by another writer; reload and retry." }))
                          .ConfigureAwait(false);
                return;

            case PreviewWorkflowMutationOutcome.Invalid:
                foreach (var error in result.Validation!.Errors)
                {
                    AddError(error);
                }

                await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
                return;

            default:
                throw new InvalidOperationException($"Unhandled preview workflow update outcome '{result.Outcome}'.");
        }
    }
}
