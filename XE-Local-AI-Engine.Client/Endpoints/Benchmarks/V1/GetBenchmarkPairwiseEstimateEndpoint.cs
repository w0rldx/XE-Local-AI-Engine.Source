namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     What pairwise judging this project will cost, answered BEFORE the operator saves the mode. Pairwise is
///     quadratic in the cohort — twelve runs is 132 judge calls — so the number goes in front of the decision.
/// </summary>
public sealed class GetBenchmarkPairwiseEstimateEndpoint : Endpoint<GetBenchmarkPairwiseEstimateRequest, GetBenchmarkPairwiseEstimateResponse>
{
    private readonly IBenchmarkPairwisePlanner _planner;
    private readonly BenchmarkRecordService _records;

    public GetBenchmarkPairwiseEstimateEndpoint(BenchmarkRecordService records, IBenchmarkPairwisePlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(records);
        _planner = planner;
        _records = records;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectPairwiseEstimate);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GetBenchmarkPairwiseEstimateRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (await _records.GetProjectAsync(req.ProjectId, ct) is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found.")));
            return;
        }

        var estimate = await _planner.EstimateAsync(req.ProjectId, ct);
        await Send.OkAsync(new GetBenchmarkPairwiseEstimateResponse
        {
            EligibleRuns = estimate.EligibleRuns,
            PairedRuns = estimate.PairedRuns,
            CappedRuns = estimate.CappedRuns,
            JudgeCalls = estimate.JudgeCalls,
            EstimatedSeconds = estimate.EstimatedSeconds,
            Warn = estimate.Warn,
            MaximumRuns = BenchmarkPairwisePolicy.MaximumRuns
        }, ct);
    }
}
