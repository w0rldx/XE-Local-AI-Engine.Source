namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

/// <summary>
///     The polling surface for a download in flight: the response carries live byte progress, following the model and
///     image download lanes rather than adding a third hub for a transfer that already has a status route.
/// </summary>
public sealed class GetBaseArtifactEndpoint : Endpoint<BaseArtifactByIdRequest, BaseArtifactResponse>
{
    private readonly IBaseArtifactService _baseArtifactService;

    public GetBaseArtifactEndpoint(IBaseArtifactService baseArtifactService)
    {
        _baseArtifactService = baseArtifactService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.BaseArtifactById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<BaseArtifactResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BaseArtifactByIdRequest request, CancellationToken ct)
    {
        var artifact = await _baseArtifactService.GetAsync(request.ArtifactId, ct);
        if (artifact is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(artifact.ToResponse(), ct);
    }
}
