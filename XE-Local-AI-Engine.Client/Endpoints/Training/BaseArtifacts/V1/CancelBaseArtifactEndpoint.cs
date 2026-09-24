namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

public sealed class CancelBaseArtifactEndpoint : Endpoint<BaseArtifactByIdRequest, BaseArtifactResponse>
{
    private readonly IBaseArtifactService _baseArtifactService;

    public CancelBaseArtifactEndpoint(IBaseArtifactService baseArtifactService)
    {
        _baseArtifactService = baseArtifactService;
    }

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
        var artifact = await _baseArtifactService.GetAsync(request.ArtifactId, ct);
        if (artifact is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!await _baseArtifactService.CancelAsync(request.ArtifactId))
        {
            await Send.ResultAsync(BaseArtifactBlockedEndpointSupport.Blocked("not-downloading",
                "The base checkpoint download is not running."));
            return;
        }

        // The terminal transition is written by the download task; the client polls the get route for it.
        await Send.OkAsync(artifact.ToResponse(), ct);
    }
}
