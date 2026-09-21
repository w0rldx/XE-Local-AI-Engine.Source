namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Export;

public sealed class GetTrainingArtifactEndpoint : Endpoint<TrainingArtifactByIdRequest, TrainingArtifactResponse>
{
    private readonly ITrainingExportService _exports;

    public GetTrainingArtifactEndpoint(ITrainingExportService exports)
    {
        ArgumentNullException.ThrowIfNull(exports);
        _exports = exports;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.ArtifactById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(TrainingArtifactByIdRequest req, CancellationToken ct)
    {
        var artifact = await _exports.GetArtifactAsync(req.ArtifactId, ct);
        if (artifact is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(artifact.ToResponse(), ct);
    }
}
