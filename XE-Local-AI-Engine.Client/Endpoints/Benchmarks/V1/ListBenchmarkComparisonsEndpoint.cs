namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Text.Json;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     The verdict matrix behind a project's pairwise scores, together with the fit those verdicts produced.
/// </summary>
/// <remarks>
///     One route, deliberately: splitting them would let a client render a strength beside a verdict set that did not
///     produce it, and nothing on the wire would say so.
/// </remarks>
public sealed class ListBenchmarkComparisonsEndpoint : Endpoint<ListBenchmarkComparisonsRequest, ListBenchmarkComparisonsResponse>
{
    private static readonly JsonSerializerOptions ScoreOptions = new(JsonSerializerDefaults.Web);
    private readonly BenchmarkRecordService _records;

    public ListBenchmarkComparisonsEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
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
        if (await _records.GetProjectAsync(req.ProjectId, ct) is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found.")));
            return;
        }

        var cohort = await _records.GetPairwiseCohortAsync(req.ProjectId, ct);
        var fit = await _records.GetActivePairwiseFitAsync(req.ProjectId, ct);
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
                      Fit = ToResponse(fit, cohort)
                  }, ct);
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
            Scores =
            [
                .. scores.Select(static score => new BenchmarkPairwiseRunScoreResponse
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
