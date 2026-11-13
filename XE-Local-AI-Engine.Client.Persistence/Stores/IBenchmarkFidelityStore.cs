namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Quant-fidelity settings, attempts and their work items. One role of <c>BenchmarkStore</c>, registered as the same
///     scoped instance as its siblings.
/// </summary>
public interface IBenchmarkFidelityStore
{
    /// <summary>
    ///     Enqueues one fidelity measurement of a run: a <c>Queued</c> attempt at the next sequence plus its work
    ///     item, in one transaction.
    /// </summary>
    /// <remarks>
    ///     Re-measuring inserts a NEW attempt rather than reusing the previous one, so the numbers a KLD figure was
    ///     measured against survive a corpus or chunk-count change.
    /// </remarks>
    Task<Guid> EnqueueFidelityAsync(Guid runId, string kind, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Terminalizes a fidelity work item and its attempt, and refreshes the run's fidelity projection from it only
    ///     when the attempt succeeded AND is the highest-sequenced succeeded attempt of the run.
    /// </summary>
    /// <remarks>
    ///     A stale re-measurement therefore cannot overwrite newer numbers, and a failed one leaves the previous
    ///     numbers alone.
    /// </remarks>
    Task<BenchmarkRunRecord> MarkFidelitySucceededAsync(BenchmarkFidelitySuccessCommand command, CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkFidelityFailedAsync(Guid runId, long expectedWorkVersion, string errorMessage, CancellationToken cancellationToken = default);

    Task<BenchmarkRunRecord> MarkFidelityCancelledAsync(Guid runId, long expectedWorkVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns a claimed fidelity item — work item AND attempt — to <c>Queued</c>, carrying the reason it could not
    ///     proceed yet.
    /// </summary>
    /// <remarks>
    ///     For a blocker that clears itself, such as a base-logit file another process is still writing: the work item
    ///     pins <c>attempt = 1</c>, so terminalizing it as failed is terminal in the literal sense and the "it will be
    ///     retried" the operator was promised has nothing behind it. A no-op once the attempt is terminal, so a requeue
    ///     racing a completion cannot start a second measurement.
    /// </remarks>
    Task<BenchmarkRunRecord> RequeueFidelityAsync(Guid runId, long expectedWorkVersion, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes the project's quant-fidelity settings, and ONLY those, deliberately ignoring the freeze the ordinary
    ///     project write honors.
    /// </summary>
    /// <remarks>
    ///     A frozen project refuses every other edit because its runs were measured against the task, context and
    ///     agent it carries; the fidelity settings are none of those — they decide what gets measured NEXT, and every
    ///     stored number keeps its own comparability digest. <paramref name="measureExisting" /> additionally enqueues
    ///     a fidelity item for every succeeded, non-warm-up, first-of-its-repeat-group run with no attempt yet, in the
    ///     same transaction — the rule freeze applies per cell — and the count is reported.
    /// </remarks>
    Task<BenchmarkProjectFidelityChange> UpdateProjectFidelityAsync(Guid projectId,
        long expectedProjectVersion,
        BenchmarkProjectFidelityInput input,
        bool measureExisting = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The distinct primary model names of the runs a <c>measureExisting</c> fidelity write would enqueue now, so a
    ///     caller can refuse a KL-divergence base that does not match them before the write.
    /// </summary>
    Task<IReadOnlyList<string>> ListMissingFidelityModelNamesAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<BenchmarkFidelityAttemptRecord?> GetFidelityAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default);

    /// <summary>Every fidelity attempt of a run, newest sequence first — the audit trail behind the projection.</summary>
    Task<IReadOnlyList<BenchmarkFidelityAttemptRecord>> ListFidelityAttemptsAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The base-logit digests a queued or running fidelity attempt is about to read. Cache eviction must not
    ///     delete a file a measurement is on its way to using.
    /// </summary>
    Task<IReadOnlySet<string>> ListLiveFidelityDigestsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether any fidelity work item is queued or running — the guard on clearing the base cache.</summary>
    Task<bool> HasLiveFidelityWorkAsync(CancellationToken cancellationToken = default);
}
