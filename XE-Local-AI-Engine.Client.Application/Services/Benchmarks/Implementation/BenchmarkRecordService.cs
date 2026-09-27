namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The Benchmarks endpoints' only door onto <see cref="IBenchmarkStore" />.</summary>
/// <remarks>
///     An endpoint may not take a persistence store (the endpoint-dependency rule), so its reads and ownerless row writes
///     arrive here unchanged. A pass-through (no state, validation, ordering or defaulting; the store's own parameters)
///     so the store keeps deciding not-found and version conflicts and the endpoints keep the 404/400/409 wording; the
///     one composite read is <see cref="GetProjectDetailAsync" />. Domain operations stay on their own services: see
///     docs/wiki/20-benchmarks.md ("Where things live").
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

    /// <summary>
    ///     The whole project detail in one place: the decrypted judge policy and the task items, so a create, an edit, a
    ///     judge change and a plain GET can never disagree about what a project detail carries.
    /// </summary>
    public async Task<BenchmarkProjectDetail> GetProjectDetailAsync(BenchmarkProjectRecord project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var revision = await _store.GetCurrentJudgePolicyRevisionAsync(project.Id, cancellationToken);
        var policy = revision?.PolicyJson is { } payload && !payload.IsEmpty
            ? BenchmarkJudgeSerialization.DeserializePolicy(payload.Span)
            : null;
        return new BenchmarkProjectDetail
        {
            Project = project,
            JudgePolicyRevision = revision,
            JudgePolicy = policy,

            // A plain LIST, never get-or-create: materializing item 0 for a project that has none is a write, and exactly one endpoint may perform it — the items GET.
            // Every other project read stays a read, so a page refresh cannot race two item-0 rows into existence.
            TaskItems = await _store.ListTaskItemsAsync(project.Id, cancellationToken)
        };
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
}
