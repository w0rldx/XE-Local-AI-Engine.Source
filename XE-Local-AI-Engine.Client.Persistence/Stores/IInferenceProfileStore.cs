namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for llama-server inference profiles. There is exactly one live config per
///     <c>(machine_key, model_name, role, backend)</c> key: <see cref="CreateOrUpdateExploredAsync" /> upserts that single
///     config (latest explore wins), and the status transitions promote it to <see cref="InferenceProfileStatus.Frozen" />
///     or demote it to <see cref="InferenceProfileStatus.Stale" />. All columns are plaintext structural data; this store
///     performs no validation and owns id/timestamp stamping. The freeze transition mirrors the transactional
///     latest-successful promotion of <c>ModelFitSnapshotStore</c>.
/// </summary>
public interface IInferenceProfileStore
{
    /// <summary>
    ///     Upserts the single <see cref="InferenceProfileStatus.Explored" /> config for the natural key
    ///     (<c>machine_key, model_name, role, backend</c>). When a row already exists for the key its drafted args are
    ///     OVERWRITTEN and it is reset to <see cref="InferenceProfileStatus.Explored" /> (clearing any prior freeze
    ///     justification); otherwise a new row is inserted with a fresh <c>Id</c>/<c>CreatedAtUtc</c>. Returns the stored
    ///     profile.
    /// </summary>
    Task<InferenceProfileRecord> CreateOrUpdateExploredAsync(InferenceProfileInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Transitions the profile with <paramref name="id" /> to <see cref="InferenceProfileStatus.Frozen" />, recording
    ///     the justifying <paramref name="benchmarkSnapshotId" />, global free VRAM invalidation baseline, and
    ///     process-specific VRAM budget diagnostic, in a single transaction (mirrors the transactional promotion of the
    ///     snapshot store). Only
    ///     a row currently in <see cref="InferenceProfileStatus.Explored" /> is frozen (the freeze gate): a successful
    ///     benchmark is the only justification. Returns the updated profile, or <c>null</c> when no row has that id or it is
    ///     not in <see cref="InferenceProfileStatus.Explored" />.
    /// </summary>
    Task<InferenceProfileRecord?> MarkFrozenAsync(Guid id,
        Guid benchmarkSnapshotId,
        long? globalFreeVramAtFreezeBytes,
        long? processBudgetVramAtFreezeBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Transitions the profile with <paramref name="id" /> to <see cref="InferenceProfileStatus.Stale" /> (an
    ///     invalidation trigger fired). Touches one row. Returns the updated profile, or <c>null</c> when no row has that id.
    /// </summary>
    Task<InferenceProfileRecord?> MarkStaleAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the single profile for the natural key (<paramref name="machineKey" />, <paramref name="modelName" />,
    ///     <paramref name="role" />, <paramref name="backend" />), or <c>null</c> when none exists.
    /// </summary>
    Task<InferenceProfileRecord?> GetByKeyAsync(string machineKey,
        string modelName,
        int role,
        string backend,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every persisted profile, ordered by <c>ModelName</c> then <c>Role</c>.</summary>
    Task<IReadOnlyList<InferenceProfileRecord>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     The args/attributes an explore run drafts for one profile key. <see cref="Role" /> is the integer value of
///     <c>ModelRole</c> (Chat=0, Embedding=1). The store owns <c>Id</c>, timestamps, <c>Status</c>,
///     <c>BenchmarkSnapshotId</c> and the explicit global-free/process-budget VRAM fields (the last two are stamped on
///     freeze, not here).
/// </summary>
public sealed class InferenceProfileInput
{
    public required string MachineKey { get; init; }

    public required string ModelName { get; init; }

    public required int Role { get; init; }

    public required string Backend { get; init; }

    public required string LlamacppBuild { get; init; }

    public required string Quant { get; init; }

    public required int CtxSize { get; init; }

    public required int? NGpuLayers { get; init; }

    public required string? TensorSplit { get; init; }

    public required string? OverrideTensor { get; init; }

    public required string? KvTypeK { get; init; }

    public required string? KvTypeV { get; init; }

    public required bool FlashAttn { get; init; }

    public required long? NParams { get; init; }

    public required bool IsMoe { get; init; }

    public required int? ExpertCount { get; init; }

    public required int LaunchPolicyFingerprintVersion { get; init; }

    public required string LaunchPolicyFingerprint { get; init; }
}

/// <summary>
///     Typed projection of a persisted inference profile. The replay args (<see cref="CtxSize" />,
///     <see cref="NGpuLayers" />, <see cref="TensorSplit" />, <see cref="OverrideTensor" />, <see cref="KvTypeK" />,
///     <see cref="KvTypeV" />, <see cref="FlashAttn" />) are exactly what the resolver feeds the supervisor's launch-spec
///     builder for a frozen/explored profile.
/// </summary>
public sealed record InferenceProfileRecord
{
    public required Guid Id { get; init; }

    public required string MachineKey { get; init; }

    public required string ModelName { get; init; }

    public required int Role { get; init; }

    public required string Backend { get; init; }

    public required string LlamacppBuild { get; init; }

    public required string Quant { get; init; }

    public required int CtxSize { get; init; }

    public required int? NGpuLayers { get; init; }

    public required string? TensorSplit { get; init; }

    public required string? OverrideTensor { get; init; }

    public required string? KvTypeK { get; init; }

    public required string? KvTypeV { get; init; }

    public required bool FlashAttn { get; init; }

    public required long? NParams { get; init; }

    public required bool IsMoe { get; init; }

    public required int? ExpertCount { get; init; }

    public required InferenceProfileStatus Status { get; init; }

    public required Guid? BenchmarkSnapshotId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public int? LaunchPolicyFingerprintVersion { get; init; }

    public string? LaunchPolicyFingerprint { get; init; }

    public long? GlobalFreeVramAtFreezeBytes { get; init; }

    public long? ProcessBudgetVramAtFreezeBytes { get; init; }
}
