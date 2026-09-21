namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    public async Task<BenchmarkProjectRecord> CreateProjectAsync(BenchmarkProjectInput input,
        BenchmarkJudgePolicyChangeInput? judgePolicy = null,
        IReadOnlyList<BenchmarkTaskItemInput>? initialItems = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(input);
        var now = Now();
        var entity = new BenchmarkProject
        {
            Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id,
            Name = input.Name.Trim(),
            CoreTaskJson = input.CoreTaskJson.ToArray(),
            ContextTokens = input.ContextTokens,
            MaxOutputTokens = input.MaxOutputTokens,
            ReasoningBudgetTokens = input.ReasoningBudgetTokens,
            InvocationTimeoutSeconds = input.InvocationTimeoutSeconds,
            AgentDefinitionId = input.AgentDefinitionId,
            FidelityEnabled = input.FidelityEnabled,
            FidelityKldEnabled = input.FidelityKldEnabled,
            FidelityChunks = input.FidelityChunks,
            FidelityKldBaseModelName = input.FidelityKldBaseModelName,
            FidelityKldBaseFingerprint = input.FidelityKldBaseFingerprint,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        if (judgePolicy is null && initialItems is not { Count: > 0 })
        {
            _dbContext.BenchmarkProjects.Add(entity);
            await SaveAsync(cancellationToken);
            return ToRecord(entity, frozen: false);
        }

        // The project, its judge and its items are ONE creation. Staged saves inside one transaction are what the circular project↔revision pointers force: project
        // with a null pointer, then the revision, then the pointer. The items ride along so a project never exists without a question to ask, and no read invents one.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        _dbContext.BenchmarkProjects.Add(entity);
        await SaveAsync(cancellationToken);
        if (judgePolicy is not null)
        {
            await ApplyJudgePolicyChangeAsync(entity, judgePolicy, now, cancellationToken);
        }

        if (initialItems is { Count: > 0 })
        {
            var created = new List<BenchmarkTaskItem>(initialItems.Count);
            for (var index = 0; index < initialItems.Count; index++)
            {
                created.Add(NewTaskItem(entity.Id, initialItems[index], index, now));
            }

            _dbContext.BenchmarkTaskItems.AddRange(created);
            entity.TaskItemSetHash = BenchmarkTaskItemHashing.ComputeSetHash(created);
        }

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToRecord(entity, frozen: false);
    }

    public async Task<BenchmarkProjectRecord?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _dbContext.BenchmarkProjects.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken);
        if (project is null)
        {
            return null;
        }

        var frozen = await _dbContext.BenchmarkRuns.AnyAsync(entity => entity.ProjectId == projectId, cancellationToken);
        return ToRecord(project, frozen);
    }

    public async Task<IReadOnlyList<BenchmarkProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var projects = await _dbContext.BenchmarkProjects.AsNoTracking().OrderBy(entity => entity.Name).ThenBy(entity => entity.Id).ToListAsync(cancellationToken);
        var frozenIds = await _dbContext.BenchmarkRuns.AsNoTracking().Select(entity => entity.ProjectId).Distinct().ToListAsync(cancellationToken);
        var frozen = frozenIds.ToHashSet();
        return projects.Select(entity => ToRecord(entity, frozen.Contains(entity.Id))).ToArray();
    }

    public async Task<BenchmarkProjectRecord> UpdateProjectAsync(Guid projectId,
        long expectedVersion,
        BenchmarkProjectInput input,
        BenchmarkJudgePolicyChangeInput? judgePolicyChange = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(input);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var project = await RequireProjectAsync(projectId, cancellationToken);
        EnsureVersion(project.Version, expectedVersion);
        if (await _dbContext.BenchmarkRuns.AnyAsync(entity => entity.ProjectId == projectId, cancellationToken))
        {
            throw new BenchmarkConflictException("ProjectFrozen");
        }

        var now = Now();
        await SyncFirstItemPromptAsync(project, input.CoreTaskJson, now, cancellationToken);
        project.Name = input.Name.Trim();
        project.CoreTaskJson = input.CoreTaskJson.ToArray();
        project.ContextTokens = input.ContextTokens;
        project.MaxOutputTokens = input.MaxOutputTokens;
        project.ReasoningBudgetTokens = input.ReasoningBudgetTokens;
        project.InvocationTimeoutSeconds = input.InvocationTimeoutSeconds;
        project.AgentDefinitionId = input.AgentDefinitionId;
        project.FidelityEnabled = input.FidelityEnabled;
        project.FidelityKldEnabled = input.FidelityKldEnabled;
        project.FidelityChunks = input.FidelityChunks;
        project.FidelityKldBaseModelName = input.FidelityKldBaseModelName;
        project.FidelityKldBaseFingerprint = input.FidelityKldBaseFingerprint;
        project.Version++;
        project.UpdatedAtUtc = now;
        if (judgePolicyChange is not null)
        {
            // Same transaction as the field edit: an edit that committed without its judge change would leave the
            // project judging under a policy the operator has just replaced.
            await ApplyJudgePolicyChangeAsync(project, judgePolicyChange, now, cancellationToken);
        }

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToRecord(project, frozen: false);
    }

    /// <summary>
    ///     Deletes a project with everything under it: every run and all the run-scoped evidence, then the
    ///     project-scoped rows, then the project.
    /// </summary>
    /// <remarks>
    ///     Refused with <c>ActiveRun</c> while ANY run is still in play — not terminal, or holding a queued or running work item, judge attempt or comparison — and
    ///     refused whole rather than partially applied, so the operator never loses half a project. The version bump is the FIRST write on purpose: it reserves
    ///     SQLite's single writer before the run set is read, the same idiom as <c>AcquireWorkCompletionAsync</c>, so a concurrent run is either seen by the guard or
    ///     refused by its own compare-and-swap in <c>StartRunsAsync</c>. Reading first would let a run land in the gap.
    /// </remarks>
    public async Task DeleteProjectAsync(Guid projectId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var project = await RequireProjectAsync(projectId, cancellationToken);
        EnsureVersion(project.Version, expectedVersion);
        project.Version++;
        project.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken);

        var runs = await _dbContext.BenchmarkRuns.AsNoTracking()
                                   .Where(entity => entity.ProjectId == projectId)
                                   .Select(entity => new
                                   {
                                       entity.Id,
                                       entity.PrimaryStatus
                                   })
                                   .ToArrayAsync(cancellationToken);
        foreach (var run in runs)
        {
            if (await IsRunActiveAsync(run.Id, run.PrimaryStatus, cancellationToken))
            {
                // Nothing has been committed, so the version bump above is rolled back with everything else.
                throw new BenchmarkConflictException("ActiveRun");
            }
        }

        // The SAME per-run deletion the single-run delete performs, once per run in this transaction, every run checked first. Re-read one at a time: DeleteRunCoreAsync
        // clears the tracker, so a detached instance skips its judge-pointer write. simplified: one pass per run (a few thousand statements for 400); set-based would be one.
        foreach (var run in runs)
        {
            await DeleteRunCoreAsync(await RequireRunAsync(run.Id, tracking: true, cancellationToken), cancellationToken);
        }

        // Same explicit order as run deletion, for the same reason: the project stops pointing at its revision
        // before the revisions go, and nothing relies on a cascade that this database does not enforce.
        project = await RequireProjectAsync(projectId, cancellationToken);
        project.CurrentJudgePolicyRevisionId = null;
        await SaveAsync(cancellationToken);

        // Every run-scoped child (work items, judge and fidelity attempts, comparisons) went with its run above; what is scoped to the PROJECT did not. Task items
        // hold encrypted prompts, answers and overrides and outlive every run, and a pairwise fit is only DEACTIVATED by a run delete. Both go before the project row.
        await _dbContext.BenchmarkTaskItems.Where(entity => entity.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await _dbContext.BenchmarkPairwiseFits.Where(entity => entity.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        await _dbContext.BenchmarkJudgePolicyRevisions.Where(entity => entity.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        _dbContext.ChangeTracker.Clear();
        _ = await _dbContext.BenchmarkProjects.Where(entity => entity.Id == projectId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
