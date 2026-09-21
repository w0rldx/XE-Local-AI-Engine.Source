namespace XE_Local_AI_Engine.Client.Endpoints.Training.Datasets.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class GetTrainingDatasetEndpoint : Endpoint<GetTrainingDatasetRequest, TrainingDatasetResponse>
{
    private readonly TrainingDatasetService _datasets;

    public GetTrainingDatasetEndpoint(TrainingDatasetService datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        _datasets = datasets;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.DatasetById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetTrainingDatasetRequest req, CancellationToken ct)
    {
        var record = await _datasets.GetAsync(req.DatasetId, ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}
