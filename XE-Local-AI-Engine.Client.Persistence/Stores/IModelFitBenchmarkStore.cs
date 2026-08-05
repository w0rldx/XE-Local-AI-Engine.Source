namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for measured model-fit benchmark rows projected from a benchmark snapshot. The structural
///     metrics are plaintext; the raw output and diagnostics are encrypted at rest by the node encryption interceptors
///     and returned decrypted on the record. This store performs no validation; it owns id stamping and the per-snapshot
///     replace.
/// </summary>
public interface IModelFitBenchmarkStore
{
    // Deferred: the ModelFit Benchmark feature is scaffolding and not wired.
    // This store has no live caller; it is kept so the deferred feature's persistence contract survives.

    /// <summary>
    ///     Replaces every benchmark row for <paramref name="snapshotId" /> with <paramref name="benchmarks" /> in a single
    ///     transaction (delete-then-insert). Each input row is assigned a fresh <c>Id</c>. Returns the count inserted.
    /// </summary>
    Task<int> ReplaceForSnapshotAsync(Guid snapshotId, IReadOnlyList<ModelFitBenchmarkInput> benchmarks, CancellationToken cancellationToken = default);

    /// <summary>Returns the benchmark rows for <paramref name="snapshotId" />, ordered by <c>ModelName</c>.</summary>
    Task<IReadOnlyList<ModelFitBenchmarkRecord>> ListForSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the most recent SUCCESSFUL benchmark row bound to <paramref name="profileId" /> (its parent snapshot is
    ///     <c>Succeeded</c>), newest first by the snapshot's creation instant, or <c>null</c> when the profile has no
    ///     successful benchmark. Legacy rows with a null <c>ProfileId</c> never match. Backs the freeze gate's revision
    ///     binding, so a benchmark taken for a different profile revision is never returned here.
    /// </summary>
    Task<ModelFitBenchmarkRecord?> GetLatestSuccessfulForProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
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
    string? DiagnosticsJson,
    double? PpTokensPerSecond = null,
    double? CacheHitRate = null,
    double? ToolLoopMs = null,
    long? VramLoadBytes = null,
    long? VramAfterBytes = null,
    string? LlamacppBuild = null,
    string? Quant = null,
    int? CtxSize = null,
    string? KvType = null,
    string? Backend = null,
    string? MachineKey = null,
    int? NGpuLayers = null,
    string? TensorSplit = null,
    string? OverrideTensor = null,
    string? KvTypeV = null,
    bool? FlashAttn = null,
    Guid? ProfileId = null,
    int? LaunchPolicyFingerprintVersion = null,
    string? LaunchPolicyFingerprint = null,
    long? GlobalFreeVramLoadBytes = null,
    long? GlobalFreeVramAfterBytes = null,
    long? ProcessBudgetVramLoadBytes = null,
    long? ProcessBudgetVramAfterBytes = null,
    long? MinimumGlobalFreeVramBytes = null,
    long? MinimumProcessBudgetVramBytes = null,
    long? PeakProcessRamBytes = null,
    bool ExternalPressureDetected = false);

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
    string? DiagnosticsJson,
    double? PpTokensPerSecond = null,
    double? CacheHitRate = null,
    double? ToolLoopMs = null,
    long? VramLoadBytes = null,
    long? VramAfterBytes = null,
    string? LlamacppBuild = null,
    string? Quant = null,
    int? CtxSize = null,
    string? KvType = null,
    string? Backend = null,
    string? MachineKey = null,
    int? NGpuLayers = null,
    string? TensorSplit = null,
    string? OverrideTensor = null,
    string? KvTypeV = null,
    bool? FlashAttn = null,
    Guid? ProfileId = null,
    int? LaunchPolicyFingerprintVersion = null,
    string? LaunchPolicyFingerprint = null,
    long? GlobalFreeVramLoadBytes = null,
    long? GlobalFreeVramAfterBytes = null,
    long? ProcessBudgetVramLoadBytes = null,
    long? ProcessBudgetVramAfterBytes = null,
    long? MinimumGlobalFreeVramBytes = null,
    long? MinimumProcessBudgetVramBytes = null,
    long? PeakProcessRamBytes = null,
    bool ExternalPressureDetected = false);
