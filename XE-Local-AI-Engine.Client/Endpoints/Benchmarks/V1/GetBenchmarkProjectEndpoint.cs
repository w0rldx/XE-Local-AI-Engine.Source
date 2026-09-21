namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class GetBenchmarkProjectEndpoint : Endpoint<BenchmarkProjectRouteRequest, BenchmarkProjectDetailResponse>
{
    private readonly BenchmarkRecordService _records;

    public GetBenchmarkProjectEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BenchmarkProjectRouteRequest req, CancellationToken ct)
    {
        var project = await _records.GetProjectAsync(req.ProjectId, ct);
        if (project is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found.")));
            return;
        }

        var runCount = await _records.CountRunsAsync(project.Id, ct);
        await Send.OkAsync(await BenchmarkProjectDetailProjection.ReadAsync(_records, project, runCount, ct), ct);
    }
}
