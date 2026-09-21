namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for model-fit snapshot runs.
/// </summary>
/// <remarks>
///     The raw output, stderr excerpt and detailed diagnostics are encrypted at rest by the node encryption
///     interceptors and are SANITIZED-BY-DEFAULT at this boundary: the list and latest projections
///     (<see cref="ModelFitSnapshotSummaryRecord" />) never carry them, and only the explicit operator-diagnostics read
///     (<see cref="GetRawByIdAsync" />) returns them decrypted. This store performs no validation; it owns id and
///     timestamp stamping and the transactional latest-successful replacement.
/// </remarks>
public interface IModelFitSnapshotStore
{
    /// <summary>
    ///     Persists a new run (assigning <c>Id</c> and <c>CreatedAtUtc</c>) in a non-terminal state and returns its summary
    ///     projection.
    /// </summary>
    Task<ModelFitSnapshotSummaryRecord> CreateRunningAsync(ModelFitSnapshotInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Moves the run with <paramref name="id" /> to a terminal status, stamping the supplied fields and storing the
    ///     plaintext raw output, stderr and diagnostics for at-rest encryption.
    /// </summary>
    /// <remarks>
    ///     A terminal <see cref="ModelFitRunStatus.Succeeded" /> runs in a single transaction that clears
    ///     <c>is_latest_successful</c> on the prior latest row for the SAME key
    ///     (<c>operation, use_case, provider_name, model_name</c>) and sets it on this row, so two refreshes can never
    ///     leave two rows latest for one key.
    /// </remarks>
    /// <returns>The updated summary, or <c>null</c> when no run has that id.</returns>
    Task<ModelFitSnapshotSummaryRecord?> MarkTerminalAsync(Guid id,
        ModelFitRunStatus status,
        int? exitCode,
        long? durationMs,
        string? rawJson,
        string? stderrExcerpt,
        string? diagnosticsJson,
        long completedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the latest successful summary for the key (<paramref name="operation" />, <paramref name="useCase" />,
    ///     <paramref name="providerName" />, <paramref name="modelName" />), or <c>null</c> when none exists. The use-case
    ///     and model-name match on null too (null for recommendation snapshots).
    /// </summary>
    Task<ModelFitSnapshotSummaryRecord?> GetLatestSuccessfulSummaryAsync(ModelFitOperation operation,
        string? useCase,
        string providerName,
        string? modelName,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the most recent run summaries (newest first by <c>CreatedAtUtc</c>), filtered by the supplied criteria
    ///     (each <c>null</c> filter is ignored) and capped at <paramref name="limit" />.
    /// </summary>
    Task<IReadOnlyList<ModelFitSnapshotSummaryRecord>> ListRecentSummariesAsync(ModelFitOperation? operation = null,
        string? providerName = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Operator-diagnostics read ONLY: returns the run with <paramref name="id" /> including its decrypted raw output,
    ///     stderr excerpt and diagnostics, or <c>null</c> when none exists. Never used by list/latest projections.
    /// </summary>
    Task<ModelFitSnapshotRawRecord?> GetRawByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
///     Sanitized projection of a persisted model-fit snapshot. Deliberately omits the raw output, stderr excerpt and
///     diagnostics so default reads never surface sensitive utility output.
/// </summary>
public sealed class ModelFitSnapshotSummaryRecord
{
    public required Guid Id { get; init; }

    public required string ApprovedImageId { get; init; }

    public required ModelFitOperation Operation { get; init; }

    public required string? UseCase { get; init; }

    public required string ProviderName { get; init; }

    public required string? ModelName { get; init; }

    public required ModelFitRunStatus Status { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long? DurationMs { get; init; }

    public required int? ExitCode { get; init; }

    public required bool IsLatestSuccessful { get; init; }

    public required Guid? CreatedByRunId { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>
///     Operator-only projection of a persisted model-fit snapshot's sensitive payload. The raw output, stderr excerpt
///     and diagnostics are returned decrypted (decrypted on materialization). Returned ONLY by
///     <see cref="IModelFitSnapshotStore.GetRawByIdAsync" />.
/// </summary>
public sealed class ModelFitSnapshotRawRecord
{
    public required Guid Id { get; init; }

    public required string? RawJson { get; init; }

    public required string? StderrExcerpt { get; init; }

    public required string? DiagnosticsJson { get; init; }
}

/// <summary>
///     Mutable fields supplied when a model-fit snapshot run is created. <see cref="UseCase" /> and
///     <see cref="ModelName" /> are part of the latest-successful key (both null for a recommendation run).
/// </summary>
public sealed class ModelFitSnapshotInput
{
    public required string ApprovedImageId { get; init; }

    public required ModelFitOperation Operation { get; init; }

    public required string? UseCase { get; init; }

    public required string ProviderName { get; init; }

    public required string? ModelName { get; init; }

    public required ModelFitRunStatus Status { get; init; }

    public required long? StartedAtUtc { get; init; }

    public Guid? CreatedByRunId { get; init; }
}
