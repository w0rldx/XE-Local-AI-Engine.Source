namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>A benchmark project's task items. One role of <c>BenchmarkStore</c>, registered as the same scoped instance as its siblings.</summary>
public interface IBenchmarkTaskItemStore
{
    /// <summary>Every task item of a project — generators included — ordered by <see cref="BenchmarkTaskItemRecord.Index" />.</summary>
    Task<IReadOnlyList<BenchmarkTaskItemRecord>> ListTaskItemsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The project's task items, materializing item 0 from <see cref="BenchmarkProjectRecord.CoreTaskJson" /> when
    ///     it has none — a LEGACY path only.
    /// </summary>
    /// <remarks>
    ///     A project created after task items existed gets them in the same transaction as itself, so the
    ///     write-on-a-read-path is unreachable from anything an operator can newly create. It runs inside the normal EF
    ///     write path so both encryption interceptors fire, which is why a migration cannot do this instead: it has no
    ///     node key, and the prompt is a required encrypted blob bound to its own item's id. Idempotent under the
    ///     unique (project, index) index, and it leaves <see cref="BenchmarkProjectRecord.TaskItemSetHash" /> null.
    /// </remarks>
    Task<IReadOnlyList<BenchmarkTaskItemRecord>> GetOrCreateItemsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Appends one task item and recomputes the project's item-set hash.
    /// </summary>
    /// <remarks>
    ///     A changed set hash resets the rank cohort, through the same path a judge-policy activation uses: the project
    ///     score is a mean over the item set, so changing the set changes what the score means. A generator's leaf case
    ///     is an ordinary item with its own id, so every cap, every hash and the export reach it without knowing what
    ///     generated it.
    /// </remarks>
    /// <param name="children">
    ///     The leaf cases a generator item expands into, written in the SAME transaction as the generator, which
    ///     therefore never exists without the cases it promises.
    /// </param>
    Task<BenchmarkTaskItemRecord> CreateTaskItemAsync(Guid projectId,
        long expectedProjectVersion,
        BenchmarkTaskItemInput input,
        IReadOnlyList<BenchmarkTaskItemInput>? children = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Rewrites one task item's payloads, bumping its <see cref="BenchmarkTaskItemRecord.Revision" /> and
    ///     recomputing its <see cref="BenchmarkTaskItemRecord.InputHash" />.
    /// </summary>
    /// <remarks>
    ///     The new hash is what makes every stored answer to the OLD instance identifiable as an answer to a question
    ///     that no longer exists. Regenerating children atomically is the point too: a case must never be left
    ///     describing parameters its generator no longer has.
    /// </remarks>
    /// <param name="children">
    ///     A generator's cases, REGENERATED: the item's existing children are deleted and these written in their
    ///     place, inside the same transaction as the edit.
    /// </param>
    Task<BenchmarkTaskItemRecord> UpdateTaskItemAsync(Guid projectId,
        Guid itemId,
        long expectedItemVersion,
        BenchmarkTaskItemInput input,
        IReadOnlyList<BenchmarkTaskItemInput>? children = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes one task item, and a generator's children before the generator itself.
    /// </summary>
    /// <remarks>
    ///     Nothing cascades here, so that order is what makes the delete legal. Deleting the last leaf is refused: a
    ///     project always asks at least one question.
    /// </remarks>
    Task DeleteTaskItemAsync(Guid projectId, Guid itemId, long expectedItemVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Renumbers the project's items into the given order.
    /// </summary>
    /// <remarks>
    ///     Deliberately NOT a revision bump and NOT a cohort reset: the index is a display position, absent from every
    ///     hash, and a drag-and-drop must not unrank a completed suite. <paramref name="orderedItemIds" /> must name
    ///     exactly the project's current items, which is also this call's concurrency check.
    /// </remarks>
    Task<IReadOnlyList<BenchmarkTaskItemRecord>> ReorderTaskItemsAsync(Guid projectId,
        IReadOnlyList<Guid> orderedItemIds,
        CancellationToken cancellationToken = default);
}
