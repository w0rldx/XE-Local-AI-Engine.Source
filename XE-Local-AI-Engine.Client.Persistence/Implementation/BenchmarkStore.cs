namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class BenchmarkStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IBenchmarkStore
{
    private const string InterruptedMessage = "Interrupted by application restart.";
    private const string UnresolvedJudgeRuntimeMessage = "judge runtime unresolved";
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

        // Same explicit order as run deletion, for the same reason: the project stops pointing at its revision
        // before the revisions go, and nothing relies on a cascade that this database does not enforce.
        project.CurrentJudgePolicyRevisionId = null;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await _dbContext.BenchmarkJudgePolicyRevisions.Where(entity => entity.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        _ = await _dbContext.BenchmarkProjects.Where(entity => entity.Id == projectId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
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
            PrimaryVariant = command.PrimaryLaunchIntent?.Variant,
            PrimaryKvCacheType = command.PrimaryLaunchIntent?.KvCacheType,
            PrimaryKvCacheTypeSource = command.PrimaryLaunchIntent?.KvCacheTypeSource,
            PrimaryKvAutoReason = command.PrimaryLaunchIntent?.KvAutoReason,
            PrimaryFlashAttentionMode = command.PrimaryLaunchIntent?.FlashAttentionMode,
            PrimaryIntendedLaunchIdentity = command.PrimaryLaunchIntent?.IntendedLaunchIdentity,
            PrimaryIntendedExecutableSha256 = command.PrimaryLaunchIntent?.IntendedExecutableSha256,
            JudgeVariant = command.JudgeLaunchIntent?.Variant,
            JudgeKvCacheType = command.JudgeLaunchIntent?.KvCacheType,
            JudgeKvCacheTypeSource = command.JudgeLaunchIntent?.KvCacheTypeSource,
            JudgeKvAutoReason = command.JudgeLaunchIntent?.KvAutoReason,
            JudgeFlashAttentionMode = command.JudgeLaunchIntent?.FlashAttentionMode,
            JudgeIntendedLaunchIdentity = command.JudgeLaunchIntent?.IntendedLaunchIdentity,
            JudgeIntendedExecutableSha256 = command.JudgeLaunchIntent?.IntendedExecutableSha256,
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

    public async Task<BenchmarkRunPage> ListRunsAsync(Guid projectId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var runs = _dbContext.BenchmarkRuns.AsNoTracking().Where(entity => entity.ProjectId == projectId);
        var totalCount = await runs.CountAsync(cancellationToken).ConfigureAwait(false);

        // Column projection, not entity materialization: the four encrypted payload columns are never read, so the
        // materialization interceptor has nothing to decrypt and a 200-row page costs no crypto at all. Everything a
        // summary shows is a flat column. The nested records are rebuilt from their own flat columns — presence is
        // decided by a non-payload member of each block, since both blocks are always written whole.
        // A local, not `default` inline: EF rejects a ReadOnlyMemory<byte> constant in a client projection.
        var noPayload = default(ReadOnlyMemory<byte>);
        var items = await runs.OrderByDescending(entity => entity.CreatedAtUtc)
                              .ThenByDescending(entity => entity.Id)
                              .Skip(skip)
                              .Take(take)
                              .Select(entity => new BenchmarkRunRecord(entity.Id,
                                  entity.ProjectId,
                                  noPayload,
                                  entity.PrimaryModelName,
                                  entity.PrimaryModelOrigin,
                                  entity.ModelContentFingerprint,
                                  entity.AgentName,
                                  entity.AgentVersion,
                                  entity.RequestedContextTokens,
                                  entity.PrimaryStatus,
                                  entity.EffectiveContextTokens,
                                  entity.DurationMs,
                                  entity.TotalTokens,
                                  entity.TokensPerSecond,
                                  null,
                                  entity.LastStreamSequence,
                                  entity.UserScore,
                                  entity.JudgeStatus,
                                  null,
                                  entity.PrimaryErrorMessage,
                                  entity.JudgeErrorMessage,
                                  entity.Version,
                                  entity.CreatedAtUtc,
                                  entity.StartedAtUtc,
                                  entity.PrimaryCompletedAtUtc,
                                  entity.JudgeStartedAtUtc,
                                  entity.JudgeCompletedAtUtc,
                                  entity.UpdatedAtUtc,
                                  entity.PrimaryVariant == null
                                      ? null
                                      : new BenchmarkRunLaunchIntent(entity.PrimaryVariant,
                                          entity.PrimaryKvCacheType!,
                                          entity.PrimaryKvCacheTypeSource!,
                                          entity.PrimaryKvAutoReason,
                                          entity.PrimaryFlashAttentionMode!,
                                          entity.PrimaryIntendedLaunchIdentity!,
                                          entity.PrimaryIntendedExecutableSha256),
                                  entity.JudgeVariant == null
                                      ? null
                                      : new BenchmarkRunLaunchIntent(entity.JudgeVariant,
                                          entity.JudgeKvCacheType!,
                                          entity.JudgeKvCacheTypeSource!,
                                          entity.JudgeKvAutoReason,
                                          entity.JudgeFlashAttentionMode!,
                                          entity.JudgeIntendedLaunchIdentity!,
                                          entity.JudgeIntendedExecutableSha256),
                                  entity.PrimaryEnvironmentFactsHash == null
                                      ? null
                                      : new BenchmarkRunLaunchEvidence(null,
                                          null,
                                          entity.PrimaryReceiptHash,
                                          entity.PrimaryEnvironmentFactsHash,
                                          entity.PrimaryEffectiveLaunchIdentity,
                                          entity.PrimaryEffectiveBackend,
                                          entity.PrimaryPlacementOffloaded,
                                          entity.PrimaryPlacementTotal,
                                          entity.PrimaryLaunchExecutableSha256,
                                          entity.PrimaryLaunchHasAuxAssets,
                                          entity.PrimaryLaunchKvCacheTypeSource),
                                  entity.JudgeEnvironmentFactsHash == null
                                      ? null
                                      : new BenchmarkRunLaunchEvidence(null,
                                          null,
                                          entity.JudgeReceiptHash,
                                          entity.JudgeEnvironmentFactsHash,
                                          entity.JudgeEffectiveLaunchIdentity,
                                          entity.JudgeEffectiveBackend,
                                          entity.JudgePlacementOffloaded,
                                          entity.JudgePlacementTotal,
                                          entity.JudgeLaunchExecutableSha256,
                                          entity.JudgeLaunchHasAuxAssets,
                                          entity.JudgeLaunchKvCacheTypeSource)))
                              .ToArrayAsync(cancellationToken)
                              .ConfigureAwait(false);
        return new BenchmarkRunPage(items, totalCount);
    }

    public Task<int> CountRunsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _dbContext.BenchmarkRuns.AsNoTracking().CountAsync(entity => entity.ProjectId == projectId, cancellationToken);

    public async Task<BenchmarkClaimedWork?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var candidate = await _dbContext.BenchmarkWorkItems.AsNoTracking()
                                            .Where(entity => entity.Status == BenchmarkWorkStatus.Queued)
                                            .OrderBy(entity => entity.QueueSequence)
                                            .Select(entity => new
                                            {
                                                entity.QueueSequence,
                                                entity.Version
                                            })
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
                if (work.JudgeAttemptId is { } claimedAttemptId)
                {
                    var attempt = await RequireJudgeAttemptAsync(claimedAttemptId, cancellationToken).ConfigureAwait(false);
                    if (attempt.Status != BenchmarkJudgeAttemptStatus.Queued)
                    {
                        throw new BenchmarkConflictException("InvalidJudgeTransition");
                    }

                    attempt.Status = BenchmarkJudgeAttemptStatus.Running;
                    attempt.StartedAtUtc = now;
                    attempt.Version++;
                }
            }

            run.Version++;
            run.UpdatedAtUtc = now;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BenchmarkClaimedWork(work.QueueSequence, work.RunId, work.Kind, work.Attempt, work.Version, ToRecord(run), work.JudgeAttemptId);
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
            // Only the pointer column: deciding whether to judge must not decrypt the project's core task.
            var currentRevisionId = await _dbContext.BenchmarkProjects.AsNoTracking()
                                                    .Where(entity => entity.Id == run.ProjectId)
                                                    .Select(entity => entity.CurrentJudgePolicyRevisionId)
                                                    .SingleOrDefaultAsync(cancellationToken)
                                                    .ConfigureAwait(false);
            if (currentRevisionId is { } revisionId)
            {
                var seed = command.JudgeAttempt;

                // The runtime was resolved for one specific revision. If the project moved on meanwhile, abandoning the
                // transaction rolls primary success back with it, and the caller re-resolves and retries.
                if (seed?.ExpectedJudgePolicyRevisionId is { } expectedRevisionId && expectedRevisionId != revisionId)
                {
                    throw new BenchmarkJudgePolicyChangedException("The project's judge policy changed while the run was executing.");
                }

                var revision = await RequireJudgePolicyRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false);
                _ = await InsertJudgeAttemptAsync(run,
                                                  revision,
                                                  seed?.RuntimeJson,
                                                  seed?.RuntimeUnresolvedReason,
                                                  seed?.LaunchIntent,
                                                  now,
                                                  cancellationToken)
                        .ConfigureAwait(false);
            }
            else
            {
                // No policy means nothing to judge under. A judge work item without an attempt is not representable,
                // so the phase terminalizes here rather than queueing work that can never be claimed.
                run.JudgeStatus = BenchmarkJudgeStatus.Skipped;
            }
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
        await TerminalizeJudgeAttemptAsync(work, BenchmarkJudgeAttemptStatus.Succeeded, errorMessage: null, now, cancellationToken).ConfigureAwait(false);
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
        await TerminalizeJudgeAttemptAsync(work, BenchmarkJudgeAttemptStatus.Failed, run.JudgeErrorMessage, now, cancellationToken).ConfigureAwait(false);
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
        await TerminalizeJudgeAttemptAsync(work, BenchmarkJudgeAttemptStatus.Cancelled, errorMessage: null, now, cancellationToken).ConfigureAwait(false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run);
    }

    public Task<bool> MarkPrimaryLaunchReadyAsync(Guid runId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        CancellationToken cancellationToken = default) =>
        MarkLaunchReadyAsync(runId, BenchmarkWorkKind.Primary, workItemId, claimedWorkVersion, command, cancellationToken);

    public Task<bool> MarkJudgeLaunchReadyAsync(Guid runId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        CancellationToken cancellationToken = default) =>
        MarkLaunchReadyAsync(runId, BenchmarkWorkKind.Judge, workItemId, claimedWorkVersion, command, cancellationToken);

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
            await TerminalizeJudgeAttemptAsync(work, BenchmarkJudgeAttemptStatus.Cancelled, errorMessage: null, now, cancellationToken).ConfigureAwait(false);
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
        int? score,
        long expectedRunVersion,
        CancellationToken cancellationToken = default)
    {
        if (score is < 0 or > 100)
        {
            throw new BenchmarkValidationException("Score must be between 0 and 100.");
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

        // Attempts first, so the work-item pass below finds them already terminal. A result is never overwritten:
        // only an attempt still marked Running — i.e. one whose judging died with the process — is touched.
        var interruptedAttempts = await _dbContext.BenchmarkJudgeAttempts
                                                  .Where(entity => entity.Status == BenchmarkJudgeAttemptStatus.Running)
                                                  .ToListAsync(cancellationToken)
                                                  .ConfigureAwait(false);
        foreach (var attempt in interruptedAttempts)
        {
            attempt.Status = BenchmarkJudgeAttemptStatus.Failed;
            attempt.ErrorMessage = InterruptedMessage;
            attempt.CompletedAtUtc = now;
            attempt.Version++;
        }

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
                await TerminalizeJudgeAttemptAsync(work, BenchmarkJudgeAttemptStatus.Failed, InterruptedMessage, now, cancellationToken).ConfigureAwait(false);
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
                               .ConfigureAwait(false)
            || await _dbContext.BenchmarkJudgeAttempts.AnyAsync(entity => entity.RunId == runId
                                                                          && (entity.Status == BenchmarkJudgeAttemptStatus.Queued
                                                                              || entity.Status == BenchmarkJudgeAttemptStatus.Running), cancellationToken)
                               .ConfigureAwait(false))
        {
            throw new BenchmarkConflictException("ActiveRun");
        }

        // Foreign keys are not enforced on this database, so the order below IS the referential integrity: the run
        // stops pointing at its attempt, then work items, then attempts, then the run itself.
        var projectId = run.ProjectId;
        run.CurrentJudgeAttemptId = null;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await _dbContext.BenchmarkWorkItems.Where(entity => entity.RunId == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _dbContext.BenchmarkJudgeAttempts.Where(entity => entity.RunId == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        // The deletes intentionally bypass the tracker: this scope may have materialized the required work/run
        // relationship earlier, and mixing ExecuteDelete for the child with tracked Remove for the parent makes EF
        // interpret the already-deleted child as a severed required association.
        _dbContext.ChangeTracker.Clear();
        _ = await _dbContext.BenchmarkRuns.Where(entity => entity.Id == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // The cohort was defined by runs that no longer exist. Leaving the reference key behind would silently keep
        // the next run's judging out of the ranking for a runtime nothing is measured against any more.
        if (!await _dbContext.BenchmarkRuns.AnyAsync(entity => entity.ProjectId == projectId, cancellationToken).ConfigureAwait(false))
        {
            await ResetCurrentCohortAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkJudgePolicyActivation> ActivateJudgePolicyAsync(Guid projectId,
        long expectedProjectVersion,
        ReadOnlyMemory<byte> policyJson,
        string policyHash,
        CancellationToken cancellationToken = default)
    {
        if (policyJson.IsEmpty)
        {
            throw new BenchmarkValidationException("A judge policy cannot be empty.");
        }

        EnsurePolicyHash(policyHash);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(project.Version, expectedProjectVersion);
        await EnsureNoActiveJudgeAttemptsAsync(projectId, cancellationToken).ConfigureAwait(false);

        var current = project.CurrentJudgePolicyRevisionId is { } currentRevisionId
            ? await RequireJudgePolicyRevisionAsync(currentRevisionId, cancellationToken).ConfigureAwait(false)
            : null;

        // Re-activating the policy the project already judges under changes nothing — in particular it must not reset
        // the cohort, or a no-op save would drop every ranked run out of the ranking.
        if (current is not null && string.Equals(current.PolicyHash, policyHash, StringComparison.Ordinal))
        {
            return new BenchmarkJudgePolicyActivation(ToRecord(current, includePayload: true), WasCreated: false, []);
        }

        var now = Now();
        var (revision, wasCreated) = await GetOrCreateJudgePolicyRevisionAsync(projectId, policyJson, policyHash, now, cancellationToken).ConfigureAwait(false);
        if (!wasCreated)
        {
            // A revision the project has held before starts a fresh cohort; a brand new one is already at generation 1.
            revision.ReferenceExecutionKey = null;
            revision.CohortGeneration = checked(revision.CohortGeneration + 1);
        }

        project.CurrentJudgePolicyRevisionId = revision.Id;
        project.Version++;
        project.UpdatedAtUtc = now;
        var succeededRunIds = await _dbContext.BenchmarkRuns.AsNoTracking()
                                              .Where(entity => entity.ProjectId == projectId
                                                               && entity.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
                                                               && entity.OutputPartsJson != null)
                                              .OrderBy(entity => entity.CreatedAtUtc)
                                              .ThenBy(entity => entity.Id)
                                              .Select(entity => entity.Id)
                                              .ToArrayAsync(cancellationToken)
                                              .ConfigureAwait(false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BenchmarkJudgePolicyActivation(ToRecord(revision, includePayload: true), wasCreated, succeededRunIds);
    }

    public async Task DisableJudgePolicyAsync(Guid projectId, long expectedProjectVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(project.Version, expectedProjectVersion);
        await EnsureNoActiveJudgeAttemptsAsync(projectId, cancellationToken).ConfigureAwait(false);
        project.CurrentJudgePolicyRevisionId = null;
        project.Version++;
        project.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkJudgePolicyRevisionRecord?> GetCurrentJudgePolicyRevisionAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        // Only the pointer column is read: resolving the current revision must not decrypt the project's core task.
        var revisionId = await _dbContext.BenchmarkProjects.AsNoTracking()
                                         .Where(entity => entity.Id == projectId)
                                         .Select(entity => entity.CurrentJudgePolicyRevisionId)
                                         .SingleOrDefaultAsync(cancellationToken)
                                         .ConfigureAwait(false);
        if (revisionId is null)
        {
            return null;
        }

        var revision = await _dbContext.BenchmarkJudgePolicyRevisions.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.Id == revisionId, cancellationToken)
                                       .ConfigureAwait(false);
        return revision is null ? null : ToRecord(revision, includePayload: true);
    }

    public async Task<IReadOnlyList<BenchmarkJudgePolicyRevisionRecord>> ListJudgePolicyRevisionsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await _dbContext.BenchmarkJudgePolicyRevisions.AsNoTracking()
                        .Where(entity => entity.ProjectId == projectId)
                        .OrderBy(entity => entity.Revision)
                        // Column projection, not entity materialization: a history list must not decrypt one policy
                        // blob per row to render revision numbers and hashes.
                        .Select(entity => new BenchmarkJudgePolicyRevisionRecord(entity.Id,
                            entity.ProjectId,
                            entity.Revision,
                            null,
                            entity.PolicyHash,
                            entity.ReferenceExecutionKey,
                            entity.CohortGeneration,
                            entity.CreatedAtUtc))
                        .ToArrayAsync(cancellationToken)
                        .ConfigureAwait(false);

    public async Task<BenchmarkJudgeAttemptRecord> EnqueueJudgeAttemptAsync(BenchmarkEnqueueJudgeAttemptCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await RequireRunAsync(command.RunId, tracking: true, cancellationToken).ConfigureAwait(false);
        EnsureVersion(run.Version, command.ExpectedRunVersion);
        if (run.PrimaryStatus != BenchmarkPrimaryStatus.Succeeded || run.OutputPartsJson is null)
        {
            throw new BenchmarkConflictException("PrimaryNotSucceeded");
        }

        var currentRevisionId = await _dbContext.BenchmarkProjects.AsNoTracking()
                                                .Where(entity => entity.Id == run.ProjectId)
                                                .Select(entity => entity.CurrentJudgePolicyRevisionId)
                                                .SingleOrDefaultAsync(cancellationToken)
                                                .ConfigureAwait(false);
        if (currentRevisionId != command.PolicyRevisionId)
        {
            throw new BenchmarkJudgePolicyChangedException("The requested judge policy revision is not the project's current one.");
        }

        var revision = await RequireJudgePolicyRevisionAsync(command.PolicyRevisionId, cancellationToken).ConfigureAwait(false);
        var currentAttempt = run.CurrentJudgeAttemptId is { } currentAttemptId
            ? await RequireJudgeAttemptAsync(currentAttemptId, cancellationToken).ConfigureAwait(false)
            : null;
        if (currentAttempt?.Status is BenchmarkJudgeAttemptStatus.Queued or BenchmarkJudgeAttemptStatus.Running)
        {
            throw new BenchmarkConflictException("JudgeAttemptActive");
        }

        // Already applied means: judged under this exact revision, in the live cohort generation, with the execution
        // the cohort is keyed on. Anything else — a stale generation, a different runtime — earns a fresh attempt.
        if (!command.Force
            && currentAttempt is { Status: BenchmarkJudgeAttemptStatus.Succeeded }
            && currentAttempt.PolicyRevisionId == revision.Id
            && currentAttempt.CohortGeneration == revision.CohortGeneration
            && currentAttempt.JudgeExecutionKey is not null
            && string.Equals(currentAttempt.JudgeExecutionKey, revision.ReferenceExecutionKey, StringComparison.Ordinal))
        {
            throw new BenchmarkConflictException("JudgePolicyAlreadyApplied");
        }

        var now = Now();
        var attempt = await InsertJudgeAttemptAsync(run,
                                                    revision,
                                                    command.RuntimeJson,
                                                    command.RuntimeUnresolvedReason,
                                                    command.LaunchIntent,
                                                    now,
                                                    cancellationToken)
                          .ConfigureAwait(false);
        run.Version++;
        run.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(attempt);
    }

    public async Task<bool> TryPromoteReferenceExecutionKeyAsync(Guid revisionId,
        int cohortGeneration,
        string executionKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executionKey))
        {
            throw new BenchmarkValidationException("A judge execution key cannot be empty.");
        }

        // Insert-if-null compare-and-swap in one statement: whichever same-generation success gets there first defines
        // the cohort, and a reset (generation bump) makes every in-flight promotion attempt miss.
        var promoted = await _dbContext.BenchmarkJudgePolicyRevisions
                                       .Where(entity => entity.Id == revisionId
                                                        && entity.CohortGeneration == cohortGeneration
                                                        && entity.ReferenceExecutionKey == null)
                                       .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.ReferenceExecutionKey, executionKey), cancellationToken)
                                       .ConfigureAwait(false);
        return promoted == 1;
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

    /// <summary>
    ///     Insert-if-null write of one phase's launch evidence. Keyed by the immutable work item, so a run version
    ///     that moved on (a score, a cancellation request) cannot invalidate the checkpoint, and no status is touched.
    /// </summary>
    private async Task<bool> MarkLaunchReadyAsync(Guid runId,
        BenchmarkWorkKind kind,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = await _dbContext.BenchmarkWorkItems.AsNoTracking()
                                   .SingleOrDefaultAsync(entity => entity.QueueSequence == workItemId, cancellationToken)
                                   .ConfigureAwait(false);

        // Running at the claimed version is the ordinary case; Cancelled at its successor is the proven cancel-first
        // ordering, where the work item was terminalized (Version + 1) while this launch was still coming up.
        if (work is null
            || work.RunId != runId
            || work.Kind != kind
            || !((work.Status == BenchmarkWorkStatus.Running && work.Version == claimedWorkVersion)
                 || (work.Status == BenchmarkWorkStatus.Cancelled && work.Version == claimedWorkVersion + 1)))
        {
            return false;
        }

        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        var receiptJson = command.ReceiptJson is null ? null : Encoding.UTF8.GetBytes(command.ReceiptJson);
        var environmentFactsJson = Encoding.UTF8.GetBytes(command.EnvironmentFactsJson);
        if (kind == BenchmarkWorkKind.Primary)
        {
            if (run.PrimaryLaunchReceiptJson is not null || run.PrimaryEnvironmentFactsJson is not null)
            {
                return false;
            }

            run.PrimaryLaunchReceiptJson = receiptJson;
            run.PrimaryEnvironmentFactsJson = environmentFactsJson;
            run.PrimaryReceiptHash = command.ReceiptHash;
            run.PrimaryEnvironmentFactsHash = command.EnvironmentFactsHash;
            run.PrimaryEffectiveLaunchIdentity = command.EffectiveLaunchIdentity;
            run.PrimaryEffectiveBackend = command.EffectiveBackend;
            run.PrimaryPlacementOffloaded = command.PlacementOffloaded;
            run.PrimaryPlacementTotal = command.PlacementTotal;
            run.PrimaryLaunchExecutableSha256 = command.ExecutableSha256;
            run.PrimaryLaunchHasAuxAssets = command.HasAuxAssets;
            run.PrimaryLaunchKvCacheTypeSource = command.KvCacheTypeSource;
        }
        else
        {
            if (run.JudgeLaunchReceiptJson is not null || run.JudgeEnvironmentFactsJson is not null)
            {
                return false;
            }

            run.JudgeLaunchReceiptJson = receiptJson;
            run.JudgeEnvironmentFactsJson = environmentFactsJson;
            run.JudgeReceiptHash = command.ReceiptHash;
            run.JudgeEnvironmentFactsHash = command.EnvironmentFactsHash;
            run.JudgeEffectiveLaunchIdentity = command.EffectiveLaunchIdentity;
            run.JudgeEffectiveBackend = command.EffectiveBackend;
            run.JudgePlacementOffloaded = command.PlacementOffloaded;
            run.JudgePlacementTotal = command.PlacementTotal;
            run.JudgeLaunchExecutableSha256 = command.ExecutableSha256;
            run.JudgeLaunchHasAuxAssets = command.HasAuxAssets;
            run.JudgeLaunchKvCacheTypeSource = command.KvCacheTypeSource;
        }

        // The run's own version is deliberately left alone: the checkpoint is evidence, not a lifecycle transition,
        // and bumping it would 409 an operator cancellation that is holding the version it just read.
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     Inserts one judging of <paramref name="run" /> plus its work item, and repoints the run at it. A missing
    ///     <paramref name="runtimeJson" /> means the judge runtime could not be resolved: the attempt goes in already
    ///     Failed together with a terminal work item, so "attempt implies work item" holds without ever queueing work
    ///     that no claimant could execute.
    /// </summary>
    private async Task<BenchmarkJudgeAttempt> InsertJudgeAttemptAsync(BenchmarkRun run,
        BenchmarkJudgePolicyRevision revision,
        ReadOnlyMemory<byte>? runtimeJson,
        string? runtimeUnresolvedReason,
        BenchmarkRunLaunchIntent? launchIntent,
        long now,
        CancellationToken cancellationToken)
    {
        var lastSequence = await _dbContext.BenchmarkJudgeAttempts
                                           .Where(entity => entity.RunId == run.Id)
                                           .Select(entity => (int?)entity.Sequence)
                                           .MaxAsync(cancellationToken)
                                           .ConfigureAwait(false)
                           ?? 0;
        var unresolved = runtimeJson is null;
        var failure = unresolved ? Sanitize(runtimeUnresolvedReason ?? UnresolvedJudgeRuntimeMessage) : null;
        var attempt = new BenchmarkJudgeAttempt
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Sequence = lastSequence + 1,
            PolicyRevisionId = revision.Id,
            CohortGeneration = revision.CohortGeneration,
            JudgeRuntimeJson = runtimeJson?.ToArray(),
            Status = unresolved ? BenchmarkJudgeAttemptStatus.Failed : BenchmarkJudgeAttemptStatus.Queued,
            ErrorMessage = failure,
            Variant = launchIntent?.Variant,
            KvCacheType = launchIntent?.KvCacheType,
            KvCacheTypeSource = launchIntent?.KvCacheTypeSource,
            KvAutoReason = launchIntent?.KvAutoReason,
            FlashAttentionMode = launchIntent?.FlashAttentionMode,
            IntendedLaunchIdentity = launchIntent?.IntendedLaunchIdentity,
            IntendedExecutableSha256 = launchIntent?.IntendedExecutableSha256,
            EnqueuedAtUtc = now,
            CompletedAtUtc = unresolved ? now : null,
            Version = 1
        };
        _dbContext.BenchmarkJudgeAttempts.Add(attempt);
        _dbContext.BenchmarkWorkItems.Add(new BenchmarkWorkItem
        {
            RunId = run.Id,
            Kind = BenchmarkWorkKind.Judge,
            JudgeAttemptId = attempt.Id,
            Status = unresolved ? BenchmarkWorkStatus.Failed : BenchmarkWorkStatus.Queued,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = now,
            FinishedAtUtc = unresolved ? now : null,
            ErrorMessage = failure
        });
        run.CurrentJudgeAttemptId = attempt.Id;
        run.JudgeStatus = unresolved ? BenchmarkJudgeStatus.Failed : BenchmarkJudgeStatus.Queued;
        run.JudgeErrorMessage = failure;
        run.JudgeStartedAtUtc = null;
        run.JudgeCompletedAtUtc = unresolved ? now : null;
        return attempt;
    }

    /// <summary>Moves the attempt behind a judge work item to its terminal state. Never overwrites a terminal one.</summary>
    private async Task TerminalizeJudgeAttemptAsync(BenchmarkWorkItem work,
        BenchmarkJudgeAttemptStatus status,
        string? errorMessage,
        long now,
        CancellationToken cancellationToken)
    {
        if (work.JudgeAttemptId is not { } attemptId)
        {
            return;
        }

        var attempt = await _dbContext.BenchmarkJudgeAttempts.SingleOrDefaultAsync(entity => entity.Id == attemptId, cancellationToken).ConfigureAwait(false);
        if (attempt is null
            || attempt.Status is BenchmarkJudgeAttemptStatus.Succeeded or BenchmarkJudgeAttemptStatus.Failed or BenchmarkJudgeAttemptStatus.Cancelled)
        {
            return;
        }

        attempt.Status = status;
        attempt.ErrorMessage = errorMessage;
        attempt.CompletedAtUtc = now;
        attempt.Version++;
    }

    /// <summary>
    ///     Get-or-create by <c>(project, hash)</c>: insert, and on the unique conflict re-query, so two racing
    ///     activations of the same policy converge on one row instead of minting a duplicate revision.
    /// </summary>
    private async Task<(BenchmarkJudgePolicyRevision Revision, bool WasCreated)> GetOrCreateJudgePolicyRevisionAsync(Guid projectId,
        ReadOnlyMemory<byte> policyJson,
        string policyHash,
        long now,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.BenchmarkJudgePolicyRevisions
                                       .SingleOrDefaultAsync(entity => entity.ProjectId == projectId && entity.PolicyHash == policyHash, cancellationToken)
                                       .ConfigureAwait(false);
        if (existing is not null)
        {
            return (existing, false);
        }

        var lastRevision = await _dbContext.BenchmarkJudgePolicyRevisions
                                           .Where(entity => entity.ProjectId == projectId)
                                           .Select(entity => (int?)entity.Revision)
                                           .MaxAsync(cancellationToken)
                                           .ConfigureAwait(false)
                           ?? 0;
        var revision = new BenchmarkJudgePolicyRevision
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Revision = lastRevision + 1,
            PolicyJson = policyJson.ToArray(),
            PolicyHash = policyHash,
            ReferenceExecutionKey = null,
            CohortGeneration = 1,
            CreatedAtUtc = now
        };
        _dbContext.BenchmarkJudgePolicyRevisions.Add(revision);
        try
        {
            // Staged save inside the caller's transaction: the revision must exist before the project points at it,
            // and a failure anywhere after this still rolls both rows back together.
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BenchmarkConflictException conflict) when (string.Equals(conflict.Code, "DuplicateWork", StringComparison.Ordinal))
        {
            _dbContext.Entry(revision).State = EntityState.Detached;
            var raced = await _dbContext.BenchmarkJudgePolicyRevisions
                                        .SingleOrDefaultAsync(entity => entity.ProjectId == projectId && entity.PolicyHash == policyHash, cancellationToken)
                                        .ConfigureAwait(false);
            if (raced is null)
            {
                throw;
            }

            return (raced, false);
        }

        return (revision, true);
    }

    private async Task ResetCurrentCohortAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _dbContext.BenchmarkProjects.SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken).ConfigureAwait(false);
        if (project?.CurrentJudgePolicyRevisionId is not { } revisionId)
        {
            return;
        }

        var revision = await _dbContext.BenchmarkJudgePolicyRevisions.SingleOrDefaultAsync(entity => entity.Id == revisionId, cancellationToken).ConfigureAwait(false);
        if (revision is null)
        {
            return;
        }

        revision.ReferenceExecutionKey = null;
        revision.CohortGeneration = checked(revision.CohortGeneration + 1);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureNoActiveJudgeAttemptsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var active = await (from attempt in _dbContext.BenchmarkJudgeAttempts.AsNoTracking()
                            join run in _dbContext.BenchmarkRuns.AsNoTracking() on attempt.RunId equals run.Id
                            where run.ProjectId == projectId
                                  && (attempt.Status == BenchmarkJudgeAttemptStatus.Queued || attempt.Status == BenchmarkJudgeAttemptStatus.Running)
                            select attempt.Id).AnyAsync(cancellationToken).ConfigureAwait(false);
        if (active)
        {
            throw new BenchmarkConflictException("JudgeAttemptsActive");
        }
    }

    private async Task<BenchmarkJudgePolicyRevision> RequireJudgePolicyRevisionAsync(Guid revisionId, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkJudgePolicyRevisions.SingleOrDefaultAsync(entity => entity.Id == revisionId, cancellationToken).ConfigureAwait(false)
        ?? throw new BenchmarkNotFoundException("Benchmark judge policy revision was not found.");

    private async Task<BenchmarkJudgeAttempt> RequireJudgeAttemptAsync(Guid attemptId, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkJudgeAttempts.SingleOrDefaultAsync(entity => entity.Id == attemptId, cancellationToken).ConfigureAwait(false)
        ?? throw new BenchmarkNotFoundException("Benchmark judge attempt was not found.");

    private static void EnsurePolicyHash(string policyHash)
    {
        if (policyHash is not { Length: 64 } || !policyHash.All(static character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new BenchmarkValidationException("A judge policy hash must be 64 lowercase hexadecimal characters.");
        }
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

    /// <summary>
    ///     The run's work item of that kind. A run has exactly one primary item, but one judge item per attempt, so the
    ///     newest queue sequence is taken — that is the current attempt's, which is the only one still in play.
    /// </summary>
    private async Task<BenchmarkWorkItem> RequireWorkAsync(Guid runId, BenchmarkWorkKind kind, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkWorkItems.Where(entity => entity.RunId == runId && entity.Kind == kind)
                        .OrderByDescending(entity => entity.QueueSequence)
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false)
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
            throw new BenchmarkConflictException("VersionConflict")
            {
                Source = exception.Source
            };
        }
        catch (DbUpdateException exception) when (exception.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new BenchmarkConflictException("DuplicateWork")
            {
                Source = exception.Source
            };
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

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void UpdateLastStreamSequence(BenchmarkRun run, long sequence)
    {
        if (sequence > run.LastStreamSequence)
        {
            run.LastStreamSequence = sequence;
        }
    }

    private long Now() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

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
            entity.JudgeStartedAtUtc, entity.JudgeCompletedAtUtc, entity.UpdatedAtUtc,
            ToIntent(entity.PrimaryVariant, entity.PrimaryKvCacheType, entity.PrimaryKvCacheTypeSource, entity.PrimaryKvAutoReason,
                entity.PrimaryFlashAttentionMode, entity.PrimaryIntendedLaunchIdentity, entity.PrimaryIntendedExecutableSha256),
            ToIntent(entity.JudgeVariant, entity.JudgeKvCacheType, entity.JudgeKvCacheTypeSource, entity.JudgeKvAutoReason,
                entity.JudgeFlashAttentionMode, entity.JudgeIntendedLaunchIdentity, entity.JudgeIntendedExecutableSha256),
            ToEvidence(entity.PrimaryLaunchReceiptJson, entity.PrimaryEnvironmentFactsJson, entity.PrimaryReceiptHash,
                entity.PrimaryEnvironmentFactsHash, entity.PrimaryEffectiveLaunchIdentity, entity.PrimaryEffectiveBackend,
                entity.PrimaryPlacementOffloaded, entity.PrimaryPlacementTotal, entity.PrimaryLaunchExecutableSha256,
                entity.PrimaryLaunchHasAuxAssets, entity.PrimaryLaunchKvCacheTypeSource),
            ToEvidence(entity.JudgeLaunchReceiptJson, entity.JudgeEnvironmentFactsJson, entity.JudgeReceiptHash,
                entity.JudgeEnvironmentFactsHash, entity.JudgeEffectiveLaunchIdentity, entity.JudgeEffectiveBackend,
                entity.JudgePlacementOffloaded, entity.JudgePlacementTotal, entity.JudgeLaunchExecutableSha256,
                entity.JudgeLaunchHasAuxAssets, entity.JudgeLaunchKvCacheTypeSource));

    private static BenchmarkRunLaunchIntent? ToIntent(string? variant,
        string? kvCacheType,
        string? kvCacheTypeSource,
        string? kvAutoReason,
        string? flashAttentionMode,
        string? intendedLaunchIdentity,
        string? intendedExecutableSha256) =>
        variant is null || kvCacheType is null || kvCacheTypeSource is null || flashAttentionMode is null || intendedLaunchIdentity is null
            ? null
            : new BenchmarkRunLaunchIntent(variant, kvCacheType, kvCacheTypeSource, kvAutoReason, flashAttentionMode,
                intendedLaunchIdentity, intendedExecutableSha256);

    private static BenchmarkRunLaunchEvidence? ToEvidence(byte[]? receiptJson,
        byte[]? environmentFactsJson,
        string? receiptHash,
        string? environmentFactsHash,
        string? effectiveLaunchIdentity,
        string? effectiveBackend,
        int? placementOffloaded,
        int? placementTotal,
        string? executableSha256,
        bool? hasAuxAssets,
        string? kvCacheTypeSource) =>
        receiptJson is null && environmentFactsJson is null
            ? null
            : new BenchmarkRunLaunchEvidence(CopyOptional(receiptJson), CopyOptional(environmentFactsJson), receiptHash,
                environmentFactsHash, effectiveLaunchIdentity, effectiveBackend, placementOffloaded, placementTotal,
                executableSha256, hasAuxAssets, kvCacheTypeSource);

    private static BenchmarkJudgePolicyRevisionRecord ToRecord(BenchmarkJudgePolicyRevision entity, bool includePayload) =>
        new(entity.Id, entity.ProjectId, entity.Revision, includePayload ? CopyOptional(entity.PolicyJson) : null, entity.PolicyHash,
            entity.ReferenceExecutionKey, entity.CohortGeneration, entity.CreatedAtUtc);

    private static BenchmarkJudgeAttemptRecord ToRecord(BenchmarkJudgeAttempt entity) =>
        new(entity.Id, entity.RunId, entity.Sequence, entity.PolicyRevisionId, entity.CohortGeneration,
            CopyOptional(entity.JudgeRuntimeJson), entity.JudgeExecutionKey, entity.Status, CopyOptional(entity.ResultJson),
            entity.Score, entity.ErrorMessage, entity.EnqueuedAtUtc, entity.StartedAtUtc, entity.CompletedAtUtc, entity.Version,
            ToIntent(entity.Variant, entity.KvCacheType, entity.KvCacheTypeSource, entity.KvAutoReason, entity.FlashAttentionMode,
                entity.IntendedLaunchIdentity, entity.IntendedExecutableSha256),
            ToEvidence(entity.LaunchReceiptJson, entity.EnvironmentFactsJson, entity.ReceiptHash, entity.EnvironmentFactsHash,
                entity.EffectiveLaunchIdentity, entity.EffectiveBackend, entity.PlacementOffloaded, entity.PlacementTotal,
                entity.LaunchExecutableSha256, entity.LaunchHasAuxAssets, entity.LaunchKvCacheTypeSource));

    private static ReadOnlyMemory<byte>? CopyOptional(byte[]? value)
    {
        if (value is null)
        {
            return default;
        }

        return new ReadOnlyMemory<byte>(value.ToArray());
    }
}
