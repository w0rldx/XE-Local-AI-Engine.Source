namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    public async Task<IReadOnlyList<BenchmarkTaskItemRecord>> ListTaskItemsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        [.. (await TaskItemsAsync(projectId, tracking: false, cancellationToken).ConfigureAwait(false)).Select(ToRecord)];

    public async Task<IReadOnlyList<BenchmarkTaskItemRecord>> GetOrCreateItemsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await ListTaskItemsAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            return existing;
        }

        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var now = Now();
        var item = NewTaskItem(projectId,
            new BenchmarkTaskItemInput(project.CoreTaskJson),
            index: 0,
            now);

        // The project's set hash is deliberately NOT written here. Materializing item 0 changes nothing about what the
        // project asks, and moving the hash every historical run is compared against would unrank the whole project's
        // history for a bookkeeping write. It moves on the first real item edit, where unranking is the correct answer.
        _dbContext.BenchmarkTaskItems.Add(item);
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BenchmarkConflictException exception) when (string.Equals(exception.Code, "DuplicateWork", StringComparison.Ordinal))
        {
            // Two concurrent readers of one legacy project both tried to materialize item 0. The unique
            // (project_id, index) index turns that into a constraint violation rather than a second item 0, so the
            // loser simply reads what the winner wrote.
            _dbContext.ChangeTracker.Clear();
            return await ListTaskItemsAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        return [ToRecord(item)];
    }

    public async Task<BenchmarkTaskItemRecord> CreateTaskItemAsync(Guid projectId,
        long expectedProjectVersion,
        BenchmarkTaskItemInput input,
        IReadOnlyList<BenchmarkTaskItemInput>? children = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTaskItem(input);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(project.Version, expectedProjectVersion);
        await EnsureNoActiveProjectWorkAsync(projectId, cancellationToken).ConfigureAwait(false);

        var items = await TaskItemsAsync(projectId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (input.ParentItemId is { } parentId && items.All(entity => entity.Id != parentId))
        {
            throw new BenchmarkValidationException("The parent task item does not belong to this project.");
        }

        var now = Now();

        // Indices are never renumbered on delete, so the next one is one past the highest — a gap is fine and is
        // cheaper than rewriting every sibling row to close it.
        var nextIndex = items.Count == 0 ? 0 : items.Max(entity => entity.Index) + 1;
        var item = NewTaskItem(projectId, input, nextIndex, now);
        _dbContext.BenchmarkTaskItems.Add(item);
        var written = new List<BenchmarkTaskItem>(items)
        {
            item
        };
        written.AddRange(AddChildren(projectId, item.Id, children, nextIndex + 1, now));
        await ApplyItemSetChangeAsync(project, written, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(item);
    }

    public async Task<BenchmarkTaskItemRecord> UpdateTaskItemAsync(Guid projectId,
        Guid itemId,
        long expectedItemVersion,
        BenchmarkTaskItemInput input,
        IReadOnlyList<BenchmarkTaskItemInput>? children = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTaskItem(input);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        await EnsureNoActiveProjectWorkAsync(projectId, cancellationToken).ConfigureAwait(false);
        var items = await TaskItemsAsync(projectId, tracking: true, cancellationToken).ConfigureAwait(false);
        var item = items.SingleOrDefault(entity => entity.Id == itemId) ?? throw new BenchmarkNotFoundException("Benchmark task item was not found.");
        EnsureVersion(item.Version, expectedItemVersion);
        if (!string.Equals(item.Kind, input.Kind, StringComparison.Ordinal))
        {
            // A kind change would turn a run target into a generator (or the reverse) under a stable id, which is a
            // different item wearing the old identity. Delete and re-create instead, where the set hash moves for it.
            throw new BenchmarkValidationException("A task item's kind cannot be changed. Delete the item and create the new one.");
        }

        var now = Now();
        item.PromptJson = input.PromptJson.ToArray();
        item.ReferenceAnswerJson = OptionalPayload(input.ReferenceAnswerJson);
        item.VerifierConfigJson = OptionalPayload(input.VerifierConfigJson);
        item.GeneratorConfigJson = OptionalPayload(input.GeneratorConfigJson);
        item.CountsTowardScore = input.CountsTowardScore;

        // The revision is inside the input hash, so a payload that is edited BACK to its previous bytes still reads as
        // a different question — which is right: the answers in between were given to something else.
        item.Revision = checked(item.Revision + 1);
        item.InputHash = BenchmarkTaskItemHashing.ComputeInputHash(item);
        item.Version++;
        item.UpdatedAtUtc = now;

        // A generator's cases are regenerated, not patched: the old rows go and the new ones are written in this same
        // transaction, so no case is ever left describing parameters its generator no longer has. The replacements
        // take fresh indices past the highest rather than reusing the vacated ones — the unique (project, index)
        // index is enforced per statement, and a reused index collides with a row EF has not deleted yet.
        var survivors = items;
        if (children is not null)
        {
            var doomed = items.Where(entity => entity.ParentItemId == itemId).ToArray();
            _dbContext.BenchmarkTaskItems.RemoveRange(doomed);
            survivors = [.. items.Where(entity => Array.IndexOf(doomed, entity) < 0)];
            survivors.AddRange(AddChildren(projectId, itemId, children, items.Max(entity => entity.Index) + 1, now));
        }

        await ApplyItemSetChangeAsync(project, survivors, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(item);
    }

    /// <summary>
    ///     Writes a generator's cases as ordinary items pointing back at it. Called inside the generator's own
    ///     transaction, never on its own.
    /// </summary>
    private List<BenchmarkTaskItem> AddChildren(Guid projectId,
        Guid parentItemId,
        IReadOnlyList<BenchmarkTaskItemInput>? children,
        int firstIndex,
        long now)
    {
        var written = new List<BenchmarkTaskItem>(children?.Count ?? 0);
        foreach (var child in children ?? [])
        {
            ValidateTaskItem(child);
            var row = NewTaskItem(projectId, child with
            {
                ParentItemId = parentItemId
            }, firstIndex + written.Count, now);
            _dbContext.BenchmarkTaskItems.Add(row);
            written.Add(row);
        }

        return written;
    }

    public async Task DeleteTaskItemAsync(Guid projectId, Guid itemId, long expectedItemVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        await EnsureNoActiveProjectWorkAsync(projectId, cancellationToken).ConfigureAwait(false);
        var items = await TaskItemsAsync(projectId, tracking: true, cancellationToken).ConfigureAwait(false);
        var item = items.SingleOrDefault(entity => entity.Id == itemId) ?? throw new BenchmarkNotFoundException("Benchmark task item was not found.");
        EnsureVersion(item.Version, expectedItemVersion);

        // A generator goes with its children, so the doomed set is the item plus everything it expanded into.
        var doomed = items.Where(entity => entity.Id == itemId || entity.ParentItemId == itemId).ToArray();
        var survivors = items.Where(entity => Array.IndexOf(doomed, entity) < 0).ToArray();
        if (!survivors.Any(entity => BenchmarkTaskItemKinds.IsLeaf(entity.Kind)))
        {
            throw new BenchmarkValidationException("A benchmark project must keep at least one task item.");
        }

        // Foreign keys are off on this connection and no cascade fires, so this order IS the referential integrity:
        // children before the parent they point at.
        _dbContext.BenchmarkTaskItems.RemoveRange(doomed.Where(entity => entity.ParentItemId == itemId));
        _dbContext.BenchmarkTaskItems.Remove(item);
        await ApplyItemSetChangeAsync(project, survivors, Now(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BenchmarkTaskItemRecord>> ReorderTaskItemsAsync(Guid projectId,
        IReadOnlyList<Guid> orderedItemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedItemIds);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var items = await TaskItemsAsync(projectId, tracking: true, cancellationToken).ConfigureAwait(false);

        // Naming exactly the current set is this call's concurrency check: an item added or deleted while the operator
        // was dragging makes the two sets disagree, and the reorder is refused instead of renumbering a stale list.
        if (orderedItemIds.Count != items.Count || orderedItemIds.Distinct().Count() != orderedItemIds.Count
                                                || orderedItemIds.Any(id => items.All(entity => entity.Id != id)))
        {
            throw new BenchmarkConflictException("VersionConflict");
        }

        var now = Now();

        // Two passes over disjoint index ranges: the unique (project_id, index) index is enforced per statement, so
        // renumbering in place would collide with a row that has not moved yet.
        var offset = items.Max(entity => entity.Index) + 1;
        for (var position = 0; position < orderedItemIds.Count; position++)
        {
            items.Single(entity => entity.Id == orderedItemIds[position]).Index = offset + position;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        for (var position = 0; position < orderedItemIds.Count; position++)
        {
            var item = items.Single(entity => entity.Id == orderedItemIds[position]);
            item.Index = position;
            item.Version++;
            item.UpdatedAtUtc = now;
        }

        // No revision bump, no set-hash change and no cohort reset, all deliberately: the index is a display position
        // that no hash carries, and a drag-and-drop must not unrank a completed suite.
        project.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. items.OrderBy(static entity => entity.Index).Select(ToRecord)];
    }

    /// <summary>
    ///     Keeps the project's core task and its FIRST item asking the same question. The project edit is refused once
    ///     the project has runs, so this is only ever reached while there is no history to unrank — which is why it
    ///     bumps the item's revision and the set hash without resetting a cohort that cannot exist yet.
    /// </summary>
    private async Task SyncFirstItemPromptAsync(BenchmarkProject project, ReadOnlyMemory<byte> coreTaskJson, long now, CancellationToken cancellationToken)
    {
        if (project.CoreTaskJson.AsSpan().SequenceEqual(coreTaskJson.Span))
        {
            return;
        }

        var items = await TaskItemsAsync(project.Id, tracking: true, cancellationToken).ConfigureAwait(false);
        var first = items.Where(static entity => BenchmarkTaskItemKinds.IsLeaf(entity.Kind)).MinBy(static entity => entity.Index);
        if (first is null)
        {
            return;
        }

        first.PromptJson = coreTaskJson.ToArray();
        first.Revision = checked(first.Revision + 1);
        first.InputHash = BenchmarkTaskItemHashing.ComputeInputHash(first);
        first.Version++;
        first.UpdatedAtUtc = now;
        project.TaskItemSetHash = BenchmarkTaskItemHashing.ComputeSetHash(items);
    }

    /// <summary>
    ///     Recomputes the project's item-set hash over <paramref name="items" /> and, when it MOVED, bumps the project
    ///     version and resets the rank cohort — the same reset a judge-policy activation performs, and for the same
    ///     reason: the project score is a mean over the item set, so a different set is a different score.
    /// </summary>
    private async Task ApplyItemSetChangeAsync(BenchmarkProject project,
        IReadOnlyCollection<BenchmarkTaskItem> items,
        long now,
        CancellationToken cancellationToken)
    {
        var setHash = BenchmarkTaskItemHashing.ComputeSetHash(items);
        var moved = !string.Equals(project.TaskItemSetHash, setHash, StringComparison.Ordinal);
        project.TaskItemSetHash = setHash;
        project.UpdatedAtUtc = now;
        if (moved)
        {
            project.Version++;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        if (moved)
        {
            await ResetCurrentCohortAsync(project.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Refuses an item write while any of the project's work is queued or running. The ranking read's staleness
    ///     exclusions are a safety net for history; this is the primary guard, and it is what keeps a run from being
    ///     frozen against one revision of an item and judged against another.
    /// </summary>
    private async Task EnsureNoActiveProjectWorkAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var active = await (from work in _dbContext.BenchmarkWorkItems.AsNoTracking()
            join run in _dbContext.BenchmarkRuns.AsNoTracking() on work.RunId equals run.Id
            where run.ProjectId == projectId && (work.Status == BenchmarkWorkStatus.Queued || work.Status == BenchmarkWorkStatus.Running)
            select work.QueueSequence).AnyAsync(cancellationToken).ConfigureAwait(false);
        if (active)
        {
            throw new BenchmarkConflictException("ActiveRun");
        }
    }

    private async Task<List<BenchmarkTaskItem>> TaskItemsAsync(Guid projectId, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? _dbContext.BenchmarkTaskItems.AsQueryable() : _dbContext.BenchmarkTaskItems.AsNoTracking();
        return await query.Where(entity => entity.ProjectId == projectId)
                          .OrderBy(entity => entity.Index)
                          .ThenBy(entity => entity.Id)
                          .ToListAsync(cancellationToken)
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     A new item with the store-owned fields filled in. The input hash is computed here and nowhere else, so a
    ///     caller cannot present one — the value is what the ranking read trusts to say what a run was asked.
    /// </summary>
    private static BenchmarkTaskItem NewTaskItem(Guid projectId, BenchmarkTaskItemInput input, int index, long now)
    {
        var item = new BenchmarkTaskItem
        {
            Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id,
            ProjectId = projectId,
            ParentItemId = input.ParentItemId,
            Index = index,
            Kind = input.Kind,
            Revision = 1,
            CountsTowardScore = input.CountsTowardScore,
            PromptJson = input.PromptJson.ToArray(),
            ReferenceAnswerJson = OptionalPayload(input.ReferenceAnswerJson),
            VerifierConfigJson = OptionalPayload(input.VerifierConfigJson),
            GeneratorConfigJson = OptionalPayload(input.GeneratorConfigJson),
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        item.InputHash = BenchmarkTaskItemHashing.ComputeInputHash(item);
        return item;
    }

    /// <summary>
    ///     An absent optional payload is NULL, never an empty blob. The two are indistinguishable to a reader once
    ///     encrypted, and "this item has no reference answer" is a different fact from "its reference answer is empty"
    ///     — the second one participates in the input hash and would make an untouched item look edited.
    /// </summary>
    private static byte[]? OptionalPayload(ReadOnlyMemory<byte>? payload) =>
        payload is { IsEmpty: false } value ? value.ToArray() : null;

    private static void ValidateTaskItem(BenchmarkTaskItemInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.PromptJson.Length == 0 || !BenchmarkTaskItemKinds.IsKnown(input.Kind))
        {
            throw new BenchmarkValidationException("Benchmark task item input is invalid.");
        }
    }

    private static BenchmarkTaskItemRecord ToRecord(BenchmarkTaskItem entity) =>
        new(entity.Id, entity.ProjectId, entity.ParentItemId, entity.Index, entity.Kind, entity.Revision, entity.InputHash,
            entity.CountsTowardScore, entity.PromptJson.ToArray(), entity.ReferenceAnswerJson?.ToArray(),
            entity.VerifierConfigJson?.ToArray(), entity.GeneratorConfigJson?.ToArray(),
            entity.Version, entity.CreatedAtUtc, entity.UpdatedAtUtc);
}
