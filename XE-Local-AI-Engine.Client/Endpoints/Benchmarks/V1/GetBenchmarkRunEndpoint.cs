namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class GetBenchmarkRunEndpoint : Endpoint<BenchmarkRunRouteRequest, BenchmarkRunDetailResponse>
{
    private readonly BenchmarkRecordService _records;

    public GetBenchmarkRunEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BenchmarkRunRouteRequest req, CancellationToken ct)
    {
        var run = await _records.GetRunAsync(req.RunId, ct);
        if (run is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark run was not found.")));
            return;
        }

        // A detail response is the only place the verdict is decrypted: a list of runs must not decrypt one blob per row.
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_records, run, ct),
            BenchmarkEndpointSupport.ExpectedKldDigest(await _records.GetProjectAsync(run.ProjectId, ct))), ct);
    }
}
