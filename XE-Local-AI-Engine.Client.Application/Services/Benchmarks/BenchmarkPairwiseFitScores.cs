namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The one typed read of a published fit's <c>ScoresJson</c> blob outside the store's own ranking.</summary>
/// <remarks>
///     The comparisons listing and both exports read the blob through here, so a blob that cannot be parsed is handled
///     one way everywhere: no pairwise scores, and a Warning that names the fit but never carries the blob.
/// </remarks>
internal static class BenchmarkPairwiseFitScores
{
    /// <summary>Same options the fitter wrote the blob with, so a member name cannot bind on one side only.</summary>
    private static readonly JsonSerializerOptions ScoreOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The fit's per-run entries in stored order; empty when there is no fit or its blob is unreadable.</summary>
    public static IReadOnlyList<BenchmarkPairwiseScoreEntry> Read(BenchmarkPairwiseFitRecord? fit, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (fit is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<BenchmarkPairwiseScoreEntry[]>(fit.ScoresJson, ScoreOptions) ?? [];
        }
        catch (JsonException exception)
        {
            // The exception is not attached: its message quotes the offending token, which is blob content.
            logger.LogWarning("Benchmark pairwise fit {FitId} of project {ProjectId} has unreadable scores (line {LineNumber}, byte {BytePosition}); "
                              + "it is served as no pairwise score.",
                fit.Id,
                fit.ProjectId,
                exception.LineNumber,
                exception.BytePositionInLine);
            return [];
        }
    }
}
