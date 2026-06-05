namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Read-only projections over the cached model-fit data. This is a pure cache reader: it lists the
///     approved utility images and assembles the latest cached recommendation snapshot from the sanitized persistence-store
///     projections. It NEVER invokes the HostAgent utility runner and never runs llmfit — fresh runs are produced only by
///     the scheduler's <c>model-recommendation-check</c> handler. It deliberately takes no dependency on
///     <c>IModelFitUtilityRunner</c> or <c>IModelFitRefreshService</c> so a query can never trigger an execution path.
/// </summary>
public interface IModelFitQueryService
{
    /// <summary>Returns every approved utility image descriptor, ordered by id, projected from the registry store.</summary>
    Task<IReadOnlyList<ApprovedUtilityImageRecord>> ListApprovedImagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the latest successful cached recommendation snapshot for the (<paramref name="useCase" />,
    ///     <paramref name="providerName" />) key as a sanitized view, or <c>null</c> when no successful recommendation
    ///     snapshot has ever been cached (a cache-miss the caller surfaces as the empty state). Reads cached state only —
    ///     it never runs the utility.
    /// </summary>
    Task<ModelFitLatestRecommendationsView?> GetLatestRecommendationsAsync(
        string? useCase,
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
