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
    ///     the justifying <paramref name="benchmarkSnapshotId" /> and the <paramref name="freeVramAtFreezeBytes" />
    ///     invalidation baseline, in a single transaction (mirrors the transactional promotion of the snapshot store). Only
    ///     a row currently in <see cref="InferenceProfileStatus.Explored" /> is frozen (the freeze gate): a successful
    ///     benchmark is the only justification. Returns the updated profile, or <c>null</c> when no row has that id or it is
    ///     not in <see cref="InferenceProfileStatus.Explored" />.
    /// </summary>
    Task<InferenceProfileRecord?> MarkFrozenAsync(Guid id,
        Guid benchmarkSnapshotId,
        long? freeVramAtFreezeBytes,
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
///     <c>BenchmarkSnapshotId</c> and <c>FreeVramAtFreezeBytes</c> (the last two are stamped on freeze, not here).
/// </summary>
public sealed record InferenceProfileInput(
    string MachineKey,
    string ModelName,
    int Role,
    string Backend,
    string LlamacppBuild,
    string Quant,
    int CtxSize,
    int? NGpuLayers,
    string? TensorSplit,
    string? OverrideTensor,
    string? KvTypeK,
    string? KvTypeV,
    bool FlashAttn,
    long? NParams,
    bool IsMoe,
    int? ExpertCount);

/// <summary>
///     Typed projection of a persisted inference profile. The replay args (<see cref="CtxSize" />,
///     <see cref="NGpuLayers" />, <see cref="TensorSplit" />, <see cref="OverrideTensor" />, <see cref="KvTypeK" />,
///     <see cref="KvTypeV" />, <see cref="FlashAttn" />) are exactly what the resolver feeds the supervisor's launch-spec
///     builder for a frozen/explored profile.
/// </summary>
public sealed record InferenceProfileRecord(
    Guid Id,
    string MachineKey,
    string ModelName,
    int Role,
    string Backend,
    string LlamacppBuild,
    string Quant,
    int CtxSize,
    int? NGpuLayers,
    string? TensorSplit,
    string? OverrideTensor,
    string? KvTypeK,
    string? KvTypeV,
    bool FlashAttn,
    long? NParams,
    bool IsMoe,
    int? ExpertCount,
    long? FreeVramAtFreezeBytes,
    InferenceProfileStatus Status,
    Guid? BenchmarkSnapshotId,
    long CreatedAtUtc,
    long UpdatedAtUtc);
