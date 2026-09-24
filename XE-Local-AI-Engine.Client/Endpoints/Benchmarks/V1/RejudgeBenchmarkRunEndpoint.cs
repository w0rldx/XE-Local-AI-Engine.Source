namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>Judges one succeeded run again under the project's current policy.</summary>
public sealed class RejudgeBenchmarkRunEndpoint : Endpoint<RejudgeBenchmarkRunRequest, BenchmarkRunDetailResponse>
{
    private readonly IBenchmarkProjectService _projects;
    private readonly BenchmarkRecordService _records;

    public RejudgeBenchmarkRunEndpoint(IBenchmarkProjectService projects, BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(records);
        _projects = projects;
        _records = records;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.RunRejudge);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(RejudgeBenchmarkRunRequest req, CancellationToken ct)
    {
        _ = await _projects.RejudgeRunAsync(req.RunId, req.ExpectedVersion, req.Force, ct);
        var run = await _records.GetRunAsync(req.RunId, ct)
                  ?? throw new BenchmarkNotFoundException("Benchmark run was not found.");
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_records, run, ct),
            BenchmarkEndpointSupport.ExpectedKldDigest(await _records.GetProjectAsync(run.ProjectId, ct))), ct);
    }
}
