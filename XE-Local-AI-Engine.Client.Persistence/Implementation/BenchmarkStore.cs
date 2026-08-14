namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class BenchmarkStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IBenchmarkStore
{
    private const string InterruptedMessage = "Interrupted by application restart.";
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<BenchmarkProjectRecord> CreateProjectAsync(BenchmarkProjectInput input, CancellationToken cancellationToken = default)
    {
        ValidateProject(input);
        var now = Now();
        var entity = new BenchmarkProject
        {
            Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id,
            Name = input.Name.Trim(),
            CoreTaskJson = input.CoreTaskJson.ToArray(),
            ContextTokens = input.ContextTokens,
            AgentDefinitionId = input.AgentDefinitionId,
            JudgeEnabled = input.JudgeEnabled,
            JudgeModelName = NormalizeOptional(input.JudgeModelName),
            JudgeContextTokens = input.JudgeContextTokens,
            JudgePromptVersion = input.JudgePromptVersion,
            JudgeOutputSchemaVersion = input.JudgeOutputSchemaVersion,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _dbContext.BenchmarkProjects.Add(entity);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
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

        project.Name = input.Name.Trim();
        project.CoreTaskJson = input.CoreTaskJson.ToArray();
        project.ContextTokens = input.ContextTokens;
        project.AgentDefinitionId = input.AgentDefinitionId;
        project.JudgeEnabled = input.JudgeEnabled;
        project.JudgeModelName = NormalizeOptional(input.JudgeModelName);
        project.JudgeContextTokens = input.JudgeContextTokens;
        project.JudgePromptVersion = input.JudgePromptVersion;
        project.JudgeOutputSchemaVersion = input.JudgeOutputSchemaVersion;
        project.Version++;
        project.UpdatedAtUtc = Now();
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

        _dbContext.BenchmarkProjects.Remove(project);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkRunRecord> StartRunAsync(BenchmarkStartRunCommand command, CancellationToken cancellationToken = default)
    {
        ValidateStart(command);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireProjectAsync(command.ProjectId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(project.Version, command.ExpectedProjectVersion);
        if (project.JudgeEnabled != command.JudgeEnabled)
        {
            throw new BenchmarkConflictException("FreezeDependencyChanged");
        }

        if (command.FreezeCommitGuard is not null
            && !await command.FreezeCommitGuard.IsCurrentAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new BenchmarkConflictException("FreezeDependencyChanged");
        }

        var now = Now();
        var run = new BenchmarkRun
        {
            Id = command.RunId == Guid.Empty ? Guid.NewGuid() : command.RunId,
            ProjectId = command.ProjectId,
            RuntimeSnapshotJson = command.RuntimeSnapshotJson.ToArray(),
            PrimaryModelName = command.PrimaryModelName.Trim(),
            PrimaryModelOrigin = command.PrimaryModelOrigin,
            ModelContentFingerprint = command.ModelContentFingerprint,
            AgentName = command.AgentName.Trim(),
            AgentVersion = command.AgentVersion,
            RequestedContextTokens = command.RequestedContextTokens,
            PrimaryStatus = BenchmarkPrimaryStatus.Queued,
            JudgeStatus = command.JudgeEnabled ? BenchmarkJudgeStatus.Pending : BenchmarkJudgeStatus.Disabled,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var work = new BenchmarkWorkItem
        {
            RunId = run.Id,
            Kind = BenchmarkWorkKind.Primary,
            Status = BenchmarkWorkStatus.Queued,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = now
        };
        project.Version++;
        project.UpdatedAtUtc = now;
        _dbContext.BenchmarkRuns.Add(run);
        _dbContext.BenchmarkWorkItems.Add(work);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run);
    }

    public async Task<BenchmarkRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        (await _dbContext.BenchmarkRuns.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken).ConfigureAwait(false)) is { } entity
            ? ToRecord(entity)
            : null;

    public async Task<IReadOnlyList<BenchmarkRunRecord>> ListRunsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        (await _dbContext.BenchmarkRuns.AsNoTracking()
                         .Where(entity => entity.ProjectId == projectId)
                         .OrderByDescending(entity => entity.CreatedAtUtc)
                         .ThenByDescending(entity => entity.Id)
                         .ToListAsync(cancellationToken)
                         .ConfigureAwait(false)).Select(ToRecord).ToArray();

    public async Task<BenchmarkClaimedWork?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var candidate = await _dbContext.BenchmarkWorkItems.AsNoTracking()
                                            .Where(entity => entity.Status == BenchmarkWorkStatus.Queued)
                                            .OrderBy(entity => entity.QueueSequence)
                                            .Select(entity => new { entity.QueueSequence, entity.Version })
                                            .FirstOrDefaultAsync(cancellationToken)
                                            .ConfigureAwait(false);
            if (candidate is null)
            {
                return null;
            }

            var now = Now();
            var nextVersion = candidate.Version + 1;
            var claimed = await _dbContext.BenchmarkWorkItems
                                          .Where(entity => entity.QueueSequence == candidate.QueueSequence
                                                           && entity.Version == candidate.Version
                                                           && entity.Status == BenchmarkWorkStatus.Queued)
                                          .ExecuteUpdateAsync(setters => setters
                                              .SetProperty(entity => entity.Status, BenchmarkWorkStatus.Running)
                                              .SetProperty(entity => entity.StartedAtUtc, now)
                                              .SetProperty(entity => entity.Version, nextVersion), cancellationToken)
                                          .ConfigureAwait(false);
            if (claimed == 0)
            {
                continue;
            }

            // ExecuteUpdate bypasses the change tracker. A scoped store may still be tracking the just-enqueued work
            // item at its previous version, so reload from the database before the lifecycle transition is composed.
            _dbContext.ChangeTracker.Clear();
            var work = await _dbContext.BenchmarkWorkItems.AsNoTracking().SingleAsync(entity => entity.QueueSequence == candidate.QueueSequence, cancellationToken).ConfigureAwait(false);
            var run = await RequireRunAsync(work.RunId, tracking: true, cancellationToken).ConfigureAwait(false);
            if (work.Kind == BenchmarkWorkKind.Primary)
            {
                if (run.PrimaryStatus != BenchmarkPrimaryStatus.Queued)
                {
                    throw new BenchmarkConflictException("InvalidPrimaryTransition");
                }

                run.PrimaryStatus = BenchmarkPrimaryStatus.Running;
                run.StartedAtUtc = now;
            }
            else
            {
                if (run.JudgeStatus != BenchmarkJudgeStatus.Queued)
                {
                    throw new BenchmarkConflictException("InvalidJudgeTransition");
                }

                run.JudgeStatus = BenchmarkJudgeStatus.Running;
                run.JudgeStartedAtUtc = now;
            }

            run.Version++;
            run.UpdatedAtUtc = now;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BenchmarkClaimedWork(work.QueueSequence, work.RunId, work.Kind, work.Attempt, work.Version, ToRecord(run));
        }
    }

    public async Task<BenchmarkRunRecord> MarkPrimarySucceededAsync(BenchmarkPrimarySuccessCommand command, CancellationToken cancellationToken = default)
    {
        if (command.OutputPartsJson.IsEmpty)
        {
            throw new BenchmarkValidationException("Successful primary output cannot be empty.");
        }
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireWorkCompletionAsync(command.RunId, BenchmarkWorkKind.Primary, command.ExpectedWorkVersion, cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        var run = await RequireRunAsync(command.RunId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (run.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded)
        {
            return ToRecord(run);
        }

        var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Primary, cancellationToken).ConfigureAwait(false);
        EnsureVersion(work.Version, command.ExpectedWorkVersion);
        if (run.PrimaryStatus == BenchmarkPrimaryStatus.CancelRequested)
        {
            var cancelledAt = Now();
            run.PrimaryStatus = BenchmarkPrimaryStatus.Cancelled;
            run.PrimaryErrorMessage = null;
            run.OutputPartsJson = null;
            run.LastStreamSequence = 0;
            run.EffectiveContextTokens = null;
            run.DurationMs = null;
            run.TotalTokens = null;
            run.TokensPerSecond = null;
            run.PrimaryCompletedAtUtc = cancelledAt;
            run.Version++;
            run.UpdatedAtUtc = cancelledAt;
            SkipPendingJudge(run);
            TerminalizeWork(work, BenchmarkWorkStatus.Cancelled, errorMessage: null, cancelledAt);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToRecord(run);
        }

        EnsurePrimaryState(run, BenchmarkPrimaryStatus.Running);
        var now = Now();
        run.PrimaryStatus = BenchmarkPrimaryStatus.Succeeded;
        run.OutputPartsJson = command.OutputPartsJson.ToArray();
        run.LastStreamSequence = command.LastStreamSequence;
        run.EffectiveContextTokens = command.EffectiveContextTokens;
        run.DurationMs = command.DurationMs;
        run.TotalTokens = command.TotalTokens;
        run.TokensPerSecond = command.TokensPerSecond;
        run.PrimaryCompletedAtUtc = now;
        run.Version++;
        run.UpdatedAtUtc = now;
        TerminalizeWork(work, BenchmarkWorkStatus.Succeeded, errorMessage: null, now);
        if (run.JudgeStatus == BenchmarkJudgeStatus.Pending)
        {
            run.JudgeStatus = BenchmarkJudgeStatus.Queued;
            _dbContext.BenchmarkWorkItems.Add(new BenchmarkWorkItem
            {
                RunId = run.Id,
                Kind = BenchmarkWorkKind.Judge,
                Status = BenchmarkWorkStatus.Queued,
                Attempt = 1,
                Version = 1,
                EnqueuedAtUtc = now
            });
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run);
    }

    public Task<BenchmarkRunRecord> MarkPrimaryFailedAsync(Guid runId, long expectedRunVersion, string errorMessage, CancellationToken cancellationToken = default) =>
        TerminalizePrimaryNonSuccessAsync(runId, expectedRunVersion, BenchmarkPrimaryStatus.Failed, BenchmarkWorkStatus.Failed, errorMessage, lastStreamSequence: 0, cancellationToken);

    public Task<BenchmarkRunRecord> MarkPrimaryFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        TerminalizePrimaryNonSuccessAsync(runId, expectedRunVersion, BenchmarkPrimaryStatus.Failed, BenchmarkWorkStatus.Failed, errorMessage, lastStreamSequence, cancellationToken);

    public Task<BenchmarkRunRecord> MarkPrimaryCancelledAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default) =>
        TerminalizePrimaryNonSuccessAsync(runId, expectedRunVersion, BenchmarkPrimaryStatus.Cancelled, BenchmarkWorkStatus.Cancelled, string.Empty, lastStreamSequence: 0, cancellationToken);

    public Task<BenchmarkRunRecord> MarkPrimaryCancelledAsync(Guid runId,
        long expectedRunVersion,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        TerminalizePrimaryNonSuccessAsync(runId, expectedRunVersion, BenchmarkPrimaryStatus.Cancelled, BenchmarkWorkStatus.Cancelled, string.Empty, lastStreamSequence, cancellationToken);

    public async Task<BenchmarkRunRecord> MarkJudgeSucceededAsync(BenchmarkJudgeSuccessCommand command, CancellationToken cancellationToken = default)
    {
        if (command.JudgeResultJson.IsEmpty)
        {
            throw new BenchmarkValidationException("Successful judge output cannot be empty.");
        }
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireWorkCompletionAsync(command.RunId, BenchmarkWorkKind.Judge, command.ExpectedWorkVersion, cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        var run = await RequireRunAsync(command.RunId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (run.JudgeStatus == BenchmarkJudgeStatus.Succeeded)
        {
            return ToRecord(run);
        }

        var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Judge, cancellationToken).ConfigureAwait(false);
        EnsureVersion(work.Version, command.ExpectedWorkVersion);
        EnsureJudgeState(run, BenchmarkJudgeStatus.Running);
        var now = Now();
        run.JudgeStatus = BenchmarkJudgeStatus.Succeeded;
        run.JudgeResultJson = command.JudgeResultJson.ToArray();
        UpdateLastStreamSequence(run, command.LastStreamSequence);
        run.JudgeCompletedAtUtc = now;
        run.Version++;
        run.UpdatedAtUtc = now;
        TerminalizeWork(work, BenchmarkWorkStatus.Succeeded, errorMessage: null, now);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run);
    }

    public async Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        return await MarkJudgeFailedAsync(runId, expectedRunVersion, errorMessage, lastStreamSequence: 0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        long lastStreamSequence,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireWorkCompletionAsync(runId, BenchmarkWorkKind.Judge, expectedRunVersion, cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (run.JudgeStatus == BenchmarkJudgeStatus.Failed)
        {
            return ToRecord(run);
        }

        var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Judge, cancellationToken).ConfigureAwait(false);
        EnsureVersion(work.Version, expectedRunVersion);
        EnsureJudgeState(run, BenchmarkJudgeStatus.Running);
        var now = Now();
        run.JudgeStatus = BenchmarkJudgeStatus.Failed;
        run.JudgeErrorMessage = Sanitize(errorMessage);
        UpdateLastStreamSequence(run, lastStreamSequence);
        run.JudgeCompletedAtUtc = now;
        run.Version++;
        run.UpdatedAtUtc = now;
        TerminalizeWork(work, BenchmarkWorkStatus.Failed, run.JudgeErrorMessage, now);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run);
    }

    public async Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId,
        long expectedRunVersion,
        CancellationToken cancellationToken = default)
    {
        return await MarkJudgeCancelledAsync(runId, expectedRunVersion, lastStreamSequence: 0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId,
        long expectedRunVersion,
        long lastStreamSequence,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireWorkCompletionAsync(runId, BenchmarkWorkKind.Judge, expectedRunVersion, cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (run.JudgeStatus == BenchmarkJudgeStatus.Cancelled)
        {
            return ToRecord(run);
        }

        var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Judge, cancellationToken).ConfigureAwait(false);
        EnsureVersion(work.Version, expectedRunVersion);
        EnsureJudgeState(run, BenchmarkJudgeStatus.Running);
        var now = Now();
        run.JudgeStatus = BenchmarkJudgeStatus.Cancelled;
        UpdateLastStreamSequence(run, lastStreamSequence);
        run.JudgeCompletedAtUtc = now;
        run.Version++;
        run.UpdatedAtUtc = now;
        TerminalizeWork(work, BenchmarkWorkStatus.Cancelled, errorMessage: null, now);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run);
    }

    public async Task<BenchmarkRunRecord> CancelAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (run.PrimaryStatus is BenchmarkPrimaryStatus.CancelRequested or BenchmarkPrimaryStatus.Cancelled)
        {
            return ToRecord(run);
        }

        if (run.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
            && run.JudgeStatus == BenchmarkJudgeStatus.Cancelled)
        {
            return ToRecord(run);
        }

        EnsureVersion(run.Version, expectedRunVersion);
        var now = Now();
        if (run.PrimaryStatus == BenchmarkPrimaryStatus.Queued)
        {
            run.PrimaryStatus = BenchmarkPrimaryStatus.Cancelled;
            run.PrimaryCompletedAtUtc = now;
            var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Primary, cancellationToken).ConfigureAwait(false);
            TerminalizeWork(work, BenchmarkWorkStatus.Cancelled, errorMessage: null, now);
            SkipPendingJudge(run);
        }
        else if (run.PrimaryStatus == BenchmarkPrimaryStatus.Running)
        {
            run.PrimaryStatus = BenchmarkPrimaryStatus.CancelRequested;
        }
        else if (run.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded && run.JudgeStatus is BenchmarkJudgeStatus.Queued or BenchmarkJudgeStatus.Running)
        {
            run.JudgeStatus = BenchmarkJudgeStatus.Cancelled;
            run.JudgeCompletedAtUtc = now;
            var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Judge, cancellationToken).ConfigureAwait(false);
            TerminalizeWork(work, BenchmarkWorkStatus.Cancelled, errorMessage: null, now);
        }
        else
        {
            throw new BenchmarkConflictException("InvalidCancellationTransition");
        }

        run.Version++;
        run.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run);
    }

    public async Task<BenchmarkRunRecord> SetUserScoreAsync(Guid runId,
        int score,
        long expectedRunVersion,
        CancellationToken cancellationToken = default)
    {
        if (score is < 1 or > 5)
        {
            throw new BenchmarkValidationException("Score must be between 1 and 5.");
        }

        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        EnsureVersion(run.Version, expectedRunVersion);
        if (run.PrimaryStatus != BenchmarkPrimaryStatus.Succeeded)
        {
            throw new BenchmarkConflictException("PrimaryNotSucceeded");
        }

        run.UserScore = score;
        run.Version++;
        run.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run);
    }

    public async Task<int> RecoverOnStartupAsync(CancellationToken cancellationToken = default) =>
        (await RecoverRunsOnStartupAsync(cancellationToken).ConfigureAwait(false)).Count;

    public async Task<IReadOnlyList<BenchmarkRunRecord>> RecoverRunsOnStartupAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var activeWork = await _dbContext.BenchmarkWorkItems.Where(entity => entity.Status == BenchmarkWorkStatus.Running).ToListAsync(cancellationToken).ConfigureAwait(false);
        var cancelRequested = await _dbContext.BenchmarkRuns.Where(entity => entity.PrimaryStatus == BenchmarkPrimaryStatus.CancelRequested).ToListAsync(cancellationToken).ConfigureAwait(false);
        var recoveredRunIds = new HashSet<Guid>();
        var now = Now();
        foreach (var work in activeWork)
        {
            var run = await _dbContext.BenchmarkRuns.SingleAsync(entity => entity.Id == work.RunId, cancellationToken).ConfigureAwait(false);
            var cancelledPrimary = work.Kind == BenchmarkWorkKind.Primary
                                   && run.PrimaryStatus == BenchmarkPrimaryStatus.CancelRequested;
            work.Status = cancelledPrimary ? BenchmarkWorkStatus.Cancelled : BenchmarkWorkStatus.Failed;
            work.ErrorMessage = cancelledPrimary ? null : InterruptedMessage;
            work.FinishedAtUtc = now;
            work.Version++;
            if (work.Kind == BenchmarkWorkKind.Primary)
            {
                run.PrimaryStatus = cancelledPrimary ? BenchmarkPrimaryStatus.Cancelled : BenchmarkPrimaryStatus.Failed;
                run.PrimaryErrorMessage = cancelledPrimary ? null : InterruptedMessage;
                run.PrimaryCompletedAtUtc = now;
                SkipPendingJudge(run);
            }
            else
            {
                run.JudgeStatus = BenchmarkJudgeStatus.Failed;
                run.JudgeErrorMessage = InterruptedMessage;
                run.JudgeCompletedAtUtc = now;
            }

            run.Version++;
            run.LastStreamSequence = checked(run.LastStreamSequence + 1);
            run.UpdatedAtUtc = now;
            recoveredRunIds.Add(run.Id);
        }

        foreach (var run in cancelRequested.Where(run => activeWork.All(work => work.RunId != run.Id)))
        {
            run.PrimaryStatus = BenchmarkPrimaryStatus.Cancelled;
            run.PrimaryCompletedAtUtc = now;
            run.Version++;
            run.LastStreamSequence = checked(run.LastStreamSequence + 1);
            run.UpdatedAtUtc = now;
            SkipPendingJudge(run);
            recoveredRunIds.Add(run.Id);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recoveredRunIds
                         .Select(id => ToRecord(_dbContext.BenchmarkRuns.Local.Single(run => run.Id == id)))
                         .ToArray();
    }

    public async Task DeleteRunAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        EnsureVersion(run.Version, expectedRunVersion);
        if (!IsPrimaryTerminal(run.PrimaryStatus)
            || run.JudgeStatus is BenchmarkJudgeStatus.Pending or BenchmarkJudgeStatus.Queued or BenchmarkJudgeStatus.Running
            || await _dbContext.BenchmarkWorkItems.AnyAsync(entity => entity.RunId == runId
                                                                      && (entity.Status == BenchmarkWorkStatus.Queued || entity.Status == BenchmarkWorkStatus.Running), cancellationToken)
                               .ConfigureAwait(false))
        {
            throw new BenchmarkConflictException("ActiveRun");
        }

        await _dbContext.BenchmarkWorkItems.Where(entity => entity.RunId == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        // Both deletes intentionally bypass the tracker: this scope may have materialized the required work/run
        // relationship earlier, and mixing ExecuteDelete for the child with tracked Remove for the parent makes EF
        // interpret the already-deleted child as a severed required association.
        _dbContext.ChangeTracker.Clear();
        _ = await _dbContext.BenchmarkRuns.Where(entity => entity.Id == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<BenchmarkRunRecord> TerminalizePrimaryNonSuccessAsync(Guid runId,
        long expectedRunVersion,
        BenchmarkPrimaryStatus status,
        BenchmarkWorkStatus workStatus,
        string errorMessage,
        long lastStreamSequence,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireWorkCompletionAsync(runId, BenchmarkWorkKind.Primary, expectedRunVersion, cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (run.PrimaryStatus == status)
        {
            return ToRecord(run);
        }

        var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Primary, cancellationToken).ConfigureAwait(false);
        EnsureVersion(work.Version, expectedRunVersion);
        if (run.PrimaryStatus is not (BenchmarkPrimaryStatus.Running or BenchmarkPrimaryStatus.CancelRequested))
        {
            throw new BenchmarkConflictException("InvalidPrimaryTransition");
        }

        var now = Now();
        var reconciledStatus = run.PrimaryStatus == BenchmarkPrimaryStatus.CancelRequested
            ? BenchmarkPrimaryStatus.Cancelled
            : status;
        var reconciledWorkStatus = reconciledStatus == BenchmarkPrimaryStatus.Cancelled
            ? BenchmarkWorkStatus.Cancelled
            : workStatus;
        run.PrimaryStatus = reconciledStatus;
        run.PrimaryErrorMessage = reconciledStatus == BenchmarkPrimaryStatus.Cancelled ? null : Sanitize(errorMessage);
        UpdateLastStreamSequence(run, lastStreamSequence);
        run.PrimaryCompletedAtUtc = now;
        run.Version++;
        run.UpdatedAtUtc = now;
        SkipPendingJudge(run);
        TerminalizeWork(work, reconciledWorkStatus, run.PrimaryErrorMessage, now);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run);
    }

    private static void TerminalizeWork(BenchmarkWorkItem work,
        BenchmarkWorkStatus status,
        string? errorMessage,
        long now)
    {
        if (work.Status is BenchmarkWorkStatus.Succeeded or BenchmarkWorkStatus.Failed or BenchmarkWorkStatus.Cancelled)
        {
            return;
        }

        work.Status = status;
        work.ErrorMessage = errorMessage;
        work.FinishedAtUtc = now;
        work.Version++;
    }

    private async Task<BenchmarkWorkItem> RequireWorkAsync(Guid runId, BenchmarkWorkKind kind, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkWorkItems.SingleOrDefaultAsync(entity => entity.RunId == runId && entity.Kind == kind, cancellationToken).ConfigureAwait(false)
        ?? throw new BenchmarkNotFoundException("Benchmark work item was not found.");

    private async Task AcquireWorkCompletionAsync(Guid runId,
        BenchmarkWorkKind kind,
        long expectedWorkVersion,
        CancellationToken cancellationToken)
    {
        // Reserve SQLite's single writer before reading the aggregate. Score and cancellation updates then serialize
        // around phase completion without participating in the executor's work-item compare-and-swap token.
        var acquired = await _dbContext.BenchmarkWorkItems
                                       .Where(entity => entity.RunId == runId
                                                        && entity.Kind == kind
                                                        && entity.Status == BenchmarkWorkStatus.Running
                                                        && entity.Version == expectedWorkVersion)
                                       .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.Version, entity => entity.Version), cancellationToken)
                                       .ConfigureAwait(false);
        if (acquired == 0)
        {
            throw new BenchmarkConflictException("VersionConflict");
        }
    }

    private async Task<BenchmarkProject> RequireProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkProjects.SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken).ConfigureAwait(false)
        ?? throw new BenchmarkNotFoundException("Benchmark project was not found.");

    private async Task<BenchmarkRun> RequireRunAsync(Guid runId, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? _dbContext.BenchmarkRuns.AsQueryable() : _dbContext.BenchmarkRuns.AsNoTracking();
        return await query.SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken).ConfigureAwait(false)
               ?? throw new BenchmarkNotFoundException("Benchmark run was not found.");
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new BenchmarkConflictException("VersionConflict") { Source = exception.Source };
        }
        catch (DbUpdateException exception) when (exception.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new BenchmarkConflictException("DuplicateWork") { Source = exception.Source };
        }
    }

    private static void ValidateProject(BenchmarkProjectInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name) || input.CoreTaskJson.Length == 0 || input.ContextTokens <= 0 || input.AgentDefinitionId == Guid.Empty)
        {
            throw new BenchmarkValidationException("Benchmark project input is invalid.");
        }

        if (input.JudgeEnabled && (string.IsNullOrWhiteSpace(input.JudgeModelName) || input.JudgeContextTokens is null or <= 0))
        {
            throw new BenchmarkValidationException("Enabled judging requires a model and positive context.");
        }
    }

    private static void ValidateStart(BenchmarkStartRunCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ProjectId == Guid.Empty || command.RuntimeSnapshotJson.Length == 0 || string.IsNullOrWhiteSpace(command.PrimaryModelName)
            || string.IsNullOrWhiteSpace(command.ModelContentFingerprint) || !command.ModelContentFingerprint.StartsWith("v1:", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(command.AgentName) || command.RequestedContextTokens <= 0)
        {
            throw new BenchmarkValidationException("Benchmark run input is invalid.");
        }
    }

    private static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected)
        {
            throw new BenchmarkConflictException("VersionConflict");
        }
    }

    private static void EnsurePrimaryState(BenchmarkRun run, BenchmarkPrimaryStatus required)
    {
        if (run.PrimaryStatus != required)
        {
            throw new BenchmarkConflictException("InvalidPrimaryTransition");
        }
    }

    private static void EnsureJudgeState(BenchmarkRun run, BenchmarkJudgeStatus required)
    {
        if (run.JudgeStatus != required)
        {
            throw new BenchmarkConflictException("InvalidJudgeTransition");
        }
    }

    private static void SkipPendingJudge(BenchmarkRun run)
    {
        if (run.JudgeStatus == BenchmarkJudgeStatus.Pending)
        {
            run.JudgeStatus = BenchmarkJudgeStatus.Skipped;
        }
    }

    private static bool IsPrimaryTerminal(BenchmarkPrimaryStatus status) =>
        status is BenchmarkPrimaryStatus.Succeeded or BenchmarkPrimaryStatus.Failed or BenchmarkPrimaryStatus.Cancelled;

    private static string Sanitize(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 1024 ? normalized : normalized[..1024];
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void UpdateLastStreamSequence(BenchmarkRun run, long sequence)
    {
        if (sequence > run.LastStreamSequence)
        {
            run.LastStreamSequence = sequence;
        }
    }

    private long Now() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static BenchmarkProjectRecord ToRecord(BenchmarkProject entity, bool frozen) =>
        new(entity.Id, entity.Name, entity.CoreTaskJson.ToArray(), entity.ContextTokens, entity.AgentDefinitionId, entity.JudgeEnabled,
            entity.JudgeModelName, entity.JudgeContextTokens, entity.JudgePromptVersion, entity.JudgeOutputSchemaVersion, frozen,
            entity.Version, entity.CreatedAtUtc, entity.UpdatedAtUtc);

    private static BenchmarkRunRecord ToRecord(BenchmarkRun entity) =>
        new(entity.Id, entity.ProjectId, entity.RuntimeSnapshotJson.ToArray(), entity.PrimaryModelName, entity.PrimaryModelOrigin,
            entity.ModelContentFingerprint, entity.AgentName, entity.AgentVersion, entity.RequestedContextTokens, entity.PrimaryStatus,
            entity.EffectiveContextTokens, entity.DurationMs, entity.TotalTokens, entity.TokensPerSecond, CopyOptional(entity.OutputPartsJson),
            entity.LastStreamSequence, entity.UserScore, entity.JudgeStatus, CopyOptional(entity.JudgeResultJson), entity.PrimaryErrorMessage,
            entity.JudgeErrorMessage, entity.Version, entity.CreatedAtUtc, entity.StartedAtUtc, entity.PrimaryCompletedAtUtc,
            entity.JudgeStartedAtUtc, entity.JudgeCompletedAtUtc, entity.UpdatedAtUtc);

    private static ReadOnlyMemory<byte>? CopyOptional(byte[]? value)
    {
        if (value is null)
        {
            return default;
        }

        return new ReadOnlyMemory<byte>(value.ToArray());
    }
}
