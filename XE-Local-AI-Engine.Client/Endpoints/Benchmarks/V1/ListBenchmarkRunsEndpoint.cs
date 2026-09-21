namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class ListBenchmarkRunsEndpoint : Endpoint<ListBenchmarkRunsRequest, ListBenchmarkRunsResponse>
{
    private readonly BenchmarkRecordService _records;

    public ListBenchmarkRunsEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectRuns);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ListBenchmarkRunsRequest req, CancellationToken ct)
    {
        var project = await _records.GetProjectAsync(req.ProjectId, ct);
        if (project is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found.")));
            return;
        }

        var expectedKldDigest = BenchmarkEndpointSupport.ExpectedKldDigest(project);

        var page = await _records.ListRunsAsync(req.ProjectId,
                                   (req.Page - 1) * req.PageSize,
                                   req.PageSize,
                                   req.ModelContentFingerprint,
                                   req.IncludeUnscored,
                                   ct);
        await Send.OkAsync(new ListBenchmarkRunsResponse
                  {
                      Items = page.Items.Select(run => run.ToSummary(expectedKldDigest)).ToArray(),
                      Page = req.Page,
                      PageSize = req.PageSize,
                      TotalCount = page.TotalCount,
                      RankCohort = new BenchmarkRankCohortResponse
                      {
                          PolicyRevision = page.RankCohort?.PolicyRevision,
                          ExecutionKey = page.RankCohort?.ExecutionKey,
                          CohortGeneration = page.RankCohort?.CohortGeneration,
                          RankedCount = page.RankCohort?.RankedCount ?? 0,
                          TotalScored = page.RankCohort?.TotalScored ?? 0
                      }
                  }, ct);
    }
}
