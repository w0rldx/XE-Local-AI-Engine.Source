namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal static class BenchmarkExportProjection
{
    /// <summary>
    ///     Current export schema version. Version 4 adds task items, measurement cells and per-run task/cell identity
    ///     while preserving every field from version 3.
    /// </summary>
    public const int SchemaVersion = 4;

    public static BenchmarkExportPairwiseFitResponse? ToResponse(BenchmarkPairwiseFitRecord? fit) =>
        fit is null
            ? null
            : new BenchmarkExportPairwiseFitResponse
            {
                Id = fit.Id,
                FitKey = fit.FitKey,
                JudgeExecutionKey = fit.JudgeExecutionKey,
                CohortGeneration = fit.CohortGeneration,
                ComparisonSetVersion = fit.ComparisonSetVersion,
                Iterations = fit.Iterations,
                BootstrapReplicates = fit.BootstrapReplicates,
                CreatedAtUtc = fit.CreatedAtUtc,
                FittedSetJson = fit.FittedSetJson,
                Scores =
                [
                    .. BenchmarkExportPairwise.Scores(fit)
                                              .Values.OrderBy(static entry => entry.RunId)
                                              .Select(static entry => new BenchmarkExportPairwiseScoreResponse
                                              {
                                                  RunId = entry.RunId,
                                                  Score = entry.Score,
                                                  CiLow = entry.CiLow,
                                                  CiHigh = entry.CiHigh,
                                                  Comparisons = entry.Comparisons,
                                                  BootstrapAppearances = entry.BootstrapAppearances,
                                                  Reason = entry.Reason
                                              })
                ]
            };

    private const int MaxSlugLength = 40;

    public static BenchmarkRankCohortResponse ToResponse(BenchmarkRankCohort? cohort) =>
        new()
        {
            PolicyRevision = cohort?.PolicyRevision,
            ExecutionKey = cohort?.ExecutionKey,
            CohortGeneration = cohort?.CohortGeneration,
            RankedCount = cohort?.RankedCount ?? 0,
            TotalScored = cohort?.TotalScored ?? 0
        };

    /// <summary>
    ///     <c>benchmark-&lt;slug&gt;-&lt;yyyyMMdd-HHmm&gt;.&lt;extension&gt;</c>. The project name is operator-supplied
    ///     free text, so the slug is reduced to <c>[a-z0-9-]</c> before it can reach a <c>Content-Disposition</c>
    ///     header or steer where a browser writes the file.
    /// </summary>
    public static string FileName(string projectName, DateTimeOffset now, string extension) =>
        $"benchmark-{Slug(projectName)}{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.{extension}";

    public static string Attachment(string projectName, DateTimeOffset now, string extension) =>
        $"attachment; filename=\"{FileName(projectName, now, extension)}\"";

    private static string Slug(string projectName)
    {
        var builder = new StringBuilder(MaxSlugLength);
        foreach (var character in projectName)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            if (builder.Length >= MaxSlugLength)
            {
                break;
            }
        }

        // A trailing separator is always emitted so an empty name degrades to `benchmark-<timestamp>` rather than
        // `benchmark<timestamp>`.
        return builder.Length > 0 && builder[^1] != '-' ? builder.Append('-').ToString() : builder.ToString();
    }
}
