namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

/// <summary>
///     The verdict matrix behind a project's pairwise scores, together with the fit those verdicts produced.
/// </summary>
/// <remarks>
///     One route, deliberately: splitting them would let a client render a strength beside a verdict set that did not
///     produce it, and nothing on the wire would say so.
/// </remarks>
public sealed class ListBenchmarkComparisonsEndpoint : Endpoint<ListBenchmarkComparisonsRequest, ListBenchmarkComparisonsResponse>
{
    private readonly BenchmarkComparisonService _comparisons;

    public ListBenchmarkComparisonsEndpoint(BenchmarkComparisonService comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        _comparisons = comparisons;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectComparisons);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ListBenchmarkComparisonsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (await _comparisons.GetComparisonsAsync(req.ProjectId, ct) is not { } view)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found.")));
            return;
        }

        var cohort = view.Cohort;
        await Send.OkAsync(new ListBenchmarkComparisonsResponse
        {
            CohortGeneration = cohort.CohortGeneration,
            ComparisonSetVersion = cohort.ComparisonSetVersion,
            ReferenceExecutionKey = cohort.ReferenceExecutionKey,
            Items =
            [
                .. cohort.Comparisons.Select(static comparison => new BenchmarkComparisonResponse
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
                })
            ],
            Fit = ToResponse(view)
        }, ct);
    }

    private static BenchmarkPairwiseFitResponse? ToResponse(BenchmarkComparisonsView view)
    {
        if (view.Fit is not { } fit)
        {
            return null;
        }

        return new BenchmarkPairwiseFitResponse
        {
            FitKey = fit.FitKey,
            JudgeExecutionKey = fit.JudgeExecutionKey,
            ComparisonSetVersion = fit.ComparisonSetVersion,
            CohortGeneration = fit.CohortGeneration,
            Iterations = fit.Iterations,
            BootstrapReplicates = fit.BootstrapReplicates,
            IsCurrent = view.FitIsCurrent,
            CreatedAtUtc = fit.CreatedAtUtc,
            FittedSetJson = fit.FittedSetJson,
            Scores =
            [
                .. view.FitScores.Select(static score => new BenchmarkPairwiseRunScoreResponse
                {
                    RunId = score.RunId,
                    Score = score.Score,
                    CiLow = score.CiLow,
                    CiHigh = score.CiHigh,
                    Comparisons = score.Comparisons,
                    BootstrapAppearances = score.BootstrapAppearances,
                    Reason = score.Reason
                })
            ]
        };
    }
}
