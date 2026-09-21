namespace XE_Local_AI_Engine.Client.Endpoints.Training.Datasets.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class DeleteTrainingDatasetEndpoint : Endpoint<DeleteTrainingDatasetRequest>
{
    private readonly TrainingDatasetService _datasets;

    public DeleteTrainingDatasetEndpoint(TrainingDatasetService datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        _datasets = datasets;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.DatasetById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteTrainingDatasetRequest req, CancellationToken ct)
    {
        await _datasets.DeleteAsync(req.DatasetId, req.ExpectedVersion, ct);
        await Send.NoContentAsync(ct);
    }
}
