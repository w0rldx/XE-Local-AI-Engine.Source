namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The benchmark project rows. One role of <c>BenchmarkStore</c>, registered as the same scoped instance as its siblings.</summary>
public interface IBenchmarkProjectStore
{
    /// <summary>
    ///     Creates the project and, with a <paramref name="judgePolicy" />, activates its judge in the SAME
    ///     transaction.
    /// </summary>
    /// <remarks>
    ///     A project that persisted with judging off because a second transaction never ran is one an operator can
    ///     only retry into a duplicate. <paramref name="initialItems" /> is created in that same transaction for the
    ///     same reason, so a project never exists without at least one question to ask and no read path invents one.
    /// </remarks>
    /// <param name="initialItems">
    ///     The project's task items. Empty or <see langword="null" /> leaves the project item-less;
    ///     <see cref="IBenchmarkTaskItemStore.GetOrCreateItemsAsync" /> materializes item 0 for it on first touch.
    /// </param>
    Task<BenchmarkProjectRecord> CreateProjectAsync(BenchmarkProjectInput input,
        BenchmarkJudgePolicyChangeInput? judgePolicy = null,
        IReadOnlyList<BenchmarkTaskItemInput>? initialItems = null,
        CancellationToken cancellationToken = default);

    Task<BenchmarkProjectRecord?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BenchmarkProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Edits an unfrozen project and, with a <paramref name="judgePolicyChange" />, applies that judge change in
    ///     the SAME transaction, for the same reason <see cref="CreateProjectAsync" /> does.
    /// </summary>
    Task<BenchmarkProjectRecord> UpdateProjectAsync(Guid projectId,
        long expectedVersion,
        BenchmarkProjectInput input,
        BenchmarkJudgePolicyChangeInput? judgePolicyChange = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes a project with every run under it and everything scoped to those runs. Refused with
    ///     <c>ActiveRun</c> — nothing deleted — while any of its runs is not terminal or still holds a queued or
    ///     running work item, judge attempt or comparison.
    /// </summary>
    Task DeleteProjectAsync(Guid projectId, long expectedVersion, CancellationToken cancellationToken = default);
}
