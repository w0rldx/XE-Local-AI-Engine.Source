namespace XE_Local_AI_Engine.Client.Endpoints.Training.Datasets.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class ListTrainingDatasetsEndpoint : EndpointWithoutRequest<ListTrainingDatasetsResponse>
{
    private readonly TrainingDatasetService _datasets;

    public ListTrainingDatasetsEndpoint(TrainingDatasetService datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        _datasets = datasets;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Datasets);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var records = await _datasets.ListAsync(ct);
        await Send.OkAsync(new ListTrainingDatasetsResponse
        {
            Items = records.Select(record => record.ToResponse()).ToArray()
        }, ct);
    }
}
