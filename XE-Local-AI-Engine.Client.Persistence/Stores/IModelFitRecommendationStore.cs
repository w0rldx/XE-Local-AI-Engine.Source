namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for normalized model-fit recommendation rows projected from a recommendation snapshot.
///     All columns are plaintext (model metadata, not secret). This store performs no validation; it owns id stamping
///     and the per-snapshot replace.
/// </summary>
public interface IModelFitRecommendationStore
{
    /// <summary>
    ///     Replaces every recommendation row for <paramref name="snapshotId" /> with <paramref name="recommendations" />
    ///     in a single transaction (delete-then-insert). Each input row is assigned a fresh <c>Id</c>. Returns the count
    ///     inserted.
    /// </summary>
    Task<int> ReplaceForSnapshotAsync(Guid snapshotId, IReadOnlyList<ModelFitRecommendationInput> recommendations, CancellationToken cancellationToken = default);

    /// <summary>Returns the recommendation rows for <paramref name="snapshotId" />, ordered by <c>Rank</c>.</summary>
    Task<IReadOnlyList<ModelFitRecommendationRecord>> ListForSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Typed projection of a persisted model-fit recommendation row. All fields are plaintext.
/// </summary>
public sealed record ModelFitRecommendationRecord(
    Guid Id,
    Guid SnapshotId,
    int Rank,
    string ModelName,
    string? ProviderModelName,
    double Score,
    string? FitLevel,
    string? RunMode,
    string? Quantization,
    double? EstimatedTokensPerSecond,
    double? RequiredRamMb,
    double? RequiredVramMb,
    int? ContextTokens,
    bool IsInstalled,
    string? PullModelName,
    string? DiagnosticsJson);

/// <summary>
///     Mutable fields of a recommendation row supplied on replace. <c>Id</c> and <c>SnapshotId</c> are assigned by the
///     store.
/// </summary>
public sealed record ModelFitRecommendationInput(
    int Rank,
    string ModelName,
    string? ProviderModelName,
    double Score,
    string? FitLevel,
    string? RunMode,
    string? Quantization,
    double? EstimatedTokensPerSecond,
    double? RequiredRamMb,
    double? RequiredVramMb,
    int? ContextTokens,
    bool IsInstalled,
    string? PullModelName,
    string? DiagnosticsJson);
