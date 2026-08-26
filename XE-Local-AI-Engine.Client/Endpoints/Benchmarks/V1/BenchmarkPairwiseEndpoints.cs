namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Text.Json;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     The verdict matrix behind a project's pairwise scores, together with the fit those verdicts produced. One
///     route, deliberately: splitting them would let a client render a strength beside a verdict set that did not
///     produce it, and nothing on the wire would say so.
/// </summary>
public sealed class ListBenchmarkComparisonsEndpoint(IBenchmarkStore store)
    : Endpoint<ListBenchmarkComparisonsRequest, ListBenchmarkComparisonsResponse>
{
    private static readonly JsonSerializerOptions ScoreOptions = new(JsonSerializerDefaults.Web);
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectComparisons);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ListBenchmarkComparisonsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (await _store.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false) is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        var cohort = await _store.GetPairwiseCohortAsync(req.ProjectId, ct).ConfigureAwait(false);
        var fit = await _store.GetActivePairwiseFitAsync(req.ProjectId, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListBenchmarkComparisonsResponse
                  {
                      CohortGeneration = cohort.CohortGeneration,
                      ComparisonSetVersion = cohort.ComparisonSetVersion,
                      ReferenceExecutionKey = cohort.ReferenceExecutionKey,
                      Items = [.. cohort.Comparisons.Select(static comparison => new BenchmarkComparisonResponse
                      {
                          Id = comparison.Id,
                          RunAId = comparison.RunAId,
                          RunBId = comparison.RunBId,
                          Order = comparison.Order,
                          AttemptSequence = comparison.AttemptSequence,
                          Sequence = comparison.Sequence,
                          TaskCaseId = comparison.TaskCaseId,
                          Status = comparison.Status.ToString(),
                          Verdict = comparison.Verdict,
                          AnswerATruncated = comparison.AnswerATruncated,
                          AnswerBTruncated = comparison.AnswerBTruncated,
                          JudgeExecutionKey = comparison.JudgeExecutionKey,
                          ErrorMessage = comparison.ErrorMessage,
                          EnqueuedAtUtc = comparison.EnqueuedAtUtc,
                          CompletedAtUtc = comparison.CompletedAtUtc
                      })],
                      Fit = ToResponse(fit, cohort)
                  }, ct)
                  .ConfigureAwait(false);
    }

    private static BenchmarkPairwiseFitResponse? ToResponse(BenchmarkPairwiseFitRecord? fit, BenchmarkPairwiseCohortState cohort)
    {
        if (fit is null)
        {
            return null;
        }

        var scores = JsonSerializer.Deserialize<BenchmarkPairwiseScoreEntry[]>(fit.ScoresJson, ScoreOptions) ?? [];
        return new BenchmarkPairwiseFitResponse
        {
            FitKey = fit.FitKey,
            JudgeExecutionKey = fit.JudgeExecutionKey,
            ComparisonSetVersion = fit.ComparisonSetVersion,
            CohortGeneration = fit.CohortGeneration,
            Iterations = fit.Iterations,
            BootstrapReplicates = fit.BootstrapReplicates,

            // The same comparison the ranking makes: one integer against the revision's current value, plus the
            // promoted execution key. No verdict is read to answer it.
            IsCurrent = fit.ComparisonSetVersion == cohort.ComparisonSetVersion
                        && string.Equals(fit.JudgeExecutionKey, cohort.ReferenceExecutionKey ?? string.Empty, StringComparison.Ordinal),
            CreatedAtUtc = fit.CreatedAtUtc,
            FittedSetJson = fit.FittedSetJson,
            Scores = [.. scores.Select(static score => new BenchmarkPairwiseRunScoreResponse
            {
                RunId = score.RunId,
                Score = score.Score,
                CiLow = score.CiLow,
                CiHigh = score.CiHigh,
                Comparisons = score.Comparisons,
                BootstrapAppearances = score.BootstrapAppearances,
                Reason = score.Reason
            })]
        };
    }
}

/// <summary>
///     What pairwise judging this project will cost, answered BEFORE the operator saves the mode. Pairwise is
///     quadratic in the cohort — twelve runs is 132 judge calls — so the number goes in front of the decision.
/// </summary>
public sealed class GetBenchmarkPairwiseEstimateEndpoint(IBenchmarkStore store, IBenchmarkPairwisePlanner planner)
    : Endpoint<GetBenchmarkPairwiseEstimateRequest, GetBenchmarkPairwiseEstimateResponse>
{
    private readonly IBenchmarkPairwisePlanner _planner = planner ?? throw new ArgumentNullException(nameof(planner));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectPairwiseEstimate);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GetBenchmarkPairwiseEstimateRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (await _store.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false) is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        var estimate = await _planner.EstimateAsync(req.ProjectId, ct).ConfigureAwait(false);
        await Send.OkAsync(new GetBenchmarkPairwiseEstimateResponse
                  {
                      EligibleRuns = estimate.EligibleRuns,
                      PairedRuns = estimate.PairedRuns,
                      CappedRuns = estimate.CappedRuns,
                      JudgeCalls = estimate.JudgeCalls,
                      EstimatedSeconds = estimate.EstimatedSeconds,
                      Warn = estimate.Warn,
                      MaximumRuns = BenchmarkPairwisePolicy.MaximumRuns
                  }, ct)
                  .ConfigureAwait(false);
    }
}
