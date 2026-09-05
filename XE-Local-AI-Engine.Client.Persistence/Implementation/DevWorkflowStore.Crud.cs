namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed partial class DevWorkflowStore
{
    public async Task<DevWorkflowWorkItemSnapshot> CreateWorkItemAsync(CreateDevWorkflowWorkItemCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.Title, nameof(command.Title));
        EnsureNotBlank(command.Request, nameof(command.Request));

        var now = Now();
        var workItem = new DevWorkflowWorkItem
        {
            Id = command.WorkItemId,
            Title = command.Title,
            Request = Utf8(command.Request),
            Status = DevWorkflowWorkItemStatus.Draft,
            DevelopmentProjectId = command.DevelopmentProjectId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1
        };
        _dbContext.DevWorkflowWorkItems.Add(workItem);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            _dbContext.ChangeTracker.Clear();
            throw new DevWorkflowConcurrencyException($"A development workflow work item already exists for id '{command.WorkItemId}'.", exception);
        }

        return WorkItemSnapshot(workItem, latestRun: null, DevWorkflowNodeCounters.Empty);
    }

    public async Task<DevWorkflowWorkItemSnapshot> UpdateWorkItemAsync(UpdateDevWorkflowWorkItemCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Title is not null)
        {
            EnsureNotBlank(command.Title, nameof(command.Title));
        }

        if (command.Request is not null)
        {
            EnsureNotBlank(command.Request, nameof(command.Request));
        }

        // Re-read and re-applied when the row moves underneath this edit. The version is a concurrency token and the
        // RUNTIME is its other writer — a dispatcher tick that moves the item to Active mid-PATCH is correct behaviour,
        // not a conflict, and the two writes touch disjoint fields because status is absent from this command by
        // design. So the honest answer to losing that race is the caller's fields applied to the row as it now stands.
        // Bounded at three, because a retry that never gave up would sit here for as long as the run kept moving; past
        // that the caller gets a retryable conflict rather than the 500 an unmapped store exception would become.
        const int maxAttempts = 3;
        var attempt = 0;
        while (true)
        {
            attempt++;
            var workItem = await _dbContext.DevWorkflowWorkItems.SingleOrDefaultAsync(entity => entity.Id == command.WorkItemId, cancellationToken).ConfigureAwait(false)
                           ?? throw new DevWorkflowNotFoundException($"Development workflow work item '{command.WorkItemId}' was not found.");
            if (command.ExpectedVersion != DevWorkflowVersions.Any && workItem.Version != command.ExpectedVersion)
            {
                throw new DevWorkflowConcurrencyException($"The work item version is stale (expected {command.ExpectedVersion}, current {workItem.Version}).");
            }

            if (command.Title is not null)
            {
                workItem.Title = command.Title;
            }

            if (command.Request is not null)
            {
                workItem.Request = Utf8(command.Request);
            }

            if (command.DevelopmentProjectId is { } projectId)
            {
                workItem.DevelopmentProjectId = projectId;
            }

            // Status is deliberately absent from this command: it is the runtime's to write, inside the transaction that
            // transitions a run. Letting a client set it would make the list filter lie the moment the two disagreed.
            workItem.Version++;
            workItem.UpdatedAtUtc = Now();
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                // Cleared, or the next pass is handed back the stale instance this one holds instead of the row the
                // winner wrote — and re-applying the edit to that would overwrite the status it just landed.
                _dbContext.ChangeTracker.Clear();
                if (attempt == maxAttempts)
                {
                    throw new DevWorkflowConcurrencyException("The work item kept changing while this edit was being written, so it was not applied.", exception);
                }

                continue;
            }

            return await ComposeWorkItemAsync(workItem, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DevWorkflowWorkItemSnapshot>> ListWorkItemsAsync(DevWorkflowWorkItemStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        // Query one: the page, each row carrying its latest run through a correlated subquery.
        var query = _dbContext.DevWorkflowWorkItems.AsNoTracking();
        if (status is { } wanted)
        {
            query = query.Where(entity => entity.Status == wanted);
        }

        var page = await query.OrderByDescending(entity => entity.UpdatedAtUtc)
                              .ThenBy(entity => entity.Id)
                              .Select(entity => new WorkItemProjection(entity,
                                  _dbContext.DevWorkflowRuns.Where(run => run.WorkItemId == entity.Id)
                                            .OrderByDescending(run => run.CreatedAtUtc)
                                            .ThenByDescending(run => run.Id)
                                            .Select(run => new LatestRunProjection(run.Id,
                                                run.Status,
                                                _dbContext.DevWorkflowDefinitions.Where(definition => definition.Id == run.DefinitionId)
                                                          .Select(definition => definition.Name)
                                                          .FirstOrDefault()))
                                            .FirstOrDefault()))
                              .ToListAsync(cancellationToken)
                              .ConfigureAwait(false);

        // Query two: one pass over the node-runs of the listed runs, tallied in memory. Never one query per row.
        var counters = await LoadNodeCountersAsync([.. page.Where(row => row.LatestRun is not null).Select(row => row.LatestRun!.RunId)], cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. page.Select(row => WorkItemSnapshot(row.WorkItem,
                row.LatestRun,
                row.LatestRun is null ? DevWorkflowNodeCounters.Empty : Counters(counters, row.LatestRun.RunId)))
        ];
    }

    public async Task<DevWorkflowWorkItemSnapshot> GetWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        var workItem = await _dbContext.DevWorkflowWorkItems.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == workItemId, cancellationToken).ConfigureAwait(false)
                       ?? throw new DevWorkflowNotFoundException($"Development workflow work item '{workItemId}' was not found.");
        return await ComposeWorkItemAsync(workItem, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevWorkflowWorkItemDeletion> DeleteWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        // Explicit ordered deletes through DevWorkflowPurge: the node connection runs without PRAGMA foreign_keys, so
        // the declared cascades are documentation only and an EF-graph delete would leave every child table populated.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runIds = await _dbContext.DevWorkflowRuns.AsNoTracking()
                                         .Where(entity => entity.WorkItemId == workItemId)
                                         .Select(entity => entity.Id)
                                         .ToListAsync(cancellationToken)
                                         .ConfigureAwait(false);

            // Inside the transaction, so a run that starts between a caller's check and this one still loses: deleting
            // the rows under a live run would leave its executor holding a slot for work nothing will ever settle.
            // The offending run is LOADED rather than merely counted, because this refusal is the operator's only
            // instruction — "cancel that run first" needs to say which run, and it costs the same query.
            if (await _dbContext.DevWorkflowRuns.AsNoTracking()
                                .Where(entity => entity.WorkItemId == workItemId
                                                 && entity.Status != DevWorkflowRunStatus.Completed
                                                 && entity.Status != DevWorkflowRunStatus.Failed
                                                 && entity.Status != DevWorkflowRunStatus.Cancelled)
                                .Select(entity => new
                                {
                                    entity.Id,
                                    entity.Status
                                })
                                .FirstOrDefaultAsync(cancellationToken)
                                .ConfigureAwait(false) is { } live)
            {
                throw new DevWorkflowRunInFlightException($"Run '{live.Id}' is {live.Status}, so work item '{workItemId}' cannot be deleted yet. Cancel the run first.");
            }

            var removed = await CountRowsAsync(runIds, workItemId, cancellationToken).ConfigureAwait(false);

            // Read here, not by the caller beforehand: one query over every run of the item rather than a page the
            // caller has to remember to walk, and gathered only on the path where the guard above has already passed.
            var sessionIds = await _dbContext.DevWorkflowNodeRuns.AsNoTracking()
                                             .Where(entity => runIds.Contains(entity.RunId) && entity.WorkSessionId != null)
                                             .Select(entity => entity.WorkSessionId!.Value)
                                             .Distinct()
                                             .ToListAsync(cancellationToken)
                                             .ConfigureAwait(false);

            await DevWorkflowPurge.DeleteWorkItemAsync(_dbContext, workItemId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _dbContext.ChangeTracker.Clear();
            return new DevWorkflowWorkItemDeletion(removed, runIds, sessionIds);
        }
        catch (DbUpdateException exception)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw new DevWorkflowConcurrencyException("The work item could not be deleted because a database constraint rejected the write.", exception);
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DevWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateDevWorkflowDefinitionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.Name, nameof(command.Name));
        EnsureNotBlank(command.GraphJson, nameof(command.GraphJson));
        if (command.NodeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "A definition node count cannot be negative.");
        }

        var graph = Utf8(command.GraphJson);
        var now = Now();
        var definition = new DevWorkflowDefinition
        {
            Id = command.DefinitionId,
            Name = command.Name,
            GraphJson = graph,
            GraphHash = HashPayload(graph),
            NodeCount = command.NodeCount,
            Source = command.Source,
            SeedSlug = command.SeedSlug,
            Archived = false,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _dbContext.DevWorkflowDefinitions.Add(definition);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            _dbContext.ChangeTracker.Clear();
            throw new DevWorkflowConcurrencyException($"A development workflow definition already exists for id '{command.DefinitionId}' or seed slug '{command.SeedSlug}'.",
                exception);
        }

        return DefinitionSnapshot(definition);
    }

    public async Task<DevWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateDevWorkflowDefinitionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Name is not null)
        {
            EnsureNotBlank(command.Name, nameof(command.Name));
        }

        if (command.GraphJson is not null)
        {
            EnsureNotBlank(command.GraphJson, nameof(command.GraphJson));
        }

        var definition = await LoadDefinitionAsync(command.DefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition.Version != command.ExpectedVersion)
        {
            throw new DevWorkflowConcurrencyException($"The definition version is stale (expected {command.ExpectedVersion}, current {definition.Version}).");
        }

        if (command.Name is not null)
        {
            definition.Name = command.Name;
        }

        if (command.GraphJson is not null)
        {
            // Hash and node count are written together with the graph, every time: that is what lets the list promise
            // never to decrypt a blob and still tell the truth about it.
            var graph = Utf8(command.GraphJson);
            definition.GraphJson = graph;
            definition.GraphHash = HashPayload(graph);
            definition.NodeCount = command.NodeCount ?? definition.NodeCount;
        }
        else if (command.NodeCount is { } nodeCount)
        {
            definition.NodeCount = nodeCount;
        }

        definition.Version++;
        definition.UpdatedAtUtc = Now();
        await SaveDefinitionAsync(cancellationToken).ConfigureAwait(false);
        return DefinitionSnapshot(definition);
    }

    public async Task<IReadOnlyList<DevWorkflowDefinitionSummary>> ListDefinitionsAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        // Projected server-side without graph_json, so no definition blob is ever decrypted to draw the picker.
        var query = _dbContext.DevWorkflowDefinitions.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(entity => !entity.Archived);
        }

        return await query.OrderBy(entity => entity.Name)
                          .ThenBy(entity => entity.Id)
                          .Select(entity => new DevWorkflowDefinitionSummary(entity.Id,
                              entity.Name,
                              entity.GraphHash,
                              entity.NodeCount,
                              entity.Source,
                              entity.SeedSlug,
                              entity.Archived,
                              entity.Version,
                              entity.CreatedAtUtc,
                              entity.UpdatedAtUtc))
                          .ToListAsync(cancellationToken)
                          .ConfigureAwait(false);
    }

    public async Task<DevWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        var definition = await _dbContext.DevWorkflowDefinitions.AsNoTracking()
                                         .SingleOrDefaultAsync(entity => entity.Id == definitionId, cancellationToken)
                                         .ConfigureAwait(false)
                         ?? throw new DevWorkflowNotFoundException($"Development workflow definition '{definitionId}' was not found.");
        return DefinitionSnapshot(definition);
    }

    public async Task<DevWorkflowDefinitionSnapshot> ArchiveDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        var definition = await LoadDefinitionAsync(definitionId, cancellationToken).ConfigureAwait(false);
        if (!definition.Archived)
        {
            definition.Archived = true;
            definition.Version++;
            definition.UpdatedAtUtc = Now();
            await SaveDefinitionAsync(cancellationToken).ConfigureAwait(false);
        }

        return DefinitionSnapshot(definition);
    }

    public async Task<DevWorkflowRunSnapshot> StartRunAsync(StartDevWorkflowRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.DefinitionGraphHash, nameof(command.DefinitionGraphHash));
        EnsureNotBlank(command.GraphJson, nameof(command.GraphJson));
        if (command.NodeRuns is { } seeded)
        {
            // The same check materialization makes, for the same reason: without it a duplicate node key would reach
            // the unique index and come back as "a live run already exists", which is a different problem entirely.
            EnsureSeedsValid(seeded, nameof(command));
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workItem = await _dbContext.DevWorkflowWorkItems.SingleOrDefaultAsync(entity => entity.Id == command.WorkItemId, cancellationToken).ConfigureAwait(false)
                           ?? throw new DevWorkflowNotFoundException($"Development workflow work item '{command.WorkItemId}' was not found.");

            var now = Now();
            var run = new DevWorkflowRun
            {
                Id = command.RunId,
                WorkItemId = command.WorkItemId,
                DefinitionId = command.DefinitionId,
                DefinitionVersion = command.DefinitionVersion,
                DefinitionGraphHash = command.DefinitionGraphHash,
                GraphJson = Utf8(command.GraphJson),
                GraphRevision = 0,
                Status = DevWorkflowRunStatus.Pending,
                LastSequence = 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1
            };
            _dbContext.DevWorkflowRuns.Add(run);
            AddEvent(run, DevWorkflowEventTypes.RunCreated, nodeRunId: null, run.Status.ToString(), operationId: null, detailJson: null);

            // In the same transaction as the run row, so a crash cannot leave a run whose node runs — and with them the
            // caller's per-run inputs, which have no other home — were never written.
            if (command.NodeRuns is { Count: > 0 } seeds)
            {
                AddNodeRuns(run, seeds, now);
                AddEvent(run,
                    DevWorkflowEventTypes.NodeMaterialized,
                    nodeRunId: null,
                    $"{seeds.Count} node run(s)",
                    operationId: null,
                    Utf8(JsonSerializer.Serialize(new MaterializationDetail(seeds.Count, run.GraphRevision), JsonOptions)));
            }

            workItem.Status = DevWorkflowWorkItemStatus.Active;
            workItem.Version++;
            workItem.UpdatedAtUtc = now;

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RunSnapshot(run);
        }
        catch (DbUpdateException exception)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);

            // ux_dev_workflow_runs_live_per_work_item is what rejects a second live run. Its own type rather than a
            // generic invalid transition, because the answer differs: wait for the live run, or cancel it.
            throw new DevWorkflowRunInFlightException(
                $"A live development workflow run already exists for work item '{command.WorkItemId}', or run '{command.RunId}' already exists. ({exception.GetBaseException().Message})",
                exception);
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DevWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.DevWorkflowRuns.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken).ConfigureAwait(false)
                  ?? throw new DevWorkflowNotFoundException($"Development workflow run '{runId}' was not found.");
        return RunSnapshot(run);
    }

    public async Task<IReadOnlyList<DevWorkflowRunSnapshot>> ListRunsAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A run list limit must be positive.");
        }

        var query = _dbContext.DevWorkflowRuns.AsNoTracking();
        if (workItemId is { } owner)
        {
            query = query.Where(entity => entity.WorkItemId == owner);
        }

        if (status is { } wanted)
        {
            query = query.Where(entity => entity.Status == wanted);
        }

        var runs = await query.OrderByDescending(entity => entity.CreatedAtUtc)
                              .ThenByDescending(entity => entity.Id)
                              .Take(limit)
                              .ToListAsync(cancellationToken)
                              .ConfigureAwait(false);
        return [.. runs.Select(RunSnapshot)];
    }

    public async Task<IReadOnlyList<DevWorkflowRunSummary>> ListRunSummariesAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A run list limit must be positive.");
        }

        var query = _dbContext.DevWorkflowRuns.AsNoTracking();
        if (workItemId is { } owner)
        {
            query = query.Where(entity => entity.WorkItemId == owner);
        }

        if (status is { } wanted)
        {
            query = query.Where(entity => entity.Status == wanted);
        }

        // No graph blob in the projection, and the same two-query posture as the work-item list.
        var runs = await query.OrderByDescending(entity => entity.CreatedAtUtc)
                              .ThenByDescending(entity => entity.Id)
                              .Take(limit)
                              .Select(entity => new RunSummaryProjection(entity.Id,
                                  entity.WorkItemId,
                                  entity.DefinitionId,
                                  _dbContext.DevWorkflowDefinitions.Where(definition => definition.Id == entity.DefinitionId)
                                            .Select(definition => definition.Name)
                                            .FirstOrDefault(),
                                  entity.Status,
                                  entity.FailureClass,
                                  entity.StartedAtUtc,
                                  entity.EndedAtUtc,
                                  entity.CreatedAtUtc,
                                  entity.UpdatedAtUtc))
                              .ToListAsync(cancellationToken)
                              .ConfigureAwait(false);

        var counters = await LoadNodeCountersAsync([.. runs.Select(run => run.Id)], cancellationToken).ConfigureAwait(false);
        return
        [
            .. runs.Select(run => new DevWorkflowRunSummary(run.Id,
                run.WorkItemId,
                run.DefinitionId,
                run.DefinitionName,
                run.Status,
                Counters(counters, run.Id),
                run.FailureClass,
                run.StartedAtUtc,
                run.EndedAtUtc,
                run.CreatedAtUtc,
                run.UpdatedAtUtc))
        ];
    }

    public async Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default) =>
    [
        .. await StrandedNodeRuns()
                 .AsNoTracking()
                 .Select(entity => new DevWorkflowReconciledNodeRun(entity.Id,
                     entity.RunId,
                     entity.NodeKey,
                     entity.NodeType,
                     entity.Status,
                     entity.Attempt,
                     entity.WorkSessionId,
                     entity.MaxAttempts))
                 .ToListAsync(cancellationToken)
                 .ConfigureAwait(false)
    ];

    public async Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<DevWorkflowNodeRunVerdict> verdicts,
        DevWorkflowUnjudgedNodeRunBlock? unjudged = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedReason);
        ArgumentNullException.ThrowIfNull(verdicts);

        // Every pass reads the world afresh. A caller that reconciles more than once holds one scope, and the identity
        // map from its earlier pass would hand back run rows as they stood BEFORE whatever moved these node runs — so
        // this would allocate sequence numbers that are already taken. Nothing outside can hold an entity of ours: the
        // store answers snapshots.
        _dbContext.ChangeTracker.Clear();

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stranded = await StrandedNodeRuns().ToListAsync(cancellationToken).ConfigureAwait(false);
            if (stranded.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return [];
            }

            var runIds = stranded.Select(entity => entity.RunId).Distinct().ToList();
            var runs = await _dbContext.DevWorkflowRuns.Where(entity => runIds.Contains(entity.Id)).ToDictionaryAsync(entity => entity.Id, cancellationToken)
                                       .ConfigureAwait(false);

            var judged = verdicts.ToDictionary(verdict => verdict.NodeRunId);
            var now = Now();
            var reconciled = new List<DevWorkflowReconciledNodeRun>(stranded.Count);
            var repairs = new List<(DevWorkflowRun Run, TransitionDevWorkflowNodeRunCommand Command)>();
            foreach (var nodeRun in stranded)
            {
                if (!runs.TryGetValue(nodeRun.RunId, out var run))
                {
                    continue;
                }

                // A row the caller did not judge, or judged as something it no longer is, is left exactly where it is:
                // collapsing it would strand it at Pending with nobody left to decide what re-running it costs. The
                // caller reads again and judges it on its next pass — or ends the matter with `unjudged`, which blocks
                // it for a human off the row in front of us, where no snapshot can be stale.
                var matched = judged.TryGetValue(nodeRun.Id, out var verdict)
                              && verdict.ObservedStatus == nodeRun.Status
                              && verdict.ObservedAttempt == nodeRun.Attempt
                              && verdict.ObservedWorkSessionId == nodeRun.WorkSessionId;
                if (!matched && unjudged is null)
                {
                    continue;
                }

                // The status BEFORE the collapse is the informative one: it says whether the node-run was merely
                // admitted or actually mid-execution. Where it lands is always Pending.
                reconciled.Add(new DevWorkflowReconciledNodeRun(nodeRun.Id,
                    nodeRun.RunId,
                    nodeRun.NodeKey,
                    nodeRun.NodeType,
                    nodeRun.Status,
                    nodeRun.Attempt,
                    nodeRun.WorkSessionId,
                    nodeRun.MaxAttempts));

                // Re-dispatchable means clean: a row sitting at Pending must not carry a terminal reason, or the UI
                // reads "the engine restarted" as this attempt's outcome. The reason is on the node.interrupted event.
                nodeRun.Status = DevWorkflowNodeRunStatus.Pending;
                nodeRun.QueueReason = null;
                nodeRun.QueuedAtUtc = null;
                nodeRun.StartedAtUtc = null;
                nodeRun.FailureClass = null;
                nodeRun.TerminalReason = null;

                AddEvent(run, DevWorkflowEventTypes.NodeInterrupted, nodeRun.Id, "interrupted", operationId: null, ReasonDetail(sanitizedReason));
                run.Version++;
                run.UpdatedAtUtc = now;
                repairs.AddRange(matched
                    ? verdict!.Repairs.Select(command => (run, command))
                    : [(run, BlockUnjudged(nodeRun, unjudged!))]);
            }

            // The run comes from the row the collapse took, never from the command: the collapse is the authority on
            // which node run belongs where.
            foreach (var (run, command) in repairs)
            {
                EnsureVersion(run, command.ExpectedVersion);

                // Recovery's own admissions go through the same writer-lock count every other re-attempt does: the
                // caller decided what it could afford from a read taken before this transaction opened, and a human
                // Retry recorded since then would otherwise be spent twice. A refusal aborts the whole collapse,
                // which is exactly the "recovery that dies before it commits" contract — nothing is half-repaired and
                // the caller re-judges.
                if (command is { MaxTotalAttempts: { } budget, IncrementAttempt: true })
                {
                    await EnsureRetryBudgetAsync(run.Id, budget, cost: 1, cancellationToken).ConfigureAwait(false);
                }

                var outcome = await ApplyNodeRunTransitionAsync(run, command, cancellationToken).ConfigureAwait(false);
                _ = AddEvent(run, outcome.EventType, outcome.NodeRunId, outcome.Outcome, command.OperationId, outcome.DetailJson);
                run.Version++;
                run.UpdatedAtUtc = now;
            }

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return reconciled;
        }
        catch (DbUpdateException exception)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw new DevWorkflowConcurrencyException("A concurrent writer won the race before the interrupted node runs were reconciled.", exception);
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Where a settling pass puts a node-run it could not judge: a human's in-tray, with no attempt spent on it.</summary>
    private static TransitionDevWorkflowNodeRunCommand BlockUnjudged(DevWorkflowNodeRun nodeRun, DevWorkflowUnjudgedNodeRunBlock unjudged) =>
        new(nodeRun.RunId,
            nodeRun.Id,
            DevWorkflowVersions.Any,
            DevWorkflowNodeRunStatus.Blocked,
            PendingDecisionKind: DevWorkflowDecisionKind.Abandon,
            FailureClass: unjudged.FailureClass,
            TerminalReason: unjudged.SanitizedReason,
            WorkItemStatus: DevWorkflowWorkItemStatus.Blocked);

    /// <summary>
    ///     The node-runs a host death stranded. Only Queued and Running lost an executor: Pending was never dispatched,
    ///     and WaitingForApproval/Blocked are durable human-wait states that a restart does not invalidate. Shared by
    ///     the read and the write so the set the caller judged cannot differ from the set the collapse takes.
    /// </summary>
    private IOrderedQueryable<DevWorkflowNodeRun> StrandedNodeRuns() =>
        _dbContext.DevWorkflowNodeRuns
                  .Where(entity => entity.Status == DevWorkflowNodeRunStatus.Queued || entity.Status == DevWorkflowNodeRunStatus.Running)
                  .OrderBy(entity => entity.RunId)
                  .ThenBy(entity => entity.Sequence);

    /// <summary>
    ///     Writes a definition edit under the row's version token.
    ///     <para>
    ///         The version check above answers the common stale PUT with the numbers the caller sent. This is the other
    ///         half: two edits that each read version N both pass that check, and only the token stops the later one
    ///         from overwriting the earlier without either caller ever learning of the other. Both refusals are the
    ///         same 409, because from the client's side they are one story — somebody edited this definition first.
    ///     </para>
    /// </summary>
    private async Task SaveDefinitionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Cleared, or the definition stays tracked as Modified and the next write in this scope re-submits an edit
            // that has already been refused once.
            _dbContext.ChangeTracker.Clear();
            throw new DevWorkflowConcurrencyException("The definition was changed by another writer before this edit could be written, so its version is stale.",
                exception);
        }
    }

    private async Task<DevWorkflowDefinition> LoadDefinitionAsync(Guid definitionId, CancellationToken cancellationToken) =>
        await _dbContext.DevWorkflowDefinitions.SingleOrDefaultAsync(entity => entity.Id == definitionId, cancellationToken).ConfigureAwait(false)
        ?? throw new DevWorkflowNotFoundException($"Development workflow definition '{definitionId}' was not found.");

    private async Task<DevWorkflowWorkItemSnapshot> ComposeWorkItemAsync(DevWorkflowWorkItem workItem, CancellationToken cancellationToken)
    {
        var latest = await _dbContext.DevWorkflowRuns.AsNoTracking()
                                     .Where(run => run.WorkItemId == workItem.Id)
                                     .OrderByDescending(run => run.CreatedAtUtc)
                                     .ThenByDescending(run => run.Id)
                                     .Select(run => new LatestRunProjection(run.Id,
                                         run.Status,
                                         _dbContext.DevWorkflowDefinitions.Where(definition => definition.Id == run.DefinitionId)
                                                   .Select(definition => definition.Name)
                                                   .FirstOrDefault()))
                                     .FirstOrDefaultAsync(cancellationToken)
                                     .ConfigureAwait(false);
        if (latest is null)
        {
            return WorkItemSnapshot(workItem, latestRun: null, DevWorkflowNodeCounters.Empty);
        }

        var counters = await LoadNodeCountersAsync([latest.RunId], cancellationToken).ConfigureAwait(false);
        return WorkItemSnapshot(workItem, latest, Counters(counters, latest.RunId));
    }

    /// <summary>
    ///     One query for every listed run's node-runs, projected to four fields and tallied in memory. A grouped SQL
    ///     aggregate cannot also answer "which node-run is blocking", and the projection is small enough that pulling it
    ///     is cheaper than a second round trip to find out.
    /// </summary>
    private async Task<Dictionary<Guid, DevWorkflowNodeCounters>> LoadNodeCountersAsync(IReadOnlyList<Guid> runIds, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0)
        {
            return [];
        }

        var rows = await _dbContext.DevWorkflowNodeRuns.AsNoTracking()
                                   .Where(entity => runIds.Contains(entity.RunId))
                                   .OrderBy(entity => entity.Sequence)
                                   .Select(entity => new NodeCounterRow(entity.RunId, entity.Id, entity.Status))
                                   .ToListAsync(cancellationToken)
                                   .ConfigureAwait(false);

        return rows.GroupBy(row => row.RunId)
                   .ToDictionary(group => group.Key,
                       group => new DevWorkflowNodeCounters(group.Count(row => row.Status == DevWorkflowNodeRunStatus.Queued),
                           group.Count(row => row.Status == DevWorkflowNodeRunStatus.Running),
                           group.Count(row => row.Status == DevWorkflowNodeRunStatus.Succeeded),
                           group.Count(),
                           group.Count(row => row.Status is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked),
                           group.FirstOrDefault(row => row.Status is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked)?.NodeRunId));
    }

    private async Task<int> CountRowsAsync(IReadOnlyList<Guid> runIds, Guid workItemId, CancellationToken cancellationToken)
    {
        var removed = await _dbContext.DevWorkflowWorkItems.AsNoTracking().CountAsync(entity => entity.Id == workItemId, cancellationToken).ConfigureAwait(false);
        if (runIds.Count == 0)
        {
            return removed;
        }

        removed += runIds.Count;
        removed += await _dbContext.DevWorkflowNodeRuns.AsNoTracking().CountAsync(entity => runIds.Contains(entity.RunId), cancellationToken).ConfigureAwait(false);
        removed += await _dbContext.DevWorkflowRunEvents.AsNoTracking().CountAsync(entity => runIds.Contains(entity.RunId), cancellationToken).ConfigureAwait(false);
        removed += await _dbContext.DevWorkflowDecisions.AsNoTracking().CountAsync(entity => runIds.Contains(entity.RunId), cancellationToken).ConfigureAwait(false);
        removed += await _dbContext.DevWorkflowArtifacts.AsNoTracking().CountAsync(entity => runIds.Contains(entity.RunId), cancellationToken).ConfigureAwait(false);
        removed += await _dbContext.DevWorkflowArtifactUses.AsNoTracking().CountAsync(entity => runIds.Contains(entity.RunId), cancellationToken).ConfigureAwait(false);
        return removed;
    }

    private static DevWorkflowNodeCounters Counters(IReadOnlyDictionary<Guid, DevWorkflowNodeCounters> counters, Guid runId) =>
        counters.TryGetValue(runId, out var found) ? found : DevWorkflowNodeCounters.Empty;

    private static DevWorkflowWorkItemSnapshot WorkItemSnapshot(DevWorkflowWorkItem workItem, LatestRunProjection? latestRun, DevWorkflowNodeCounters counters) =>
        new(workItem.Id,
            workItem.Title,
            Text(workItem.Request),
            workItem.Status,
            workItem.DevelopmentProjectId,
            latestRun?.RunId,
            latestRun?.Status,
            latestRun?.DefinitionName,
            counters,
            workItem.CreatedAtUtc,
            workItem.UpdatedAtUtc,
            workItem.Version);

    private static DevWorkflowDefinitionSnapshot DefinitionSnapshot(DevWorkflowDefinition definition) =>
        new(definition.Id,
            definition.Name,
            Text(definition.GraphJson),
            definition.GraphHash,
            definition.NodeCount,
            definition.Source,
            definition.SeedSlug,
            definition.Archived,
            definition.Version,
            definition.CreatedAtUtc,
            definition.UpdatedAtUtc);

    private static DevWorkflowRunSnapshot RunSnapshot(DevWorkflowRun run) =>
        new(run.Id,
            run.WorkItemId,
            run.DefinitionId,
            run.DefinitionVersion,
            run.DefinitionGraphHash,
            Text(run.GraphJson),
            run.GraphRevision,
            run.Status,
            run.LastSequence,
            run.FailureClass,
            run.TerminalReason,
            run.StartedAtUtc,
            run.EndedAtUtc,
            run.CreatedAtUtc,
            run.UpdatedAtUtc,
            run.Version);

    private sealed record WorkItemProjection(DevWorkflowWorkItem WorkItem, LatestRunProjection? LatestRun);

    private sealed record LatestRunProjection(Guid RunId, DevWorkflowRunStatus Status, string? DefinitionName);

    private sealed record RunSummaryProjection(
        Guid Id,
        Guid WorkItemId,
        Guid DefinitionId,
        string? DefinitionName,
        DevWorkflowRunStatus Status,
        string? FailureClass,
        long? StartedAtUtc,
        long? EndedAtUtc,
        long CreatedAtUtc,
        long UpdatedAtUtc);

    private sealed record NodeCounterRow(Guid RunId, Guid NodeRunId, DevWorkflowNodeRunStatus Status);
}
