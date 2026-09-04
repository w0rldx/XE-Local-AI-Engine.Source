namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    public async Task<BenchmarkRunRecord> StartRunAsync(BenchmarkStartRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return (await StartRunsAsync([command], command.ExpectedProjectVersion, cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyList<BenchmarkRunRecord>> StartRunsAsync(IReadOnlyList<BenchmarkStartRunCommand> commands,
        long expectedProjectVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
        {
            throw new ArgumentException("At least one run must be started.", nameof(commands));
        }

        var projectId = commands[0].ProjectId;
        foreach (var command in commands)
        {
            ValidateStart(command);
            if (command.ProjectId != projectId)
            {
                throw new ArgumentException("Every run of one insert must belong to the same project.", nameof(commands));
            }
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

        // ONE compare-and-swap for the whole group, and one commit. A per-run CAS chained on its own predecessor let a
        // concurrent writer land between run i and run i+1: the caller saw a VersionConflict and no ids while the runs
        // already inserted stayed queued and ran. All-or-nothing is also what lets a batch caller advance its expected
        // version by the returned count.
        EnsureVersion(project.Version, expectedProjectVersion);

        // Distinct by reference: a repeat group shares one guard instance, and re-running its dependency read once per
        // repeat would be N identical round trips.
        foreach (var guard in commands.Select(static command => command.FreezeCommitGuard).OfType<IBenchmarkFreezeCommitGuard>().Distinct())
        {
            if (!await guard.IsCurrentAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new BenchmarkConflictException("FreezeDependencyChanged");
            }
        }

        var now = Now();
        var runs = new List<BenchmarkRun>(commands.Count);
        foreach (var command in commands)
        {
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
                InvocationTimeoutSeconds = command.InvocationTimeoutSeconds,
                PrimaryStatus = BenchmarkPrimaryStatus.Queued,
                PrimaryVariant = command.PrimaryLaunchIntent?.Variant,
                PrimaryKvCacheType = command.PrimaryLaunchIntent?.KvCacheType,
                PrimaryKvCacheTypeSource = command.PrimaryLaunchIntent?.KvCacheTypeSource,
                PrimaryKvAutoReason = command.PrimaryLaunchIntent?.KvAutoReason,
                PrimaryFlashAttentionMode = command.PrimaryLaunchIntent?.FlashAttentionMode,
                PrimaryIntendedLaunchIdentity = command.PrimaryLaunchIntent?.IntendedLaunchIdentity,
                PrimaryIntendedExecutableSha256 = command.PrimaryLaunchIntent?.IntendedExecutableSha256,
                PrimaryLaunchIdentityScheme = command.PrimaryLaunchIntent?.LaunchIdentityScheme,
                RepeatGroupId = command.RepeatGroupId,
                RepeatIndex = command.RepeatIndex,
                IsWarmup = command.IsWarmup,
                RepeatMode = command.RepeatMode,
                SamplingSeed = command.SamplingSeed,
                SamplingTemperature = command.SamplingTemperature,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            // The identity stamps are NOT NULL, so a freeze that names no cell still names one: the run's own
            // singleton. A project with one item and no repeats is exactly this case and ranks as it always has.
            run.TaskItemId = command.TaskItemId;
            run.TaskItemIndex = command.TaskItemIndex;
            run.CellKey = string.IsNullOrEmpty(command.CellKey) ? SingletonCellKey(run.Id) : command.CellKey;
            run.TaskInputHash = command.TaskInputHash ?? LegacyTaskHash;
            run.TaskItemSetHash = command.TaskItemSetHash ?? project.TaskItemSetHash ?? LegacyTaskHash;

            // Added in caller order, and the queue sequence is assigned in insert order, which is what makes a repeat
            // group run back-to-back — warm-up first, then 1..N — rather than interleaved with whatever else is queued.
            _dbContext.BenchmarkRuns.Add(run);
            _dbContext.BenchmarkWorkItems.Add(new BenchmarkWorkItem
            {
                RunId = run.Id,
                Kind = BenchmarkWorkKind.Primary,
                Status = BenchmarkWorkStatus.Queued,
                Attempt = 1,
                Version = 1,
                EnqueuedAtUtc = now
            });

            // Fidelity is NOT queued here. A measurement enqueued at freeze outlives the run it belongs to: when the
            // primary then fails or is cancelled, hours of GPU work stay queued against a run that has no answer to
            // measure. It is seeded on primary SUCCESS instead, exactly where the judge attempt is seeded.
            //
            runs.Add(run);
        }

        // What freeze does record is the cells that will never be measured. One fidelity item per measured CELL, not
        // per repeat and not per ITEM: perplexity and KL divergence measure the model file against a corpus, so they
        // are identical for every repeat and every task item of one cell and would otherwise cost N times the GPU
        // hours (and, for KLD, N times ~25 GB of base logits) to produce N copies of one number. A warm-up is never
        // measured at all — it exists to absorb first-launch costs, not to be compared. Decided over the batch rather
        // than by a query because these rows are not saved yet.
        if (project.FidelityEnabled)
        {
            var measured = runs.Where(IsFidelityMeasuredRepeat)
                               .GroupBy(static run => run.CellKey, StringComparer.Ordinal)
                               .Select(static cell => cell.OrderBy(static run => run.TaskItemIndex ?? int.MinValue).First().Id)
                               .ToHashSet();
            foreach (var run in runs.Where(run => !measured.Contains(run.Id)))
            {
                // Recorded rather than left null: "this cell's repeats are covered by repeat 1" and "fidelity was
                // never asked for" are different facts, and the UI shows a different thing for each.
                run.FidelityStatus = "skipped";
            }
        }

        project.Version += commands.Count;
        project.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        // One judge read for the whole group, not one per run: the batch was inserted in a single round trip precisely
        // so a repeat group does not pay N of them, and materializing it per run gave that back.
        var views = await LoadJudgeViewsAsync([.. runs.Select(static run => run.Id)], cancellationToken).ConfigureAwait(false);
        return [.. runs.Select(run => ToRecordWithJudge(run, views))];
    }

    public async Task<BenchmarkRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (await _dbContext.BenchmarkRuns.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken).ConfigureAwait(false) is not { } entity)
        {
            return null;
        }

        var views = await LoadJudgeViewsAsync([runId], cancellationToken).ConfigureAwait(false);
        var (judge, qualityScore, qualityScoreSource, _) = ApplyRunExclusions(JudgeViewFor(views, runId, entity.UserScore),
            entity.UserScore,
            entity.IsWarmup,
            entity.PrimaryStopReason,
            await LoadRunIdentityAsync(entity, cancellationToken).ConfigureAwait(false));
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
        string? modelContentFingerprint = null,
        bool includeUnscored = true,
        CancellationToken cancellationToken = default)
    {
        // Rank is computed over the WHOLE project, never the page: a run's position is a property of the project, and
        // paging must not renumber it. Filters narrow which rows come back, not what they are ranked against.
        var ranking = await LoadRankingAsync(projectId, cancellationToken).ConfigureAwait(false);
        return await PageAsync(ranking, projectId, skip, take, modelContentFingerprint, includeUnscored, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BenchmarkRunPage> ListAllRunsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        // ONE ranking for the whole export. Paging through ListRunsAsync recomputed it per page, and the ranking is a
        // whole-project scan plus a judge-view join across three more tables — work that is identical every time,
        // because a run's rank is a property of the project rather than of the page it lands on.
        var ranking = await LoadRankingAsync(projectId, cancellationToken).ConfigureAwait(false);
        return await PageAsync(ranking, projectId, skip: 0, int.MaxValue, modelContentFingerprint: null, includeUnscored: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    private async Task<BenchmarkRunPage> PageAsync(BenchmarkProjectRanking ranking,
        Guid projectId,
        int skip,
        int take,
        string? modelContentFingerprint,
        bool includeUnscored,
        CancellationToken cancellationToken)
    {
        var runs = _dbContext.BenchmarkRuns.AsNoTracking().Where(entity => entity.ProjectId == projectId);
        if (modelContentFingerprint is { Length: > 0 })
        {
            runs = runs.Where(entity => entity.ModelContentFingerprint == modelContentFingerprint);
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
        // Newest first, but a repeat group ASCENDING by repeat index inside its millisecond: every run of a group is
        // inserted by one freeze, so `Now()` is the same value for all of them and the Id tiebreak alone returned a
        // group in Guid order — the table rendered `#3, #1, warm-up, #4, #2`. The client re-sorts by rank, and a
        // group's unranked rows (the warm-up above all) tie there too, so this server order is what the reader sees.
        // The Id tiebreak stays LAST and keeps paging deterministic.
        var items = await runs.OrderByDescending(entity => entity.CreatedAtUtc)
                              .ThenBy(entity => entity.RepeatIndex)
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
                                  entity.PrimaryStopReason,
                                  null,
                                  null,
                                  null,
                                  null,

                                  // Inline rather than through ToThroughput: this is a server-side projection, and a
                                  // helper call would not translate. Absence is decided by all SEVEN columns being
                                  // NULL, and every one of them is projected — the same rule and the same members the
                                  // entity-materializing ToThroughput uses. Omitting one here empties that column in
                                  // the runs table, the CSV export and the repeat statistics, while a single-run read
                                  // keeps showing it.
                                  entity.TtftMs == null
                                  && entity.PromptTokens == null
                                  && entity.PromptMs == null
                                  && entity.GenerationTokens == null
                                  && entity.GenerationMs == null
                                  && entity.CachedPromptTokens == null
                                  && entity.SegmentCount == null
                                      ? null
                                      : new BenchmarkRunThroughput(entity.TtftMs,
                                          entity.PromptTokens,
                                          entity.PromptMs,
                                          entity.GenerationTokens,
                                          entity.GenerationMs,
                                          entity.CachedPromptTokens,
                                          entity.SegmentCount),
                                  entity.RepeatGroupId,
                                  entity.RepeatIndex,
                                  entity.IsWarmup,

                                  // Positional, including the generation timeout the listing used to leave defaulted:
                                  // an expression tree cannot take an out-of-position named argument.
                                  entity.InvocationTimeoutSeconds,
                                  entity.RepeatMode,
                                  entity.SamplingSeed,
                                  entity.SamplingTemperature,
                                  null,
                                  entity.TaskItemId,
                                  entity.TaskItemIndex,
                                  entity.CellKey,
                                  entity.TaskInputHash,
                                  entity.TaskItemSetHash))
                              .ToArrayAsync(cancellationToken)
                              .ConfigureAwait(false);

        // One extra query for the page rather than a join inside the no-payload projection: the judge view is derived
        // from three more tables, and folding it in would make that projection unreadable.
        return new BenchmarkRunPage([.. items.Select(item => WithRanking(item, ranking))], totalCount, ranking.Cohort);
    }
}
