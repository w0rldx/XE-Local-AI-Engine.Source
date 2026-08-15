namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

public sealed class DeleteBaseArtifactEndpoint(IBaseArtifactService baseArtifactService) : Endpoint<BaseArtifactByIdRequest>
{
    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.BaseArtifactById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces(StatusCodes.Status204NoContent)
                               .ProducesProblemFE(StatusCodes.Status404NotFound)
                               .Produces<BaseArtifactBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(BaseArtifactByIdRequest request, CancellationToken ct)
    {
        var outcome = await baseArtifactService.DeleteAsync(request.ArtifactId, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case BaseArtifactDeleteOutcome.NotFound:
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;
            case BaseArtifactDeleteOutcome.Downloading:
                await Send.ResultAsync(Results.Conflict(new BaseArtifactBlockedResponse
                {
                    Reason = "downloading",
                    Message = "The base checkpoint is still downloading. Cancel the download before deleting it."
                })).ConfigureAwait(false);
                return;
            case BaseArtifactDeleteOutcome.Deleted:
                await Send.NoContentAsync(ct).ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException($"Unknown base artifact delete outcome: {outcome}.");
        }
    }
}
