namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Export;

public sealed class ListTrainingArtifactsEndpoint : Endpoint<TrainingRunArtifactsRequest, ListTrainingArtifactsResponse>
{
    private readonly ITrainingExportService _exports;

    public ListTrainingArtifactsEndpoint(ITrainingExportService exports)
    {
        ArgumentNullException.ThrowIfNull(exports);
        _exports = exports;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.RunArtifacts);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(TrainingRunArtifactsRequest req, CancellationToken ct)
    {
        var artifacts = await _exports.ListArtifactsAsync(req.RunId, ct);
        await Send.OkAsync(new ListTrainingArtifactsResponse
        {
            Items = artifacts.Select(item => item.ToResponse()).ToArray()
        }, ct);
    }
}
