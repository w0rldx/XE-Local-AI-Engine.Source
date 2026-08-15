namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

public sealed class CancelBaseArtifactEndpoint(IBaseArtifactService baseArtifactService)
    : Endpoint<BaseArtifactByIdRequest, BaseArtifactResponse>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Training.BaseArtifactCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<BaseArtifactResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status404NotFound)
                               .Produces<BaseArtifactBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(BaseArtifactByIdRequest request, CancellationToken ct)
    {
        var artifact = await baseArtifactService.GetAsync(request.ArtifactId, ct).ConfigureAwait(false);
        if (artifact is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        if (!baseArtifactService.Cancel(request.ArtifactId))
        {
            await Send.ResultAsync(Results.Conflict(new BaseArtifactBlockedResponse
            {
                Reason = "not-downloading",
                Message = "The base checkpoint download is not running."
            })).ConfigureAwait(false);
            return;
        }

        // The terminal transition is written by the download task; the client polls the get route for it.
        await Send.OkAsync(artifact.ToResponse(), ct).ConfigureAwait(false);
    }
}
