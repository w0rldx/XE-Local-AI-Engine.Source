namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

public sealed class PreflightModelLabCheckpointEndpoint : Endpoint<ModelLabCheckpointPreflightRequest, ModelLabCheckpointPreflightResponse>
{
    private readonly IBaseArtifactService _baseArtifacts;
    private readonly ModelLabCheckpointPreflightService _preflight;

    public PreflightModelLabCheckpointEndpoint(ModelLabCheckpointPreflightService preflight, IBaseArtifactService baseArtifacts)
    {
        ArgumentNullException.ThrowIfNull(baseArtifacts);
        ArgumentNullException.ThrowIfNull(preflight);
        _baseArtifacts = baseArtifacts;
        _preflight = preflight;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelLab.CheckpointPreflight);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabCheckpointPreflightResponse>(StatusCodes.Status200OK)
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                               .Produces<TrainingErrorResponse>(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ModelLabCheckpointPreflightRequest request, CancellationToken ct)
    {
        var result = request.BaseArtifactId is { } artifactId
            ? await _preflight.CheckDownloadedAsync(await _baseArtifacts.GetAsync(artifactId, ct), request.InstalledModelName, ct)
            : await _preflight.CheckAsync(request.RepoId!, request.Revision, request.InstalledModelName, ct);
        await Send.OkAsync(ModelLabEndpointMapper.ToResponse(result), ct);
    }
}
