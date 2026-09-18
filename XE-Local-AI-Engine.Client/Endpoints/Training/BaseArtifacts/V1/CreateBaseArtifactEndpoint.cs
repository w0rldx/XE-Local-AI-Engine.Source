namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

public sealed class CreateBaseArtifactEndpoint : Endpoint<CreateBaseArtifactRequest, BaseArtifactResponse>
{
    private readonly IBaseArtifactService _baseArtifactService;

    public CreateBaseArtifactEndpoint(IBaseArtifactService baseArtifactService)
    {
        _baseArtifactService = baseArtifactService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.BaseArtifacts);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<BaseArtifactResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<BaseArtifactBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateBaseArtifactRequest request, CancellationToken ct)
    {
        try
        {
            var artifact = await _baseArtifactService.StartDownloadAsync(request.RepoId, request.Revision, ct);
            await Send.OkAsync(artifact.ToResponse(), ct);
        }
        catch (BaseArtifactRejectedException exception)
        {
            // Rejection messages are operator-facing by construction (not trainable, or not enough disk).
            await Send.ResultAsync(BaseArtifactBlockedEndpointSupport.Blocked("rejected", exception.Message));
        }
    }
}
