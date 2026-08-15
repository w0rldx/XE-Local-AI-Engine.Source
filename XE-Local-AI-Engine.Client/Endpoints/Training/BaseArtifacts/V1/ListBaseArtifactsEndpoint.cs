namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

public sealed class ListBaseArtifactsEndpoint(IBaseArtifactService baseArtifactService)
    : EndpointWithoutRequest<BaseArtifactListResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Training.BaseArtifacts);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<BaseArtifactListResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var artifacts = await baseArtifactService.ListAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new BaseArtifactListResponse
        {
            Items = artifacts.Select(artifact => artifact.ToResponse()).ToArray()
        }, ct).ConfigureAwait(false);
    }
}
