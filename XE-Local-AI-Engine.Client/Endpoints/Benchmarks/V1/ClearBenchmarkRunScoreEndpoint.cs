namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>Clears the operator override, so the run ranks by its judge score again (or not at all).</summary>
public sealed class ClearBenchmarkRunScoreEndpoint : Endpoint<ClearBenchmarkRunScoreRequest, BenchmarkRunDetailResponse>
{
    private readonly BenchmarkRecordService _records;

    public ClearBenchmarkRunScoreEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Benchmarks.RunScore);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ClearBenchmarkRunScoreRequest req, CancellationToken ct)
    {
        var run = await _records.SetUserScoreAsync(req.RunId, score: null, req.ExpectedVersion, ct);
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_records, run, ct),
                      BenchmarkEndpointSupport.ExpectedKldDigest(await _records.GetProjectAsync(run.ProjectId, ct))), ct);
    }
}
