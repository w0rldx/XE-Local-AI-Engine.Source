namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for measured model-fit benchmark rows projected from a benchmark snapshot.
/// </summary>
/// <remarks>
///     The structural metrics are plaintext; the raw output and diagnostics are encrypted at rest by the node
///     encryption interceptors and returned decrypted on the record. This store performs no validation; it owns id
///     stamping and the per-snapshot replace.
/// </remarks>
public interface IModelFitBenchmarkStore
{
    // The disabled benchmark operation has no live caller. This interface preserves its persistence contract.

    /// <summary>
    ///     Replaces every benchmark row for <paramref name="snapshotId" /> with <paramref name="benchmarks" /> in a single
    ///     transaction (delete-then-insert). Each input row is assigned a fresh <c>Id</c>. Returns the count inserted.
    /// </summary>
    Task<int> ReplaceForSnapshotAsync(Guid snapshotId, IReadOnlyList<ModelFitBenchmarkInput> benchmarks, CancellationToken cancellationToken = default);

    /// <summary>Returns the benchmark rows for <paramref name="snapshotId" />, ordered by <c>ModelName</c>.</summary>
    Task<IReadOnlyList<ModelFitBenchmarkRecord>> ListForSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the most recent SUCCESSFUL benchmark row bound to <paramref name="profileId" /> — its parent snapshot
    ///     is <c>Succeeded</c> — newest first by the snapshot's creation instant.
    /// </summary>
    /// <remarks>
    ///     Rows with a null <c>ProfileId</c> never match. Backs the freeze gate's revision binding, so a benchmark
    ///     taken for a different profile revision is never returned here.
    /// </remarks>
    /// <returns>The row, or <c>null</c> when the profile has no successful benchmark.</returns>
    Task<ModelFitBenchmarkRecord?> GetLatestSuccessfulForProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Typed projection of a persisted model-fit benchmark row. <c>RawJson</c> and <c>DiagnosticsJson</c> are returned in
///     plaintext (decrypted on materialization); the structural metrics are plaintext columns.
/// </summary>
public sealed record ModelFitBenchmarkRecord
{
    public required Guid Id { get; init; }

    public required Guid SnapshotId { get; init; }

    public required string ModelName { get; init; }

    public required string ProviderName { get; init; }

    public required double? TokensPerSecond { get; init; }

    public required double? TtftMs { get; init; }

    public required double? TotalLatencyMs { get; init; }

    public required int? Runs { get; init; }

    public required string? RawJson { get; init; }

    public required string? DiagnosticsJson { get; init; }

    public double? PpTokensPerSecond { get; init; }

    public double? CacheHitRate { get; init; }

    public double? ToolLoopMs { get; init; }

    public long? VramLoadBytes { get; init; }

    public long? VramAfterBytes { get; init; }

    public string? LlamacppBuild { get; init; }

    public string? Quant { get; init; }

    public int? CtxSize { get; init; }

    public string? KvType { get; init; }

    public string? Backend { get; init; }

    public string? MachineKey { get; init; }

    public int? NGpuLayers { get; init; }

    public string? TensorSplit { get; init; }

    public string? OverrideTensor { get; init; }

    public string? KvTypeV { get; init; }

    public bool? FlashAttn { get; init; }

    public Guid? ProfileId { get; init; }

    public int? LaunchPolicyFingerprintVersion { get; init; }

    public string? LaunchPolicyFingerprint { get; init; }

    public long? GlobalFreeVramLoadBytes { get; init; }

    public long? GlobalFreeVramAfterBytes { get; init; }

    public long? ProcessBudgetVramLoadBytes { get; init; }

    public long? ProcessBudgetVramAfterBytes { get; init; }

    public long? MinimumGlobalFreeVramBytes { get; init; }

    public long? MinimumProcessBudgetVramBytes { get; init; }

    public long? PeakProcessRamBytes { get; init; }

    public bool ExternalPressureDetected { get; init; }
}

/// <summary>
///     Mutable fields of a benchmark row supplied on replace. <c>RawJson</c> and <c>DiagnosticsJson</c> are passed as
///     plaintext strings; the store encodes them to UTF-8 bytes before the interceptors encrypt them. <c>Id</c> and
///     <c>SnapshotId</c> are assigned by the store.
/// </summary>
public sealed class ModelFitBenchmarkInput
{
    public required string ModelName { get; init; }

    public required string ProviderName { get; init; }

    public required double? TokensPerSecond { get; init; }

    public required double? TtftMs { get; init; }

    public required double? TotalLatencyMs { get; init; }

    public required int? Runs { get; init; }

    public required string? RawJson { get; init; }

    public required string? DiagnosticsJson { get; init; }

    public double? PpTokensPerSecond { get; init; }

    public double? CacheHitRate { get; init; }

    public double? ToolLoopMs { get; init; }

    public long? VramLoadBytes { get; init; }

    public long? VramAfterBytes { get; init; }

    public string? LlamacppBuild { get; init; }

    public string? Quant { get; init; }

    public int? CtxSize { get; init; }

    public string? KvType { get; init; }

    public string? Backend { get; init; }

    public string? MachineKey { get; init; }

    public int? NGpuLayers { get; init; }

    public string? TensorSplit { get; init; }

    public string? OverrideTensor { get; init; }

    public string? KvTypeV { get; init; }

    public bool? FlashAttn { get; init; }

    public Guid? ProfileId { get; init; }

    public int? LaunchPolicyFingerprintVersion { get; init; }

    public string? LaunchPolicyFingerprint { get; init; }

    public long? GlobalFreeVramLoadBytes { get; init; }

    public long? GlobalFreeVramAfterBytes { get; init; }

    public long? ProcessBudgetVramLoadBytes { get; init; }

    public long? ProcessBudgetVramAfterBytes { get; init; }

    public long? MinimumGlobalFreeVramBytes { get; init; }

    public long? MinimumProcessBudgetVramBytes { get; init; }

    public long? PeakProcessRamBytes { get; init; }

    public bool ExternalPressureDetected { get; init; }
}
