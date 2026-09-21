namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>Changes the judge on a project that may already be frozen.</summary>
/// <remarks>
///     The judge is the one frozen-project knob an operator can still turn, and turning it re-scores every run — so it
///     is its own resource with its own confirmation, never a field that rides along on the project PUT.
/// </remarks>
public sealed class UpdateBenchmarkJudgePolicyEndpoint : Endpoint<UpdateBenchmarkJudgePolicyRequest, BenchmarkJudgeChangeResponse>
{
    private readonly IBenchmarkProjectService _projects;
    private readonly BenchmarkRecordService _records;

    public UpdateBenchmarkJudgePolicyEndpoint(IBenchmarkProjectService projects, BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(records);
        _projects = projects;
        _records = records;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Benchmarks.ProjectJudge);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict)
                                      .ProducesProblem(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(UpdateBenchmarkJudgePolicyRequest req, CancellationToken ct)
    {
        var draft = req.Policy is null
            ? null
            : new BenchmarkJudgePolicyDraft
            {
                ModelName = req.Policy.ModelName,
                ContextTokens = req.Policy.ContextTokens,
                Rubric = req.Policy.Rubric.ToRubric(),
                ReferenceAnswer = req.Policy.ReferenceAnswer,
                Mode = req.Policy.Mode
            };
        var change = await _projects.UpdateJudgePolicyAsync(req.ProjectId, req.ExpectedVersion, draft, req.ConfirmRejudge, ct);
        await Send.OkAsync(await ToResponseAsync(_records, change, ct), ct);
    }

    internal static async Task<BenchmarkJudgeChangeResponse> ToResponseAsync(BenchmarkRecordService records,
        BenchmarkJudgePolicyChange change,
        CancellationToken ct)
    {
        var runCount = await records.CountRunsAsync(change.Project.Id, ct);
        return new BenchmarkJudgeChangeResponse
        {
            Project = await BenchmarkProjectDetailProjection.ReadAsync(records, change.Project, runCount, ct),
            EnqueuedRunIds = change.EnqueuedRunIds,
            CohortGeneration = change.CohortGeneration
        };
    }
}
