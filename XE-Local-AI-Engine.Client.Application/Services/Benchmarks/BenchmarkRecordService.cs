namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The Benchmarks endpoints' only door onto <see cref="IBenchmarkStore" />.</summary>
/// <remarks>
///     An endpoint may not take a persistence store (the endpoint-dependency rule), so every call arrives here
///     unchanged: the rows an endpoint reads, plus the row-level writes with no other owner.
///     Deliberately a pass-through — no state, no validation, no ordering, no defaulting, every member carrying the
///     store's own parameter list and defaults — so the store keeps deciding not-found and version conflicts and the
///     endpoints keep the 404/400/409 wording, the projections and the paging arithmetic. Domain operations stay on their own services: see docs/wiki/20-benchmarks.md ("Where things live").
/// </remarks>
public sealed class BenchmarkRecordService
{
    private readonly IBenchmarkStore _store;

    public BenchmarkRecordService(IBenchmarkStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>One project by id, or null when it is gone — the 404 check in front of almost every Benchmarks read.</summary>
    public Task<BenchmarkProjectRecord?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return _store.GetProjectAsync(projectId, cancellationToken);
    }

    /// <summary>The project list behind the picker.</summary>
    public Task<IReadOnlyList<BenchmarkProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        return _store.ListProjectsAsync(cancellationToken);
    }

    /// <summary>How many runs a project has, counted in the database — the freeze indicator every detail response carries.</summary>
    public Task<int> CountRunsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return _store.CountRunsAsync(projectId, cancellationToken);
    }

    /// <summary>Every project's run count in one grouped query — the listing's freeze indicator, without a count per row.</summary>
    public Task<IReadOnlyDictionary<Guid, int>> CountRunsByProjectAsync(CancellationToken cancellationToken = default)
    {
        return _store.CountRunsByProjectAsync(cancellationToken);
    }

    /// <summary>Deletes a project against the version the operator saw.</summary>
    public Task DeleteProjectAsync(Guid projectId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        return _store.DeleteProjectAsync(projectId, expectedVersion, cancellationToken);
    }

    /// <summary>One run by id, payloads included, or null when it is gone.</summary>
    public Task<BenchmarkRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return _store.GetRunAsync(runId, cancellationToken);
    }

    /// <summary>One page of a project's runs, ranked. Never decrypts a payload: a run table is not a run detail.</summary>
    public Task<BenchmarkRunPage> ListRunsAsync(Guid projectId,
        int skip,
        int take,
        string? modelContentFingerprint = null,
        bool includeUnscored = true,
        CancellationToken cancellationToken = default)
    {
        return _store.ListRunsAsync(projectId, skip, take, modelContentFingerprint, includeUnscored, cancellationToken);
    }

    /// <summary>Removes a terminal run and everything that pointed at it, against the version the operator saw.</summary>
    public Task DeleteRunAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default)
    {
        return _store.DeleteRunAsync(runId, expectedRunVersion, cancellationToken);
    }

    /// <summary>Sets or clears the operator's 0..100 override; null clears it.</summary>
    public Task<BenchmarkRunRecord> SetUserScoreAsync(Guid runId, int? score, long expectedRunVersion, CancellationToken cancellationToken = default)
    {
        return _store.SetUserScoreAsync(runId, score, expectedRunVersion, cancellationToken);
    }

    /// <summary>A plain LIST of a project's task items, never get-or-create: materializing item 0 is a write, and only the items GET may do it.</summary>
    public Task<IReadOnlyList<BenchmarkTaskItemRecord>> ListTaskItemsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return _store.ListTaskItemsAsync(projectId, cancellationToken);
    }

    /// <summary>The project's measurement cells — the shape that ranks, and the one a comparison reads.</summary>
    public Task<BenchmarkCellPage> ListCellsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return _store.ListCellsAsync(projectId, cancellationToken);
    }

    /// <summary>One judge attempt by id, payloads included — the run detail's verdict comes from here.</summary>
    public Task<BenchmarkJudgeAttemptRecord?> GetJudgeAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        return _store.GetJudgeAttemptAsync(attemptId, cancellationToken);
    }

    /// <summary>The revision the project judges under, payload included, or null when judging is off.</summary>
    public Task<BenchmarkJudgePolicyRevisionRecord?> GetCurrentJudgePolicyRevisionAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return _store.GetCurrentJudgePolicyRevisionAsync(projectId, cancellationToken);
    }

    /// <summary>Enqueues one fidelity measurement of a run as a new attempt, never an overwrite of the last one.</summary>
    public Task<Guid> EnqueueFidelityAsync(Guid runId, string kind, CancellationToken cancellationToken = default)
    {
        return _store.EnqueueFidelityAsync(runId, kind, cancellationToken);
    }

    /// <summary>Every fidelity attempt of a run, newest sequence first — the audit trail behind the projection.</summary>
    public Task<IReadOnlyList<BenchmarkFidelityAttemptRecord>> ListFidelityAttemptsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return _store.ListFidelityAttemptsAsync(runId, cancellationToken);
    }

    /// <summary>Whether any fidelity work item is queued or running — the guard on clearing the base cache.</summary>
    public Task<bool> HasLiveFidelityWorkAsync(CancellationToken cancellationToken = default)
    {
        return _store.HasLiveFidelityWorkAsync(cancellationToken);
    }

    /// <summary>The project's pairwise cohort scope, its eligible runs and the comparisons that already exist, in one read.</summary>
    public Task<BenchmarkPairwiseCohortState> GetPairwiseCohortAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return _store.GetPairwiseCohortAsync(projectId, cancellationToken);
    }

    /// <summary>The active fit of the project's current cohort scope, or null when nothing has published one.</summary>
    public Task<BenchmarkPairwiseFitRecord?> GetActivePairwiseFitAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return _store.GetActivePairwiseFitAsync(projectId, cancellationToken);
    }
}
