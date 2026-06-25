namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Read-only projection over the cached model-fit data. This is a pure cache reader: it assembles the latest cached
///     recommendation snapshot from the sanitized persistence-store projections. It NEVER runs the advisor — fresh runs
///     are produced only by the scheduler's <c>model-recommendation-check</c> handler. It deliberately takes no dependency
///     on <c>IModelFitRefreshService</c> so a query can never trigger an execution path. The approved-image listing is
///     gone (the approved-image concept was removed when the advisor moved to in-process box-aware GGUF recommendation).
/// </summary>
public interface IModelFitQueryService
{
    /// <summary>
    ///     Returns the latest successful cached recommendation snapshot for the (<paramref name="useCase" />,
    ///     <paramref name="providerName" />) key as a sanitized view, or <c>null</c> when no successful recommendation
    ///     snapshot has ever been cached (a cache-miss the caller surfaces as the empty state). Reads cached state only —
    ///     it never runs the utility.
    /// </summary>
    Task<ModelFitLatestRecommendationsView?> GetLatestRecommendationsAsync(string? useCase,
        string providerName,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Application-layer view of the latest cached recommendation snapshot. Carries only sanitized/normalized data — the
///     snapshot summary fields plus the normalized recommendation rows. It never carries the snapshot's encrypted raw
///     output, stderr excerpt or detailed diagnostics (those are reachable only through the explicit operator-diagnostics
///     store read, never through this query surface).
/// </summary>
public sealed record ModelFitLatestRecommendationsView(
    Guid SnapshotId,
    ModelFitRunStatus Status,
    string ApprovedImageId,
    string? UseCase,
    string ProviderName,
    long? CompletedAtUtc,
    IReadOnlyList<ModelFitRecommendationRecord> Recommendations);
