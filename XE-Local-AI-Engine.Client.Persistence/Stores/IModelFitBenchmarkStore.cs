namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Node-scoped persistence for measured model-fit benchmark rows projected from a benchmark snapshot. The structural
///     metrics are plaintext; the raw output and diagnostics are encrypted at rest by the node encryption interceptors
///     and returned decrypted on the record. This store performs no validation; it owns id stamping and the per-snapshot
///     replace.
/// </summary>
public interface IModelFitBenchmarkStore
{
    /// <summary>
    ///     Replaces every benchmark row for <paramref name="snapshotId" /> with <paramref name="benchmarks" /> in a single
    ///     transaction (delete-then-insert). Each input row is assigned a fresh <c>Id</c>. Returns the count inserted.
    /// </summary>
    Task<int> ReplaceForSnapshotAsync(Guid snapshotId, IReadOnlyList<ModelFitBenchmarkInput> benchmarks, CancellationToken cancellationToken = default);

    /// <summary>Returns the benchmark rows for <paramref name="snapshotId" />, ordered by <c>ModelName</c>.</summary>
    Task<IReadOnlyList<ModelFitBenchmarkRecord>> ListForSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Typed projection of a persisted model-fit benchmark row. <c>RawJson</c> and <c>DiagnosticsJson</c> are returned in
///     plaintext (decrypted on materialization); the structural metrics are plaintext columns.
/// </summary>
public sealed record ModelFitBenchmarkRecord(
    Guid Id,
    Guid SnapshotId,
    string ModelName,
    string ProviderName,
    double? TokensPerSecond,
    double? TtftMs,
    double? TotalLatencyMs,
    int? Runs,
    string? RawJson,
    string? DiagnosticsJson);

/// <summary>
///     Mutable fields of a benchmark row supplied on replace. <c>RawJson</c> and <c>DiagnosticsJson</c> are passed as
///     plaintext strings; the store encodes them to UTF-8 bytes before the interceptors encrypt them. <c>Id</c> and
///     <c>SnapshotId</c> are assigned by the store.
/// </summary>
public sealed record ModelFitBenchmarkInput(
    string ModelName,
    string ProviderName,
    double? TokensPerSecond,
    double? TtftMs,
    double? TotalLatencyMs,
    int? Runs,
    string? RawJson,
    string? DiagnosticsJson);
