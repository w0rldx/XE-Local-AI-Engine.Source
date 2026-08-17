namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Diagnostics.CodeAnalysis;
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

    public async Task<BenchmarkProjectRecord> CreateProjectAsync(BenchmarkProjectInput input,
        BenchmarkJudgePolicyChangeInput? judgePolicy = null,
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
            AgentDefinitionId = input.AgentDefinitionId,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        if (judgePolicy is null)
        {
            _dbContext.BenchmarkProjects.Add(entity);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return ToRecord(entity, frozen: false);
        }

        // The project and its judge are one creation. Staged saves inside one transaction are what the circular
        // project↔revision pointers force: project with a null pointer, then the revision, then the pointer.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _dbContext.BenchmarkProjects.Add(entity);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await ApplyJudgePolicyChangeAsync(entity, judgePolicy, now, cancellationToken).ConfigureAwait(false);
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
        project.Name = input.Name.Trim();
        project.CoreTaskJson = input.CoreTaskJson.ToArray();
        project.ContextTokens = input.ContextTokens;
        project.MaxOutputTokens = input.MaxOutputTokens;
        project.AgentDefinitionId = input.AgentDefinitionId;
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
            PrimaryVariant = command.PrimaryLaunchIntent?.Variant,
            PrimaryKvCacheType = command.PrimaryLaunchIntent?.KvCacheType,
            PrimaryKvCacheTypeSource = command.PrimaryLaunchIntent?.KvCacheTypeSource,
            PrimaryKvAutoReason = command.PrimaryLaunchIntent?.KvAutoReason,
            PrimaryFlashAttentionMode = command.PrimaryLaunchIntent?.FlashAttentionMode,
            PrimaryIntendedLaunchIdentity = command.PrimaryLaunchIntent?.IntendedLaunchIdentity,
            PrimaryIntendedExecutableSha256 = command.PrimaryLaunchIntent?.IntendedExecutableSha256,
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
        return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (await _dbContext.BenchmarkRuns.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken).ConfigureAwait(false) is not { } entity)
        {
            return null;
        }

        var views = await LoadJudgeViewsAsync([runId], cancellationToken).ConfigureAwait(false);
        var judge = JudgeViewFor(views, runId, entity.UserScore);
        var (qualityScore, qualityScoreSource) = ComputeQuality(entity.UserScore, judge);
        return ToRecord(entity) with
        {
            Judge = judge,
            QualityScore = qualityScore,
            QualityScoreSource = qualityScoreSource
        };
    }

    public async Task<BenchmarkRunPage> ListRunsAsync(Guid projectId,
        int skip,
        int take,
        string? modelGroupKey = null,
        bool includeUnscored = true,
        CancellationToken cancellationToken = default)
    {
        // Rank is computed over the WHOLE project, never the page: a run's position is a property of the project, and
        // paging must not renumber it. Filters narrow which rows come back, not what they are ranked against.
        var ranking = await LoadRankingAsync(projectId, cancellationToken).ConfigureAwait(false);
        var runs = _dbContext.BenchmarkRuns.AsNoTracking().Where(entity => entity.ProjectId == projectId);
        if (modelGroupKey is { Length: > 0 })
        {
            runs = runs.Where(entity => entity.ModelContentFingerprint == modelGroupKey);
        }

        if (!includeUnscored)
        {
            var scoredIds = ranking.Runs.Where(static entry => entry.Value.QualityScore is not null).Select(static entry => entry.Key).ToArray();
            runs = runs.Where(entity => scoredIds.Contains(entity.Id));
        }

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
                                  entity.PrimaryErrorMessage,
                                  entity.Version,
                                  entity.CreatedAtUtc,
                                  entity.StartedAtUtc,
                                  entity.PrimaryCompletedAtUtc,
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
                                  entity.PrimaryStopReason))
                              .ToArrayAsync(cancellationToken)
                              .ConfigureAwait(false);

        // One extra query for the page rather than a join inside the no-payload projection: the judge view is derived
        // from three more tables, and folding it in would make that projection unreadable.
        return new BenchmarkRunPage([.. items.Select(item => WithRanking(item, ranking))], totalCount, ranking.Cohort);
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
                // The judging's whole lifecycle lives on its attempt; the run only bumps its version so a reader
                // polling the run still sees that something about it changed.
                var attempt = await RequireJudgeAttemptAsync(work.JudgeAttemptId ?? throw new BenchmarkConflictException("InvalidJudgeTransition"),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (attempt.Status != BenchmarkJudgeAttemptStatus.Queued)
                {
                    throw new BenchmarkConflictException("InvalidJudgeTransition");
                }

                attempt.Status = BenchmarkJudgeAttemptStatus.Running;
                attempt.StartedAtUtc = now;
                attempt.Version++;
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
            return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
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
            run.PrimaryStopReason = null;
            ApplyThroughput(run, throughput: null);
            run.PrimaryCompletedAtUtc = cancelledAt;
            run.Version++;
            run.UpdatedAtUtc = cancelledAt;
            TerminalizeWork(work, BenchmarkWorkStatus.Cancelled, errorMessage: null, cancelledAt);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
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
        run.PrimaryStopReason = command.PrimaryStopReason;
        ApplyThroughput(run, command.Throughput);
        run.PrimaryCompletedAtUtc = now;
        run.Version++;
        run.UpdatedAtUtc = now;
        TerminalizeWork(work, BenchmarkWorkStatus.Succeeded, errorMessage: null, now);
        // Only the pointer column: deciding whether to judge must not decrypt the project's core task. No pointer
        // means nothing to judge under, and the run simply never gets an attempt.
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

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
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
        ArgumentNullException.ThrowIfNull(command);
        if (command.JudgeResultJson.IsEmpty)
        {
            throw new BenchmarkValidationException("Successful judge output cannot be empty.");
        }

        return await TerminalizeJudgeAsync(command.RunId,
                command.ExpectedWorkVersion,
                BenchmarkJudgeAttemptStatus.Succeeded,
                BenchmarkWorkStatus.Succeeded,
                errorMessage: null,
                command.LastStreamSequence,
                (attempt, promote) =>
                {
                    attempt.ResultJson = command.JudgeResultJson.ToArray();
                    attempt.Score = command.Score;
                    return promote;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        CancellationToken cancellationToken = default) =>
        MarkJudgeFailedAsync(runId, expectedRunVersion, errorMessage, lastStreamSequence: 0, cancellationToken);

    public Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        TerminalizeJudgeAsync(runId,
            expectedRunVersion,
            BenchmarkJudgeAttemptStatus.Failed,
            BenchmarkWorkStatus.Failed,
            Sanitize(errorMessage),
            lastStreamSequence,
            static (_, _) => false,
            cancellationToken);

    public Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId,
        long expectedRunVersion,
        CancellationToken cancellationToken = default) =>
        MarkJudgeCancelledAsync(runId, expectedRunVersion, lastStreamSequence: 0, cancellationToken);

    public Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId,
        long expectedRunVersion,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        TerminalizeJudgeAsync(runId,
            expectedRunVersion,
            BenchmarkJudgeAttemptStatus.Cancelled,
            BenchmarkWorkStatus.Cancelled,
            errorMessage: null,
            lastStreamSequence,
            static (_, _) => false,
            cancellationToken);

    /// <summary>
    ///     The one judge terminalization path. The compare-and-swap is on the immutable work item, the state lives on
    ///     the attempt, and the run only moves its version and stream sequence — a judging is not run state.
    /// </summary>
    /// <param name="apply">
    ///     Writes the terminal payload onto the attempt and returns whether this outcome may claim the rank cohort.
    ///     Only a success may: a failed or cancelled judging must never define what the ranked runs are compared to.
    /// </param>
    private async Task<BenchmarkRunRecord> TerminalizeJudgeAsync(Guid runId,
        long expectedWorkVersion,
        BenchmarkJudgeAttemptStatus attemptStatus,
        BenchmarkWorkStatus workStatus,
        string? errorMessage,
        long lastStreamSequence,
        Func<BenchmarkJudgeAttempt, bool, bool> apply,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireWorkCompletionAsync(runId, BenchmarkWorkKind.Judge, expectedWorkVersion, cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Judge, cancellationToken).ConfigureAwait(false);
        EnsureVersion(work.Version, expectedWorkVersion);
        var now = Now();
        TerminalizeWork(work, workStatus, errorMessage, now);
        var attempt = await TerminalizeJudgeAttemptAsync(work, attemptStatus, errorMessage, now, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            // Already terminal: repeating a terminalization must not write a second result or bump anything.
            return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
        }

        if (apply(attempt, attemptStatus == BenchmarkJudgeAttemptStatus.Succeeded) && attempt.JudgeExecutionKey is { } executionKey)
        {
            // The cohort is claimed by the first SUCCESS of the live generation, never at readiness: a failed first
            // attempt must not poison the ranking for the runtime it happened to run on.
            _ = await TryPromoteReferenceExecutionKeyAsync(attempt.PolicyRevisionId, attempt.CohortGeneration, executionKey, cancellationToken)
                .ConfigureAwait(false);
        }

        UpdateLastStreamSequence(run, lastStreamSequence);
        run.Version++;
        run.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> MarkPrimaryLaunchReadyAsync(Guid runId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        CancellationToken cancellationToken = default)
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
            || work.Kind != BenchmarkWorkKind.Primary
            || !((work.Status == BenchmarkWorkStatus.Running && work.Version == claimedWorkVersion)
                 || (work.Status == BenchmarkWorkStatus.Cancelled && work.Version == claimedWorkVersion + 1)))
        {
            return false;
        }

        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (run.PrimaryLaunchReceiptJson is not null || run.PrimaryEnvironmentFactsJson is not null)
        {
            return false;
        }

        run.PrimaryLaunchReceiptJson = command.ReceiptJson is null ? null : Encoding.UTF8.GetBytes(command.ReceiptJson);
        run.PrimaryEnvironmentFactsJson = Encoding.UTF8.GetBytes(command.EnvironmentFactsJson);
        run.PrimaryReceiptHash = command.ReceiptHash;
        run.PrimaryEnvironmentFactsHash = command.EnvironmentFactsHash;
        run.PrimaryEffectiveLaunchIdentity = command.EffectiveLaunchIdentity;
        run.PrimaryEffectiveBackend = command.EffectiveBackend;
        run.PrimaryPlacementOffloaded = command.PlacementOffloaded;
        run.PrimaryPlacementTotal = command.PlacementTotal;
        run.PrimaryLaunchExecutableSha256 = command.ExecutableSha256;
        run.PrimaryLaunchHasAuxAssets = command.HasAuxAssets;
        run.PrimaryLaunchKvCacheTypeSource = command.KvCacheTypeSource;

        // The run's own version is deliberately left alone: the checkpoint is evidence, not a lifecycle transition,
        // and bumping it would 409 an operator cancellation that is holding the version it just read.
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> MarkJudgeLaunchReadyAsync(Guid attemptId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        string? judgeExecutionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = await _dbContext.BenchmarkWorkItems.AsNoTracking()
                                   .SingleOrDefaultAsync(entity => entity.QueueSequence == workItemId, cancellationToken)
                                   .ConfigureAwait(false);

        // Same CAS as the primary: Running at the claimed version, or Cancelled at its successor — the proven
        // cancel-first ordering, where the work item was terminalized while this launch was still coming up.
        if (work is null
            || work.Kind != BenchmarkWorkKind.Judge
            || work.JudgeAttemptId != attemptId
            || !((work.Status == BenchmarkWorkStatus.Running && work.Version == claimedWorkVersion)
                 || (work.Status == BenchmarkWorkStatus.Cancelled && work.Version == claimedWorkVersion + 1)))
        {
            return false;
        }

        var attempt = await RequireJudgeAttemptAsync(attemptId, cancellationToken).ConfigureAwait(false);
        if (attempt.LaunchReceiptJson is not null || attempt.EnvironmentFactsJson is not null)
        {
            return false;
        }

        attempt.LaunchReceiptJson = command.ReceiptJson is null ? null : Encoding.UTF8.GetBytes(command.ReceiptJson);
        attempt.EnvironmentFactsJson = Encoding.UTF8.GetBytes(command.EnvironmentFactsJson);
        attempt.ReceiptHash = command.ReceiptHash;
        attempt.EnvironmentFactsHash = command.EnvironmentFactsHash;
        attempt.EffectiveLaunchIdentity = command.EffectiveLaunchIdentity;
        attempt.EffectiveBackend = command.EffectiveBackend;
        attempt.PlacementOffloaded = command.PlacementOffloaded;
        attempt.PlacementTotal = command.PlacementTotal;
        attempt.LaunchExecutableSha256 = command.ExecutableSha256;
        attempt.LaunchHasAuxAssets = command.HasAuxAssets;
        attempt.LaunchKvCacheTypeSource = command.KvCacheTypeSource;

        // Written here and only here: the cohort key describes what this attempt actually launched. NULL stays NULL —
        // an incomplete execution identity must never be repaired into a rankable one later.
        attempt.JudgeExecutionKey = judgeExecutionKey;

        // The attempt's own version is deliberately left alone: the checkpoint is evidence, not a lifecycle
        // transition, and bumping it would invalidate the completion token the executor is holding.
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<BenchmarkRunRecord> CancelAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (run.PrimaryStatus is BenchmarkPrimaryStatus.CancelRequested or BenchmarkPrimaryStatus.Cancelled)
        {
            return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
        }

        var currentAttempt = run.CurrentJudgeAttemptId is { } currentAttemptId
            ? await RequireJudgeAttemptAsync(currentAttemptId, cancellationToken).ConfigureAwait(false)
            : null;
        if (run.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
            && currentAttempt?.Status == BenchmarkJudgeAttemptStatus.Cancelled)
        {
            return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
        }

        EnsureVersion(run.Version, expectedRunVersion);
        var now = Now();
        if (run.PrimaryStatus == BenchmarkPrimaryStatus.Queued)
        {
            run.PrimaryStatus = BenchmarkPrimaryStatus.Cancelled;
            run.PrimaryCompletedAtUtc = now;
            var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Primary, cancellationToken).ConfigureAwait(false);
            TerminalizeWork(work, BenchmarkWorkStatus.Cancelled, errorMessage: null, now);
        }
        else if (run.PrimaryStatus == BenchmarkPrimaryStatus.Running)
        {
            run.PrimaryStatus = BenchmarkPrimaryStatus.CancelRequested;
        }
        else if (run.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
                 && currentAttempt?.Status is BenchmarkJudgeAttemptStatus.Queued or BenchmarkJudgeAttemptStatus.Running)
        {
            var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Judge, cancellationToken).ConfigureAwait(false);
            TerminalizeWork(work, BenchmarkWorkStatus.Cancelled, errorMessage: null, now);
            _ = await TerminalizeJudgeAttemptAsync(work, BenchmarkJudgeAttemptStatus.Cancelled, errorMessage: null, now, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new BenchmarkConflictException("InvalidCancellationTransition");
        }

        run.Version++;
        run.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
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
        return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
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
            }
            else
            {
                _ = await TerminalizeJudgeAttemptAsync(work, BenchmarkJudgeAttemptStatus.Failed, InterruptedMessage, now, cancellationToken).ConfigureAwait(false);
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
        BenchmarkJudgeAttemptSeed? cohortAttemptSeed = null,
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
        var (revision, wasCreated) = await RepointJudgePolicyAsync(project, policyJson, policyHash, now, cancellationToken).ConfigureAwait(false);
        project.Version++;
        project.UpdatedAtUtc = now;
        var runIds = await EnqueueCohortAttemptsAsync(projectId, revision, cohortAttemptSeed, now, cancellationToken).ConfigureAwait(false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BenchmarkJudgePolicyActivation(ToRecord(revision, includePayload: true), wasCreated, runIds);
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

    public async Task<BenchmarkJudgeAttemptRecord?> GetJudgeAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default) =>
        await _dbContext.BenchmarkJudgeAttempts.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == attemptId, cancellationToken).ConfigureAwait(false) is { } attempt
            ? ToRecord(attempt)
            : null;

    public async Task<BenchmarkJudgePolicyRevisionRecord?> GetJudgePolicyRevisionAsync(Guid revisionId, CancellationToken cancellationToken = default) =>
        await _dbContext.BenchmarkJudgePolicyRevisions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == revisionId, cancellationToken).ConfigureAwait(false) is { } revision
            ? ToRecord(revision, includePayload: true)
            : null;

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

    public async Task<BenchmarkJudgePolicyActivation> BeginProjectRejudgeAsync(Guid projectId,
        long expectedProjectVersion,
        BenchmarkJudgeAttemptSeed? cohortAttemptSeed = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(project.Version, expectedProjectVersion);
        await EnsureNoActiveJudgeAttemptsAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project.CurrentJudgePolicyRevisionId is not { } revisionId)
        {
            throw new BenchmarkConflictException("JudgeDisabled");
        }

        // The seed's runtime was resolved for one revision. If the project has moved on since, rolling back is the
        // only honest answer: the caller re-resolves and retries rather than judging a cohort under a stale runtime.
        if (cohortAttemptSeed?.ExpectedJudgePolicyRevisionId is { } expectedRevisionId && expectedRevisionId != revisionId)
        {
            throw new BenchmarkJudgePolicyChangedException("The project's judge policy changed. Refresh and retry.");
        }

        // The reset and the attempts are committed together, so "move the cohort to the current runtime" can never
        // leave a partial set that would then rank against a key half of it never ran under.
        var now = Now();
        var revision = await RequireJudgePolicyRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false);
        revision.ReferenceExecutionKey = null;
        revision.CohortGeneration = checked(revision.CohortGeneration + 1);
        project.Version++;
        project.UpdatedAtUtc = now;
        var runIds = await EnqueueCohortAttemptsAsync(projectId, revision, cohortAttemptSeed, now, cancellationToken).ConfigureAwait(false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BenchmarkJudgePolicyActivation(ToRecord(revision, includePayload: true), WasCreated: false, runIds);
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
            return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
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
        TerminalizeWork(work, reconciledWorkStatus, run.PrimaryErrorMessage, now);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
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
        return attempt;
    }

    /// <summary>
    ///     Moves the attempt behind a judge work item to its terminal state and returns it. Never overwrites a terminal
    ///     one — a repeated terminalization returns <see langword="null" /> so no result is written twice.
    /// </summary>
    private async Task<BenchmarkJudgeAttempt?> TerminalizeJudgeAttemptAsync(BenchmarkWorkItem work,
        BenchmarkJudgeAttemptStatus status,
        string? errorMessage,
        long now,
        CancellationToken cancellationToken)
    {
        if (work.JudgeAttemptId is not { } attemptId)
        {
            return null;
        }

        var attempt = await _dbContext.BenchmarkJudgeAttempts.SingleOrDefaultAsync(entity => entity.Id == attemptId, cancellationToken).ConfigureAwait(false);
        if (attempt is null
            || attempt.Status is BenchmarkJudgeAttemptStatus.Succeeded or BenchmarkJudgeAttemptStatus.Failed or BenchmarkJudgeAttemptStatus.Cancelled)
        {
            return null;
        }

        attempt.Status = status;
        attempt.ErrorMessage = errorMessage;
        attempt.CompletedAtUtc = now;
        attempt.Version++;
        return attempt;
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

    /// <summary>
    ///     A run record carrying its derived judge view. Every path that returns a run uses this: the view is how a
    ///     caller reads judge state now, and a record that silently omitted it would read as "no judging" to a caller
    ///     that had just terminalized one.
    /// </summary>
    private async Task<BenchmarkRunRecord> ToRecordWithJudgeAsync(BenchmarkRun run, CancellationToken cancellationToken)
    {
        var views = await LoadJudgeViewsAsync([run.Id], cancellationToken).ConfigureAwait(false);
        var judge = JudgeViewFor(views, run.Id, run.UserScore);
        var (qualityScore, qualityScoreSource) = ComputeQuality(run.UserScore, judge);
        return ToRecord(run) with
        {
            Judge = judge,
            QualityScore = qualityScore,
            QualityScoreSource = qualityScoreSource
        };
    }

    /// <summary>
    ///     The project's ranking, computed once per request from flat columns only. Dense rank, descending, ties
    ///     sharing a rank. The plan's accepted ceiling: recompute per request rather than maintain a rollup — a
    ///     project is one hard-fixed task and its run count stays small.
    /// </summary>
    private async Task<BenchmarkProjectRanking> LoadRankingAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var scored = await _dbContext.BenchmarkRuns.AsNoTracking()
                                     .Where(entity => entity.ProjectId == projectId)
                                     .Select(entity => new
                                     {
                                         entity.Id,
                                         entity.UserScore,
                                         entity.PrimaryStopReason
                                     })
                                     .ToArrayAsync(cancellationToken)
                                     .ConfigureAwait(false);
        var views = await LoadJudgeViewsAsync([.. scored.Select(static run => run.Id)], cancellationToken).ConfigureAwait(false);

        var runs = new Dictionary<Guid, BenchmarkRunRanking>(scored.Length);
        var totalScored = 0;
        foreach (var run in scored)
        {
            // Truncation is decided BEFORE the judge-derived reasons and AFTER the operator override: a run whose
            // answer was cut off at the token budget is a real measurement of an INCOMPLETE answer, so its judge score
            // stays visible but never ranks — while an operator who scored it anyway still wins, exactly as everywhere
            // else. Read off the persisted stop reason, never inferred from the status.
            var truncated = IsTruncated(run.PrimaryStopReason) && run.UserScore is null;
            var judge = JudgeViewFor(views, run.Id, run.UserScore);
            if (truncated)
            {
                judge = judge with
                {
                    RankExclusionReason = BenchmarkRunJudgeStates.ReasonTruncated
                };
            }

            var (qualityScore, source) = truncated
                ? ((int?)null, BenchmarkQualityScoreSources.None)
                : ComputeQuality(run.UserScore, judge);
            if (run.UserScore is not null || judge.Score is not null)
            {
                totalScored++;
            }

            runs[run.Id] = new BenchmarkRunRanking(judge, qualityScore, source, Rank: null);
        }

        // Dense rank: equal scores share a position and the next distinct score is the next integer, so "rank 2" is
        // always "the second-best score in this project", however many runs tie above it.
        var ordered = runs.Values.Where(static entry => entry.QualityScore is not null)
                          .Select(static entry => entry.QualityScore!.Value)
                          .Distinct()
                          .OrderByDescending(static score => score)
                          .ToArray();
        foreach (var (runId, entry) in runs.ToArray())
        {
            if (entry.QualityScore is { } score)
            {
                runs[runId] = entry with
                {
                    Rank = Array.IndexOf(ordered, score) + 1
                };
            }
        }

        var current = await GetCurrentJudgePolicyRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);
        return new BenchmarkProjectRanking(runs,
            new BenchmarkRankCohort(current?.Revision,
                current?.ReferenceExecutionKey,
                current?.CohortGeneration,
                runs.Values.Count(static entry => entry.Rank is not null),
                totalScored));
    }

    /// <summary>
    ///     Whether the primary generation stopped because it ran out of budget. <c>length</c> is the OpenAI-compatible
    ///     token for BOTH causes llama-server reports it for — <c>n_predict</c> exhausted and the context window full
    ///     (<c>stopped_limit</c>) — and both mean the same thing here: the answer is cut off.
    /// </summary>
    private static bool IsTruncated(string? primaryStopReason) =>
        string.Equals(primaryStopReason, BenchmarkPrimaryStopReasons.Length, StringComparison.OrdinalIgnoreCase);

    private static BenchmarkRunRecord WithRanking(BenchmarkRunRecord run, BenchmarkProjectRanking ranking) =>
        ranking.Runs.TryGetValue(run.Id, out var entry)
            ? run with
            {
                Judge = entry.Judge,
                QualityScore = entry.QualityScore,
                QualityScoreSource = entry.Source,
                Rank = entry.Rank
            }
            : run;

    /// <summary>
    ///     The run's ranking value: the operator's override when set, otherwise the judge score — but only while that
    ///     judging is in the project's current cohort. A score from an outdated policy or a different judge runtime is
    ///     still shown, it just does not rank.
    /// </summary>
    private static (int? QualityScore, string Source) ComputeQuality(int? userScore, BenchmarkRunJudgeView judge)
    {
        if (userScore is { } operatorScore)
        {
            return (operatorScore, BenchmarkQualityScoreSources.User);
        }

        var judgeScore = judge is { State: BenchmarkRunJudgeStates.Succeeded, PolicyCurrent: true, ExecutionCurrent: true }
            ? judge.Score
            : null;
        return judgeScore is { } score
            ? (score, BenchmarkQualityScoreSources.Judge)
            : (null, BenchmarkQualityScoreSources.None);
    }

    private sealed record BenchmarkRunRanking(BenchmarkRunJudgeView Judge, int? QualityScore, string Source, int? Rank);

    private sealed record BenchmarkProjectRanking(IReadOnlyDictionary<Guid, BenchmarkRunRanking> Runs, BenchmarkRankCohort Cohort);

    private static BenchmarkRunJudgeView JudgeViewFor(IReadOnlyDictionary<Guid, BenchmarkRunJudgeView> views, Guid runId, int? userScore)
    {
        if (views.TryGetValue(runId, out var view))
        {
            return view;
        }

        // No attempt: there is nothing to derive a judging from, so the run is unranked unless the operator scored it.
        var reason = userScore is null ? BenchmarkRunJudgeStates.ReasonNoScore : null;
        return new BenchmarkRunJudgeView(BenchmarkRunJudgeStates.None, AttemptId: null, Score: null, PolicyRevision: null, PolicyRevisionId: null,
            AttemptSequence: null, CohortGeneration: null, ExecutionKey: null, ErrorMessage: null, PolicyCurrent: false,
            ExecutionCurrent: false, reason);
    }

    /// <summary>
    ///     The derived judge state for each run that has a current attempt. Runs without one are absent, and the caller
    ///     substitutes the <c>none</c> view — a run with no attempt has nothing to derive from.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, BenchmarkRunJudgeView>> LoadJudgeViewsAsync(IReadOnlyList<Guid> runIds, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0)
        {
            return new Dictionary<Guid, BenchmarkRunJudgeView>();
        }

        // Flat columns only across all four tables, so nothing is decrypted to answer "is this run ranked?".
        var rows = await (from run in _dbContext.BenchmarkRuns.AsNoTracking()
            join attempt in _dbContext.BenchmarkJudgeAttempts.AsNoTracking() on run.CurrentJudgeAttemptId equals attempt.Id
            join revision in _dbContext.BenchmarkJudgePolicyRevisions.AsNoTracking() on attempt.PolicyRevisionId equals revision.Id
            join project in _dbContext.BenchmarkProjects.AsNoTracking() on run.ProjectId equals project.Id
            where runIds.Contains(run.Id)
            select new JudgeViewRow(run.Id,
                attempt.Id,
                run.UserScore,
                attempt.Status,
                attempt.Score,
                attempt.Sequence,
                attempt.CohortGeneration,
                attempt.JudgeExecutionKey,
                attempt.ErrorMessage,
                attempt.PolicyRevisionId,
                revision.Revision,
                revision.CohortGeneration,
                revision.ReferenceExecutionKey,
                project.CurrentJudgePolicyRevisionId)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(static row => row.RunId, BuildJudgeView);
    }

    /// <summary>
    ///     Rank membership, decided at read time (plan §3.5): an operator score always ranks; a judge score ranks only
    ///     under the project's current policy revision, in that revision's live cohort generation, with the execution
    ///     key the cohort was claimed with. Anything else is honestly unranked, with a reason the UI can act on.
    /// </summary>
    private static BenchmarkRunJudgeView BuildJudgeView(JudgeViewRow row)
    {
        var state = row.Status switch
        {
            BenchmarkJudgeAttemptStatus.Queued => BenchmarkRunJudgeStates.Queued,
            BenchmarkJudgeAttemptStatus.Running => BenchmarkRunJudgeStates.Running,
            BenchmarkJudgeAttemptStatus.Succeeded => BenchmarkRunJudgeStates.Succeeded,
            BenchmarkJudgeAttemptStatus.Failed => BenchmarkRunJudgeStates.Failed,
            _ => BenchmarkRunJudgeStates.Cancelled
        };
        var policyCurrent = row.ProjectCurrentRevisionId == row.PolicyRevisionId;
        var executionCurrent = row.AttemptGeneration == row.RevisionGeneration
                               && row.ExecutionKey is not null
                               && string.Equals(row.ExecutionKey, row.ReferenceExecutionKey, StringComparison.Ordinal);
        return new BenchmarkRunJudgeView(state,
            row.AttemptId,
            row.Score,
            row.RevisionNumber,
            row.PolicyRevisionId,
            row.Sequence,
            row.AttemptGeneration,
            row.ExecutionKey,
            row.ErrorMessage,
            policyCurrent,
            executionCurrent,
            RankExclusionReason(row, policyCurrent, executionCurrent));
    }

    private static string? RankExclusionReason(JudgeViewRow row, bool policyCurrent, bool executionCurrent)
    {
        if (row.UserScore is not null)
        {
            return null;
        }

        return row.Status switch
        {
            BenchmarkJudgeAttemptStatus.Queued or BenchmarkJudgeAttemptStatus.Running => BenchmarkRunJudgeStates.ReasonJudgePending,
            BenchmarkJudgeAttemptStatus.Failed => BenchmarkRunJudgeStates.ReasonJudgeFailed,
            BenchmarkJudgeAttemptStatus.Cancelled => BenchmarkRunJudgeStates.ReasonJudgeCancelled,
            _ when row.Score is null => BenchmarkRunJudgeStates.ReasonNoScore,
            _ when !policyCurrent => BenchmarkRunJudgeStates.ReasonPolicyOutdated,
            _ when row.AttemptGeneration != row.RevisionGeneration => BenchmarkRunJudgeStates.ReasonGenerationStale,
            _ when row.ExecutionKey is null => BenchmarkRunJudgeStates.ReasonExecutionIdentityIncomplete,
            _ when !executionCurrent => BenchmarkRunJudgeStates.ReasonExecutionKeyMismatch,
            _ => null
        };
    }

    /// <summary>The flat columns the derived judge view is computed from. Never leaves this class.</summary>
    private sealed record JudgeViewRow(
        Guid RunId,
        Guid AttemptId,
        int? UserScore,
        BenchmarkJudgeAttemptStatus Status,
        int? Score,
        int Sequence,
        int AttemptGeneration,
        string? ExecutionKey,
        string? ErrorMessage,
        Guid PolicyRevisionId,
        int RevisionNumber,
        int RevisionGeneration,
        string? ReferenceExecutionKey,
        Guid? ProjectCurrentRevisionId);

    /// <summary>
    ///     Applies a project write's judge half to the tracked project: null policy disables, an unchanged hash is a
    ///     no-op, anything else get-or-creates the revision, resets its cohort and repoints the project. Returns the
    ///     revision the project ends up on, or <see langword="null" /> when judging was turned off.
    /// </summary>
    private async Task<(BenchmarkJudgePolicyRevision Revision, bool WasCreated)?> ApplyJudgePolicyChangeAsync(BenchmarkProject project,
        BenchmarkJudgePolicyChangeInput change,
        long now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.PolicyJson is not { } policyJson)
        {
            project.CurrentJudgePolicyRevisionId = null;
            return null;
        }

        if (policyJson.IsEmpty)
        {
            throw new BenchmarkValidationException("A judge policy cannot be empty.");
        }

        EnsurePolicyHash(change.PolicyHash);
        var current = project.CurrentJudgePolicyRevisionId is { } currentRevisionId
            ? await RequireJudgePolicyRevisionAsync(currentRevisionId, cancellationToken).ConfigureAwait(false)
            : null;

        // Same rule as an explicit activation: re-pointing at the policy the project already holds must not reset the
        // cohort, or saving an unrelated field edit would drop every ranked run out of the ranking.
        if (current is not null && string.Equals(current.PolicyHash, change.PolicyHash, StringComparison.Ordinal))
        {
            return (current, false);
        }

        return await RepointJudgePolicyAsync(project, policyJson, change.PolicyHash, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Get-or-creates the revision for this policy, starts it on a fresh cohort, and points the project at it. The
    ///     caller owns the transaction and the project version bump.
    /// </summary>
    private async Task<(BenchmarkJudgePolicyRevision Revision, bool WasCreated)> RepointJudgePolicyAsync(BenchmarkProject project,
        ReadOnlyMemory<byte> policyJson,
        string policyHash,
        long now,
        CancellationToken cancellationToken)
    {
        var (revision, wasCreated) = await GetOrCreateJudgePolicyRevisionAsync(project.Id, policyJson, policyHash, now, cancellationToken).ConfigureAwait(false);
        if (!wasCreated)
        {
            // A revision the project has held before starts a fresh cohort; a brand new one is already at generation 1.
            revision.ReferenceExecutionKey = null;
            revision.CohortGeneration = checked(revision.CohortGeneration + 1);
        }

        project.CurrentJudgePolicyRevisionId = revision.Id;
        return (revision, wasCreated);
    }

    /// <summary>
    ///     The project's eligible runs and — with a seed — one fresh Queued attempt each, inserted inside the caller's
    ///     transaction. Enqueuing here rather than in a follow-up loop is what makes a cohort reset all-or-nothing: a
    ///     reset that committed with only some attempts enqueued would rank a cohort against runs never re-judged.
    ///     No already-applied guard: the caller has just reset the cohort, and every eligible run belongs to it.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> EnqueueCohortAttemptsAsync(Guid projectId,
        BenchmarkJudgePolicyRevision revision,
        BenchmarkJudgeAttemptSeed? seed,
        long now,
        CancellationToken cancellationToken)
    {
        if (seed is null)
        {
            return await SucceededRunIdsAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        // Tracked, because each run takes a new attempt pointer and a version bump. The caller has already refused
        // the call if any attempt of the project is active, so no eligible run can be mid-judging here.
        var runs = await _dbContext.BenchmarkRuns
                                   .Where(entity => entity.ProjectId == projectId
                                                    && entity.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
                                                    && entity.OutputPartsJson != null)
                                   .OrderBy(entity => entity.CreatedAtUtc)
                                   .ThenBy(entity => entity.Id)
                                   .ToListAsync(cancellationToken)
                                   .ConfigureAwait(false);
        foreach (var run in runs)
        {
            _ = await InsertJudgeAttemptAsync(run, revision, seed.RuntimeJson, seed.RuntimeUnresolvedReason, seed.LaunchIntent, now, cancellationToken)
                .ConfigureAwait(false);
            run.Version++;
            run.UpdatedAtUtc = now;
        }

        return runs.Select(run => run.Id).ToArray();
    }

    /// <summary>The project's succeeded runs with stored output — exactly the set a re-judge must cover.</summary>
    private async Task<IReadOnlyList<Guid>> SucceededRunIdsAsync(Guid projectId, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkRuns.AsNoTracking()
                        .Where(entity => entity.ProjectId == projectId
                                         && entity.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
                                         && entity.OutputPartsJson != null)
                        .OrderBy(entity => entity.CreatedAtUtc)
                        .ThenBy(entity => entity.Id)
                        .Select(entity => entity.Id)
                        .ToArrayAsync(cancellationToken)
                        .ConfigureAwait(false);

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

    private static void EnsurePolicyHash([NotNull] string? policyHash)
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

    private static bool IsPrimaryTerminal(BenchmarkPrimaryStatus status) =>
        status is BenchmarkPrimaryStatus.Succeeded or BenchmarkPrimaryStatus.Failed or BenchmarkPrimaryStatus.Cancelled;

    private static string Sanitize(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 1024 ? normalized : normalized[..1024];
    }

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
        new(entity.Id, entity.Name, entity.CoreTaskJson.ToArray(), entity.ContextTokens, entity.AgentDefinitionId,
            entity.CurrentJudgePolicyRevisionId is not null, entity.CurrentJudgePolicyRevisionId, frozen,
            entity.Version, entity.CreatedAtUtc, entity.UpdatedAtUtc, entity.MaxOutputTokens);

    // One place writes the six throughput columns, so the success path and the cancel-reset path can never disagree
    // about which of them a run carries.
    private static void ApplyThroughput(BenchmarkRun run, BenchmarkRunThroughput? throughput)
    {
        run.TtftMs = throughput?.TtftMs;
        run.PromptTokens = throughput?.PromptTokens;
        run.PromptMs = throughput?.PromptMs;
        run.GenerationTokens = throughput?.GenerationTokens;
        run.GenerationMs = throughput?.GenerationMs;
        run.CachedPromptTokens = throughput?.CachedPromptTokens;
    }

    private static BenchmarkRunThroughput? ToThroughput(BenchmarkRun entity) =>
        entity.TtftMs is null
        && entity.PromptTokens is null
        && entity.PromptMs is null
        && entity.GenerationTokens is null
        && entity.GenerationMs is null
        && entity.CachedPromptTokens is null
            ? null
            : new BenchmarkRunThroughput(entity.TtftMs, entity.PromptTokens, entity.PromptMs, entity.GenerationTokens,
                entity.GenerationMs, entity.CachedPromptTokens);

    private static BenchmarkRunRecord ToRecord(BenchmarkRun entity) =>
        new(entity.Id, entity.ProjectId, entity.RuntimeSnapshotJson.ToArray(), entity.PrimaryModelName, entity.PrimaryModelOrigin,
            entity.ModelContentFingerprint, entity.AgentName, entity.AgentVersion, entity.RequestedContextTokens, entity.PrimaryStatus,
            entity.EffectiveContextTokens, entity.DurationMs, entity.TotalTokens, entity.TokensPerSecond, CopyOptional(entity.OutputPartsJson),
            entity.LastStreamSequence, entity.UserScore, entity.PrimaryErrorMessage, entity.Version, entity.CreatedAtUtc, entity.StartedAtUtc,
            entity.PrimaryCompletedAtUtc, entity.UpdatedAtUtc,
            ToIntent(entity.PrimaryVariant, entity.PrimaryKvCacheType, entity.PrimaryKvCacheTypeSource, entity.PrimaryKvAutoReason,
                entity.PrimaryFlashAttentionMode, entity.PrimaryIntendedLaunchIdentity, entity.PrimaryIntendedExecutableSha256),
            ToEvidence(entity.PrimaryLaunchReceiptJson, entity.PrimaryEnvironmentFactsJson, entity.PrimaryReceiptHash,
                entity.PrimaryEnvironmentFactsHash, entity.PrimaryEffectiveLaunchIdentity, entity.PrimaryEffectiveBackend,
                entity.PrimaryPlacementOffloaded, entity.PrimaryPlacementTotal, entity.PrimaryLaunchExecutableSha256,
                entity.PrimaryLaunchHasAuxAssets, entity.PrimaryLaunchKvCacheTypeSource),
            entity.PrimaryStopReason,
            Throughput: ToThroughput(entity));

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
