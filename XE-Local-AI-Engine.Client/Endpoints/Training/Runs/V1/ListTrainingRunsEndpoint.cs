namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

public sealed class ListTrainingRunsEndpoint : Endpoint<ListTrainingRunsRequest, ListTrainingRunsResponse>
{
    private readonly ITrainingRunService _runs;

    public ListTrainingRunsEndpoint(ITrainingRunService runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Runs);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListTrainingRunsRequest req, CancellationToken ct)
    {
        var page = await _runs.ListAsync(new TrainingRunQuery { Page = req.Page, PageSize = req.PageSize, DatasetId = req.DatasetId }, ct);
        await Send.OkAsync(new ListTrainingRunsResponse
        {
            Items = page.Items.Select(item => item.ToResponse()).ToArray(),
            TotalCount = page.TotalCount,
            Page = req.Page,
            PageSize = req.PageSize
        }, ct);
    }
}
