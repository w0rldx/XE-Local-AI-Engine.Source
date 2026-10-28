namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class GraphWorkflowStore
{
    public async Task<GraphWorkflowRunSnapshot> StartRunAsync(StartGraphWorkflowRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.GraphHash, nameof(command.GraphHash));
        EnsureNotBlank(command.GraphJson, nameof(command.GraphJson));
        if (command.RequestId == Guid.Empty)
        {
            throw new ArgumentException("A run start needs a caller-minted request id.", nameof(command));
        }

        if (command.NodeRuns.Select(static seed => seed.NodeKey).Distinct(StringComparer.Ordinal).Count() != command.NodeRuns.Count)
        {
            // Checked here rather than left to the unique index, which would surface as a lost race on the request id.
            throw new ArgumentException("A run start cannot create two node runs under the same node key.", nameof(command));
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // INSIDE the transaction, which is the only place the answer holds: EF opens SQLite transactions as BEGIN IMMEDIATE, so a delete of this definition
            // either sees the run row below or is refused after it. Reading out here and inserting later is what would pin a run to an already-deleted definition.
            var definition = await _dbContext.GraphWorkflowDefinitions.AsNoTracking()
                                             .SingleOrDefaultAsync(entity => entity.Id == command.DefinitionId, cancellationToken)
                             ?? throw new GraphWorkflowNotFoundException($"Graph workflow definition '{command.DefinitionId}' was not found.");
            if (definition.Version != command.DefinitionVersion || !string.Equals(definition.GraphHash, command.GraphHash, StringComparison.Ordinal))
            {
                throw new GraphWorkflowInvalidTransitionException($"Graph workflow definition '{command.DefinitionId}' changed while the run was starting "
                                                                  + $"(expected version {command.DefinitionVersion}, current {definition.Version}).");
            }

            var now = Now();
            var run = new GraphWorkflowRun
            {
                Id = command.RunId,
                RequestId = command.RequestId,
                DefinitionId = command.DefinitionId,
                DefinitionVersion = command.DefinitionVersion,
                GraphHash = command.GraphHash,
                Status = GraphWorkflowRunStatus.Pending,
                FailureClass = GraphWorkflowFailureClass.None,
                GraphJson = Utf8(command.GraphJson),
                InputJson = Utf8OrNull(command.InputJson),
                PreviousJson = Utf8OrNull(command.PreviousJson),
                ConversationId = command.ConversationId,
                TriggerMessageId = command.TriggerMessageId,
                TriggerSource = command.TriggerSource,
                PrincipalId = command.PrincipalId,
                KeyPrefix = command.KeyPrefix,
                Seq = 0,
                Version = 1,
                CreatedAtUtc = now
            };
            _dbContext.GraphWorkflowRuns.Add(run);

            // In the same transaction as the run row: a run that committed without its node runs would be a durable
            // workflow running a request nobody can reconstruct.
            foreach (var seed in command.NodeRuns)
            {
                _dbContext.GraphWorkflowNodeRuns.Add(new GraphWorkflowNodeRun
                {
                    Id = seed.NodeRunId,
                    RunId = run.Id,
                    NodeKey = seed.NodeKey,
                    Kind = seed.Kind,
                    Status = GraphWorkflowNodeRunStatus.Pending,
                    Attempt = 1,
                    FailureClass = GraphWorkflowFailureClass.None,
                    InputJson = Utf8OrNull(seed.InputJson),
                    UpdatedAtUtc = now
                });
            }

            _ = AddEvent(run, GraphWorkflowEventTypes.RunCreated, nodeKey: null, detailJson: null);
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RunSnapshot(run);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            // ux_graph_workflow_runs_request_id: another start with the same caller-minted id beat this one. The index IS the lock — the caller's earlier lookup is
            // a fast path, not a gate — so the loser answers with the run that won rather than an error the caller would have to translate back into a replay.
            await RollbackAsync(transaction);
            if (await FindRunByRequestAsync(command.RequestId, cancellationToken) is { } winner)
            {
                return winner;
            }

            // No run holds the request id, so the index that refused this insert was the other unique one on the row: the
            // conversation already has a live bound run. Told apart by elimination rather than by parsing SQLite's message.
            if (command.ConversationId is { } conversationId)
            {
                throw new GraphWorkflowRunBusyException($"Conversation '{conversationId}' already has a live graph workflow run.", exception);
            }

            throw new GraphWorkflowInvalidTransitionException($"Graph workflow run '{command.RunId}' could not be started and no run holds "
                                                              + $"request id '{command.RequestId}'.", exception);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    public async Task<GraphWorkflowRunSnapshot?> FindRunByRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.GraphWorkflowRuns.AsNoTracking()
                                  .SingleOrDefaultAsync(entity => entity.RequestId == requestId, cancellationToken);
        return run is null ? null : RunSnapshot(run);
    }

    public async Task<GraphWorkflowRunSnapshot?> FindLatestCompletedRunAsync(Guid definitionId, Guid? conversationId, CancellationToken cancellationToken = default)
    {
        var runs = _dbContext.GraphWorkflowRuns.AsNoTracking()
                             .Where(entity => entity.DefinitionId == definitionId && entity.Status == GraphWorkflowRunStatus.Completed);
        runs = conversationId is { } bound
            ? runs.Where(entity => entity.ConversationId == bound)
            : runs.Where(entity => entity.ConversationId == null && entity.TriggerMessageId == null);
        var run = await runs.OrderByDescending(entity => entity.CompletedAtUtc)
                            .ThenByDescending(entity => entity.CreatedAtUtc)
                            .FirstOrDefaultAsync(cancellationToken);
        return run is null ? null : RunSnapshot(run);
    }

    public async Task<GraphWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        RunSnapshot(await _dbContext.GraphWorkflowRuns.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken)
                    ?? throw new GraphWorkflowNotFoundException($"Graph workflow run '{runId}' was not found."));

    public async Task DeleteRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        // BEGIN IMMEDIATE: the status read and the delete see one state, and a terminal run never turns live again.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var status = await _dbContext.GraphWorkflowRuns.AsNoTracking()
                                     .Where(entity => entity.Id == runId)
                                     .Select(static entity => (GraphWorkflowRunStatus?)entity.Status)
                                     .SingleOrDefaultAsync(cancellationToken)
                     ?? throw new GraphWorkflowNotFoundException($"Graph workflow run '{runId}' was not found.");
        if (LiveRunStatuses.Contains(status))
        {
            throw new GraphWorkflowInvalidTransitionException($"Graph workflow run '{runId}' is still {status}; cancel it before deleting it.");
        }

        await GraphWorkflowPurge.DeleteRunAsync(_dbContext, runId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsAsync(GraphWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A run list limit must be positive.");
        }

        var query = _dbContext.GraphWorkflowRuns.AsNoTracking();
        if (status is { } wanted)
        {
            query = query.Where(entity => entity.Status == wanted);
        }

        var runs = await query.OrderByDescending(entity => entity.CreatedAtUtc)
                              .ThenByDescending(entity => entity.Id)
                              .Take(limit)
                              .ToListAsync(cancellationToken);
        return [.. runs.Select(RunSnapshot)];
    }

    public async Task<int> CountActiveRunsAsync(int probeLimit, CancellationToken cancellationToken = default)
    {
        if (probeLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(probeLimit), "An active-run probe limit must be positive.");
        }

        // A PARKED run — rows waiting on a person, none queued or running — releases its slot, or four waiting chats would hold
        // every slot. Cancelling always counts: it is draining work. A parked run that resumes does not re-pass admission (accepted).
        var nodeRuns = _dbContext.GraphWorkflowNodeRuns.AsNoTracking();
        return await _dbContext.GraphWorkflowRuns.AsNoTracking()
                               .Where(entity => ExecutingRunStatuses.Contains(entity.Status))
                               .Where(entity => entity.Status == GraphWorkflowRunStatus.Cancelling
                                                || !nodeRuns.Any(nodeRun => nodeRun.RunId == entity.Id && nodeRun.Status == GraphWorkflowNodeRunStatus.WaitingForApproval)
                                                || nodeRuns.Any(nodeRun => nodeRun.RunId == entity.Id
                                                                           && (nodeRun.Status == GraphWorkflowNodeRunStatus.Queued
                                                                               || nodeRun.Status == GraphWorkflowNodeRunStatus.Running)))
                               .Take(probeLimit)
                               .CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsByConversationAsync(Guid conversationId, int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A run list limit must be positive.");
        }

        var runs = await _dbContext.GraphWorkflowRuns.AsNoTracking()
                                   .Where(entity => entity.ConversationId == conversationId)
                                   .OrderByDescending(entity => entity.CreatedAtUtc)
                                   .ThenByDescending(entity => entity.Id)
                                   .Take(limit)
                                   .ToListAsync(cancellationToken);
        return [.. runs.Select(RunSnapshot)];
    }

    public Task<GraphWorkflowMutationResult> TransitionRunAsync(TransitionGraphWorkflowRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            run =>
            {
                var now = Now();
                var previousStatus = run.Status;
                var isFirstStart = run.StartedAtUtc is null;
                if (command.TargetStatus == GraphWorkflowRunStatus.Running && isFirstStart)
                {
                    run.StartedAtUtc = now;
                }

                run.Status = command.TargetStatus;
                if (command.FailureClass is { } failureClass)
                {
                    run.FailureClass = failureClass;
                }

                if (command.OutputJson is { } output)
                {
                    run.OutputJson = Utf8(output);
                }

                // Stamped HERE, off this store's clock, like every other instant on these rows: the caller that asked for the cancel has no column of its own to be
                // right about, and two clocks on one row is how a drain comes to read an intent that is older or newer than the row it sits on.
                if (command.TargetStatus == GraphWorkflowRunStatus.Cancelling)
                {
                    run.CancelRequestedAtUtc = now;
                }

                if (command.TargetStatus is GraphWorkflowRunStatus.Completed or GraphWorkflowRunStatus.Failed or GraphWorkflowRunStatus.Cancelled)
                {
                    run.CompletedAtUtc = now;
                }

                return new MutationOutcome
                {
                    EventType = EventTypeFor(previousStatus, command.TargetStatus),
                    NodeKey = null,
                    DetailJson = ReasonDetail(command.SanitizedReason)
                };
            },
            cancellationToken);
    }

    public Task<GraphWorkflowMutationResult> AppendEventAsync(AppendGraphWorkflowEventCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.EventType, nameof(command.EventType));

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            _ => new MutationOutcome
            {
                EventType = command.EventType,
                NodeKey = command.NodeKey,
                DetailJson = Utf8OrNull(command.DetailJson)
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<GraphWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long afterSeq = 0,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "An event page limit must be positive.");
        }

        // The run is read first so an unknown one answers "not found" rather than an empty page: a feed that reports a
        // missing run as a quiet one is the shape a client cannot tell apart from "nothing has happened yet".
        if (!await _dbContext.GraphWorkflowRuns.AsNoTracking().AnyAsync(entity => entity.Id == runId, cancellationToken))
        {
            throw new GraphWorkflowNotFoundException($"Graph workflow run '{runId}' was not found.");
        }

        var events = await _dbContext.GraphWorkflowRunEvents.AsNoTracking()
                                     .Where(entity => entity.RunId == runId && entity.Seq > afterSeq)
                                     .OrderBy(entity => entity.Seq)
                                     .Take(limit)
                                     .ToListAsync(cancellationToken);
        return
        [
            .. events.Select(entity => new GraphWorkflowRunEventSnapshot
            {
                Id = entity.Id,
                RunId = entity.RunId,
                Seq = entity.Seq,
                EventType = entity.EventType,
                NodeKey = entity.NodeKey,
                DetailJson = TextOrNull(entity.DetailJson),
                CreatedAtUtc = entity.CreatedAtUtc
            })
        ];
    }
}
