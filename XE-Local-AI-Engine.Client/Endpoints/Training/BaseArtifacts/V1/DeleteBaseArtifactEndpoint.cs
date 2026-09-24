namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

public sealed class DeleteBaseArtifactEndpoint : Endpoint<BaseArtifactByIdRequest>
{
    private readonly IBaseArtifactService _baseArtifactService;

    public DeleteBaseArtifactEndpoint(IBaseArtifactService baseArtifactService)
    {
        _baseArtifactService = baseArtifactService;
    }

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
        var outcome = await _baseArtifactService.DeleteAsync(request.ArtifactId, ct);
        switch (outcome)
        {
            case BaseArtifactDeleteOutcome.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case BaseArtifactDeleteOutcome.Downloading:
                await Send.ResultAsync(BaseArtifactBlockedEndpointSupport.Blocked("downloading",
                    "The base checkpoint is still downloading. Cancel the download before deleting it."));
                return;
            case BaseArtifactDeleteOutcome.Deleted:
                await Send.NoContentAsync(ct);
                return;
            default:
                throw new InvalidOperationException($"Unknown base artifact delete outcome: {outcome}.");
        }
    }
}
