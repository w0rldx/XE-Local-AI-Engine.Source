namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

/// <summary>The review inbox: reviewable runs in one queue state, across projects or for one.</summary>
public sealed class ListBenchmarkReviewsEndpoint : Endpoint<ListBenchmarkReviewsRequest, ListBenchmarkReviewsResponse>
{
    private readonly BenchmarkRecordService _records;

    public ListBenchmarkReviewsEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.Reviews);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(ListBenchmarkReviewsRequest req, CancellationToken ct)
    {
        var page = await _records.ListReviewQueueAsync(new BenchmarkReviewQuery
        {
            State = req.State,
            ProjectId = req.ProjectId,
            Skip = (req.Page - 1) * req.Take,
            Take = req.Take
        }, ct);
        await Send.OkAsync(new ListBenchmarkReviewsResponse
        {
            Items = [.. page.Items.Select(static entry => new BenchmarkReviewQueueItemResponse
            {
                RunId = entry.RunId,
                ProjectId = entry.ProjectId,
                RunVersion = entry.RunVersion,
                PrimaryModelName = entry.PrimaryModelName,
                TaskItemId = entry.TaskItemId,
                TaskItemIndex = entry.TaskItemIndex,
                PrimaryCompletedAtUtc = entry.PrimaryCompletedAtUtc,
                State = entry.State,
                ReviewScore = entry.ReviewScore,
                ReviewedAtUtc = entry.ReviewedAtUtc
            })],
            TotalCount = page.TotalCount,
            Page = req.Page,
            Take = req.Take
        }, ct);
    }
}
