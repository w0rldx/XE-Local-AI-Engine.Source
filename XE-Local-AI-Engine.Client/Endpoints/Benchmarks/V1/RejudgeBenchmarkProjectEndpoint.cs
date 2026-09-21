namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>Moves the project's rank cohort to the current judge runtime by re-judging every succeeded run.</summary>
public sealed class RejudgeBenchmarkProjectEndpoint : Endpoint<RejudgeBenchmarkProjectRequest, BenchmarkJudgeChangeResponse>
{
    private readonly IBenchmarkProjectService _projects;
    private readonly BenchmarkRecordService _records;

    public RejudgeBenchmarkProjectEndpoint(IBenchmarkProjectService projects, BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(records);
        _projects = projects;
        _records = records;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.ProjectRejudge);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(RejudgeBenchmarkProjectRequest req, CancellationToken ct)
    {
        var change = await _projects.RejudgeProjectAsync(req.ProjectId, req.ExpectedVersion, ct);
        await Send.OkAsync(await UpdateBenchmarkJudgePolicyEndpoint.ToResponseAsync(_records, change, ct), ct);
    }
}
