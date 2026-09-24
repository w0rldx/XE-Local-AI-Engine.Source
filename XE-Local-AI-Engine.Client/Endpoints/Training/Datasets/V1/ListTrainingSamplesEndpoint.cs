namespace XE_Local_AI_Engine.Client.Endpoints.Training.Datasets.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class ListTrainingSamplesEndpoint : Endpoint<ListTrainingSamplesRequest, ListTrainingSamplesResponse>
{
    private readonly TrainingDatasetService _datasets;

    public ListTrainingSamplesEndpoint(TrainingDatasetService datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        _datasets = datasets;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.DatasetSamples);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListTrainingSamplesRequest req, CancellationToken ct)
    {
        if (req.Page < 1 || req.PageSize is < 1 or > 200)
        {
            AddError("Page must be positive and pageSize must be between 1 and 200.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var page = await _datasets.ListSamplesAsync(new TrainingSampleQuery
        {
            DatasetId = req.DatasetId,
            Page = req.Page,
            PageSize = req.PageSize,
            Label = req.Label,
            ReviewState = req.ReviewState,
            Kind = req.Kind
        }, ct);
        await Send.OkAsync(new ListTrainingSamplesResponse
        {
            Items = page.Items.Select(item => item.ToResponse()).ToArray(),
            TotalCount = page.TotalCount,
            Page = req.Page,
            PageSize = req.PageSize
        }, ct);
    }
}
