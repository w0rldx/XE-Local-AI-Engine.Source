namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class StartModelLabCheckpointDownloadEndpoint : Endpoint<ModelLabCheckpointDownloadRequest, ModelLabCheckpointDownloadResponse>
{
    private readonly ModelLabExperimentService _experiments;

    public StartModelLabCheckpointDownloadEndpoint(ModelLabExperimentService experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        _experiments = experiments;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelLab.CheckpointDownload);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabCheckpointDownloadResponse>(StatusCodes.Status200OK)
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(ModelLabCheckpointDownloadRequest request, CancellationToken ct)
    {
        var result = await _experiments.StartCheckpointDownloadAsync(request.RepoId, request.Revision, request.InstalledModelName, request.LicenseConfirmed, ct);
        await Send.OkAsync(new ModelLabCheckpointDownloadResponse
        {
            Preflight = ModelLabEndpointMapper.ToResponse(result.Preflight),
            BaseArtifactId = result.BaseArtifactId,
            ArtifactStatus = result.ArtifactStatus
        }, ct);
    }
}
