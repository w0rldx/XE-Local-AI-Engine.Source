namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>The one typed read of a published fit's <c>ScoresJson</c> blob.</summary>
/// <remarks>
///     The store's ranking, the comparisons listing and both exports read the blob through here, so a blob that cannot be
///     parsed, or names a run twice, or holds a null entry, is handled one way everywhere. It lives in Persistence because
///     the ranking is a store read and Persistence cannot reach the Application layer.
/// </remarks>
public static class BenchmarkPairwiseFitScores
{
    /// <summary>Same options the fitter wrote the blob with, so a member name cannot bind on one side only.</summary>
    private static readonly JsonSerializerOptions ScoreOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The fit's per-run entries in stored order; empty when there is no fit or its blob is unreadable.</summary>
    public static IReadOnlyList<BenchmarkPairwiseScoreEntry> Read(BenchmarkPairwiseFitRecord? fit, ILogger logger) =>
        fit is null ? [] : TryRead(fit, logger) ?? [];

    /// <summary>
    ///     The fit's per-run entries in stored order, null elements skipped and the first entry kept for a run named twice;
    ///     null when the blob is unreadable. Either defect logs one Warning that names the fit but never carries the blob.
    /// </summary>
    public static IReadOnlyList<BenchmarkPairwiseScoreEntry>? TryRead(BenchmarkPairwiseFitRecord fit, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(fit);
        ArgumentNullException.ThrowIfNull(logger);
        BenchmarkPairwiseScoreEntry?[] stored;
        try
        {
            stored = JsonSerializer.Deserialize<BenchmarkPairwiseScoreEntry?[]>(fit.ScoresJson, ScoreOptions) ?? [];
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
            return null;
        }

        BenchmarkPairwiseScoreEntry[] entries = [.. stored.OfType<BenchmarkPairwiseScoreEntry>().DistinctBy(static entry => entry.RunId)];
        if (entries.Length != stored.Length)
        {
            // The fitter writes one non-null entry per eligible run, so this is a hand-edited or foreign blob.
            logger.LogWarning("Benchmark pairwise fit {FitId} of project {ProjectId} has {SkippedCount} null or duplicate score entries; "
                              + "null entries are skipped and the first entry for a run wins.",
                fit.Id,
                fit.ProjectId,
                stored.Length - entries.Length);
        }

        return entries;
    }
}
