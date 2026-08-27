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
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return ToRecord(entity, frozen: false);
        }

        // The project, its judge and its items are ONE creation. Staged saves inside one transaction are what the
        // circular project↔revision pointers force: project with a null pointer, then the revision, then the pointer.
        // The items ride the same transaction so a project never exists without a question to ask — which is what lets
        // every read path stop inventing one.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _dbContext.BenchmarkProjects.Add(entity);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        if (judgePolicy is not null)
        {
            await ApplyJudgePolicyChangeAsync(entity, judgePolicy, now, cancellationToken).ConfigureAwait(false);
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

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(entity, frozen: false);
    }

    public async Task<BenchmarkProjectRecord?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _dbContext.BenchmarkProjects.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        var frozen = await _dbContext.BenchmarkRuns.AnyAsync(entity => entity.ProjectId == projectId, cancellationToken).ConfigureAwait(false);
        return ToRecord(project, frozen);
    }

    public async Task<IReadOnlyList<BenchmarkProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var projects = await _dbContext.BenchmarkProjects.AsNoTracking().OrderBy(entity => entity.Name).ThenBy(entity => entity.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var frozenIds = await _dbContext.BenchmarkRuns.AsNoTracking().Select(entity => entity.ProjectId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
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
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(project.Version, expectedVersion);
        if (await _dbContext.BenchmarkRuns.AnyAsync(entity => entity.ProjectId == projectId, cancellationToken).ConfigureAwait(false))
        {
            throw new BenchmarkConflictException("ProjectFrozen");
        }

        var now = Now();
        await SyncFirstItemPromptAsync(project, input.CoreTaskJson, now, cancellationToken).ConfigureAwait(false);
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
            await ApplyJudgePolicyChangeAsync(project, judgePolicyChange, now, cancellationToken).ConfigureAwait(false);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(project, frozen: false);
    }

    public async Task DeleteProjectAsync(Guid projectId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(project.Version, expectedVersion);
        if (await _dbContext.BenchmarkRuns.AnyAsync(entity => entity.ProjectId == projectId, cancellationToken).ConfigureAwait(false))
        {
            throw new BenchmarkConflictException("ProjectFrozen");
        }

        // Same explicit order as run deletion, for the same reason: the project stops pointing at its revision
        // before the revisions go, and nothing relies on a cascade that this database does not enforce.
        project.CurrentJudgePolicyRevisionId = null;
        await SaveAsync(cancellationToken).ConfigureAwait(false);

        // The guard above refuses a project that still holds runs, so every run-scoped child (work items, judge and
        // fidelity attempts, comparisons) went with its run. What is scoped to the PROJECT did not: task items hold
        // encrypted prompts, reference answers and verifier overrides and outlive every run, and a pairwise fit is
        // only DEACTIVATED when the runs it was fitted over are deleted. Both are children of the project row, so
        // both go before it.
        await _dbContext.BenchmarkTaskItems.Where(entity => entity.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _dbContext.BenchmarkPairwiseFits.Where(entity => entity.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _dbContext.BenchmarkJudgePolicyRevisions.Where(entity => entity.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        _ = await _dbContext.BenchmarkProjects.Where(entity => entity.Id == projectId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
