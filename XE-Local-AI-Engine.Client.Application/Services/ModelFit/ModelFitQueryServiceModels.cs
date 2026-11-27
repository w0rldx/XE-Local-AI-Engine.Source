namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Application-layer view of the latest cached recommendation snapshot: only sanitized and normalized data — the
///     snapshot summary fields plus the normalized recommendation rows.
/// </summary>
/// <remarks>
///     It never carries the snapshot's encrypted raw output, stderr excerpt or detailed diagnostics; those are
///     reachable only through the explicit operator-diagnostics store read, never through this query surface.
/// </remarks>
public sealed class ModelFitLatestRecommendationsView
{
    public required Guid SnapshotId { get; init; }

    public required ModelFitRunStatus Status { get; init; }

    public required string ApprovedImageId { get; init; }

    public required string? UseCase { get; init; }

    public required string ProviderName { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required IReadOnlyList<ModelFitRecommendationView> Recommendations { get; init; }

    /// <summary>Display names of the catalog entries this run could not inspect; empty for older snapshots.</summary>
    public required IReadOnlyList<string> SkippedCatalogEntries { get; init; }
}

/// <summary>One recommendation row plus the typed read of its diagnostics blob, parsed once at the query boundary.</summary>
public sealed class ModelFitRecommendationView
{
    /// <summary>The row; its raw <see cref="ModelFitRecommendationRecord.DiagnosticsJson" /> is never surfaced.</summary>
    public required ModelFitRecommendationRecord Record { get; init; }

    public required ModelFitRecommendationDiagnostics Diagnostics { get; init; }
}
