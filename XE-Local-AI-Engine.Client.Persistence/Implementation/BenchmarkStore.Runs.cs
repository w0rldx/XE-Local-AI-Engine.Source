namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    public async Task<BenchmarkRunRecord> StartRunAsync(BenchmarkStartRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return (await StartRunsAsync([command], command.ExpectedProjectVersion, cancellationToken))[0];
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

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var project = await RequireProjectAsync(projectId, cancellationToken);

        // ONE compare-and-swap for the whole group, and one commit: a per-run CAS chained on its own predecessor lets a concurrent writer land between run i and i+1,
        // so the caller sees a VersionConflict and no ids while the runs already inserted stay queued and run. All-or-nothing also lets a caller advance by the count.
        EnsureVersion(project.Version, expectedProjectVersion);

        // Distinct by reference: a repeat group shares one guard instance, and re-running its dependency read once per
        // repeat would be N identical round trips.
        foreach (var guard in commands.Select(static command => command.FreezeCommitGuard).OfType<IBenchmarkFreezeCommitGuard>().Distinct())
        {
            if (!await guard.IsCurrentAsync(cancellationToken))
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

            // Fidelity is NOT queued here: a measurement enqueued at freeze outlives the run it belongs to, and when the primary then fails or is cancelled, hours
            // of GPU work stay queued against a run with no answer to measure. It is seeded on primary SUCCESS instead, exactly where the judge attempt is seeded.
            runs.Add(run);
        }

        // What freeze DOES record is the cells that will never be measured: one fidelity item per measured CELL, not per repeat and not per ITEM, since perplexity
        // and KL divergence would cost N times the GPU hours (for KLD, N times ~25 GB of base logits) for one number. A warm-up absorbs first-launch cost, so never.
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
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // One judge read for the whole group, not one per run: the batch was inserted in a single round trip precisely
        // so a repeat group does not pay N of them, and materializing it per run gave that back.
        var views = await LoadJudgeViewsAsync([.. runs.Select(static run => run.Id)], cancellationToken);
        return [.. runs.Select(run => ToRecordWithJudge(run, views))];
    }

    public async Task<BenchmarkRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (await _dbContext.BenchmarkRuns.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken) is not { } entity)
        {
            return null;
        }

        var views = await LoadJudgeViewsAsync([runId], cancellationToken);
        var (judge, qualityScore, qualityScoreSource, _) = ApplyRunExclusions(JudgeViewFor(views, runId, entity.UserScore),
            entity.UserScore,
            entity.IsWarmup,
            entity.PrimaryStopReason,
            await LoadRunIdentityAsync(entity, cancellationToken));
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
        var ranking = await LoadRankingAsync(projectId, cancellationToken);
        return await PageAsync(ranking, projectId, skip, take, modelContentFingerprint, includeUnscored, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BenchmarkRunPage> ListAllRunsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        // ONE ranking for the whole export: paging through ListRunsAsync recomputes it per page, and the ranking is a whole-project scan plus a judge-view join
        // across three more tables — identical work every time, because a run's rank is a property of the project rather than of the page it lands on.
        var ranking = await LoadRankingAsync(projectId, cancellationToken);
        return await PageAsync(ranking, projectId, skip: 0, int.MaxValue, modelContentFingerprint: null, includeUnscored: true, cancellationToken);
    }

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

        var totalCount = await runs.CountAsync(cancellationToken);

        // Column projection, not entity materialization: the four encrypted payload columns are never read, so a 200-row page costs no crypto and every summary field
        // is flat. Nested records are rebuilt from flat columns, presence decided per block. A local, not inline: EF rejects a ReadOnlyMemory constant in a projection.
        var noPayload = default(ReadOnlyMemory<byte>);
        // Newest first, but a repeat group ASCENDING by repeat index inside its millisecond: one freeze inserts every run of a group, so Now() is identical and the
        // Id tiebreak alone renders them in Guid order. The client re-sorts by rank and unranked rows tie, so this order is what a reader sees; the Id tiebreak is LAST.
        var items = await runs.OrderByDescending(entity => entity.CreatedAtUtc)
                              .ThenBy(entity => entity.RepeatIndex)
                              .ThenByDescending(entity => entity.Id)
                              .Skip(skip)
                              .Take(take)
                              .Select(entity => new BenchmarkRunRecord
                              {
                                  Id = entity.Id,
                                  ProjectId = entity.ProjectId,
                                  RuntimeSnapshotJson = noPayload,
                                  PrimaryModelName = entity.PrimaryModelName,
                                  PrimaryModelOrigin = entity.PrimaryModelOrigin,
                                  ModelContentFingerprint = entity.ModelContentFingerprint,
                                  AgentName = entity.AgentName,
                                  AgentVersion = entity.AgentVersion,
                                  RequestedContextTokens = entity.RequestedContextTokens,
                                  PrimaryStatus = entity.PrimaryStatus,
                                  EffectiveContextTokens = entity.EffectiveContextTokens,
                                  DurationMs = entity.DurationMs,
                                  TotalTokens = entity.TotalTokens,
                                  TokensPerSecond = entity.TokensPerSecond,
                                  OutputPartsJson = null,
                                  LastStreamSequence = entity.LastStreamSequence,
                                  UserScore = entity.UserScore,
                                  PrimaryErrorMessage = entity.PrimaryErrorMessage,
                                  Version = entity.Version,
                                  CreatedAtUtc = entity.CreatedAtUtc,
                                  StartedAtUtc = entity.StartedAtUtc,
                                  PrimaryCompletedAtUtc = entity.PrimaryCompletedAtUtc,
                                  UpdatedAtUtc = entity.UpdatedAtUtc,
                                  PrimaryLaunchIntent = entity.PrimaryVariant == null
                                      ? null
                                      : new BenchmarkRunLaunchIntent
                                      {
                                          Variant = entity.PrimaryVariant,
                                          KvCacheType = entity.PrimaryKvCacheType!,
                                          KvCacheTypeSource = entity.PrimaryKvCacheTypeSource!,
                                          KvAutoReason = entity.PrimaryKvAutoReason,
                                          FlashAttentionMode = entity.PrimaryFlashAttentionMode!,
                                          IntendedLaunchIdentity = entity.PrimaryIntendedLaunchIdentity!,
                                          IntendedExecutableSha256 = entity.PrimaryIntendedExecutableSha256
                                      },
                                  PrimaryLaunchEvidence = entity.PrimaryEnvironmentFactsHash == null
                                      ? null
                                      : new BenchmarkRunLaunchEvidence
                                      {
                                          ReceiptJson = null,
                                          EnvironmentFactsJson = null,
                                          ReceiptHash = entity.PrimaryReceiptHash,
                                          EnvironmentFactsHash = entity.PrimaryEnvironmentFactsHash,
                                          EffectiveLaunchIdentity = entity.PrimaryEffectiveLaunchIdentity,
                                          EffectiveBackend = entity.PrimaryEffectiveBackend,
                                          PlacementOffloaded = entity.PrimaryPlacementOffloaded,
                                          PlacementTotal = entity.PrimaryPlacementTotal,
                                          ExecutableSha256 = entity.PrimaryLaunchExecutableSha256,
                                          HasAuxAssets = entity.PrimaryLaunchHasAuxAssets,
                                          KvCacheTypeSource = entity.PrimaryLaunchKvCacheTypeSource
                                      },
                                  PrimaryStopReason = entity.PrimaryStopReason,
                                  Judge = null,
                                  QualityScore = null,
                                  QualityScoreSource = null,
                                  Rank = null,
                                  // Inline rather than through ToThroughput: a helper call would not translate in a server-side projection.
                                  // Absence is all SEVEN columns NULL, every one projected — the rule ToThroughput uses; omitting one empties that column everywhere.
                                  Throughput = entity.TtftMs == null
                                  && entity.PromptTokens == null
                                  && entity.PromptMs == null
                                  && entity.GenerationTokens == null
                                  && entity.GenerationMs == null
                                  && entity.CachedPromptTokens == null
                                  && entity.SegmentCount == null
                                      ? null
                                      : new BenchmarkRunThroughput
                                      {
                                          TtftMs = entity.TtftMs,
                                          PromptTokens = entity.PromptTokens,
                                          PromptMs = entity.PromptMs,
                                          GenerationTokens = entity.GenerationTokens,
                                          GenerationMs = entity.GenerationMs,
                                          CachedPromptTokens = entity.CachedPromptTokens,
                                          SegmentCount = entity.SegmentCount
                                      },
                                  RepeatGroupId = entity.RepeatGroupId,
                                  RepeatIndex = entity.RepeatIndex,
                                  IsWarmup = entity.IsWarmup,
                                  // Positional, including the generation timeout the listing used to leave defaulted:
                                  // an expression tree cannot take an out-of-position named argument.
                                  InvocationTimeoutSeconds = entity.InvocationTimeoutSeconds,
                                  RepeatMode = entity.RepeatMode,
                                  SamplingSeed = entity.SamplingSeed,
                                  SamplingTemperature = entity.SamplingTemperature,
                                  Fidelity = null,
                                  TaskItemId = entity.TaskItemId,
                                  TaskItemIndex = entity.TaskItemIndex,
                                  CellKey = entity.CellKey,
                                  TaskInputHash = entity.TaskInputHash,
                                  TaskItemSetHash = entity.TaskItemSetHash
                              })
                              .ToArrayAsync(cancellationToken);

        // One extra query for the page rather than a join inside the no-payload projection: the judge view is derived
        // from three more tables, and folding it in would make that projection unreadable.
        return new BenchmarkRunPage { Items = [.. items.Select(item => WithRanking(item, ranking))], TotalCount = totalCount, RankCohort = ranking.Cohort };
    }
}
