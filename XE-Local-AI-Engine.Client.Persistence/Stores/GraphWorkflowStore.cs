namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The Graph Workflow substrate: one file, because this store has one family — definitions, and the runs, node
///     runs and events below them.
/// </summary>
/// <remarks>
///     Every run mutation takes the run row inside one transaction, which is what makes the single <c>seq</c> counter
///     safe: two writers cannot allocate the same watermark, and neither can skip one. ponytail: that row is then the
///     lock for its whole subtree, so writes across parallel node runs of one run serialize on it. Accepted — the
///     runtime advances one run at a time behind a single gate, and SQLite runs WAL with a busy timeout. Upgrade path
///     if contention ever shows: per-node-run sequence namespaces merged on read.
/// </remarks>
public sealed class GraphWorkflowStore : IGraphWorkflowStore
{
    /// <summary>camelCase, matching every other document this product puts on a wire — these details are READ by name.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The node-run statuses a host death strands: exactly the two that had an executor behind them.</summary>
    private static readonly GraphWorkflowNodeRunStatus[] InterruptedStatuses =
    [
        GraphWorkflowNodeRunStatus.Queued,
        GraphWorkflowNodeRunStatus.Running
    ];

    /// <summary>The run statuses that still pin a definition. Anything else has finished and kept its own pinned graph.</summary>
    private static readonly GraphWorkflowRunStatus[] LiveRunStatuses =
    [
        GraphWorkflowRunStatus.Pending,
        GraphWorkflowRunStatus.Running,
        GraphWorkflowRunStatus.WaitingForApproval,
        GraphWorkflowRunStatus.Cancelling
    ];

    /// <summary>
    ///     The run statuses that occupy a concurrency slot — the ones a node is actually carrying.
    /// </summary>
    /// <remarks>
    ///     <c>Running</c> has work in flight; <c>WaitingForApproval</c> holds a run's rows and lane state open and
    ///     resumes without asking to be admitted again, so a node that stopped counting it could carry arbitrarily
    ///     many; <c>Cancelling</c> is still draining. <b><c>Pending</c> is deliberately absent</b>, which separates
    ///     this from <see cref="LiveRunStatuses" />: Pending is the queue admission draws FROM, so counting it would
    ///     count the run asking to start against its own admission and a cap of one would admit nothing at all.
    /// </remarks>
    private static readonly GraphWorkflowRunStatus[] ExecutingRunStatuses =
    [
        GraphWorkflowRunStatus.Running,
        GraphWorkflowRunStatus.WaitingForApproval,
        GraphWorkflowRunStatus.Cancelling
    ];

    private readonly NodeChatDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public GraphWorkflowStore(NodeChatDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<GraphWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default)
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
        var definition = new GraphWorkflowDefinition
        {
            Id = command.DefinitionId,
            Name = command.Name,
            Description = command.Description,
            GraphJson = graph,
            GraphHash = HashPayload(graph),
            NodeCount = command.NodeCount,
            SchemaVersion = command.SchemaVersion,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _dbContext.GraphWorkflowDefinitions.Add(definition);
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            // Cleared, or the definition stays tracked as Added and every later write in this scope trips over it.
            _dbContext.ChangeTracker.Clear();
            throw new GraphWorkflowDefinitionConflictException($"A graph workflow definition already exists for id '{command.DefinitionId}'.", exception);
        }
        catch (DbUpdateException)
        {
            // Anything else — a missing table, a read-only file, a full disk — is not "it already exists", and saying so would answer 409 to a broken node and hide
            // the real fault. Cleared for the same reason as above, then left to travel with its own stack.
            _dbContext.ChangeTracker.Clear();
            throw;
        }

        return Snapshot(definition);
    }

    public async Task<GraphWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Name is not null)
        {
            EnsureNotBlank(command.Name, nameof(command.Name));
        }

        byte[]? graph = null;
        var graphNodeCount = 0;
        if (command.GraphJson is not null)
        {
            EnsureNotBlank(command.GraphJson, nameof(command.GraphJson));

            // The node count is denormalized so the list never decrypts a blob. A new graph without one would leave the PREVIOUS graph's count beside it, and the
            // list would then report a number for a document that no longer has it — the one lie this column exists to make impossible.
            graphNodeCount = command.NodeCount
                             ?? throw new ArgumentException("A graph workflow definition edit that carries a graph must carry its node count with it.",
                                 nameof(command));
            graph = Utf8(command.GraphJson);
        }
        else if (command.NodeCount is not null)
        {
            // The mirror refusal, for the same reason: a count written WITHOUT the graph it counts leaves the stored document beside a number that is not its own,
            // and the list reports it without opening the blob that would contradict it. The count is derived, not editable, and travels with what it derives from.
            throw new ArgumentException("A graph workflow definition edit that carries a node count must carry the graph it counts with it.",
                nameof(command));
        }

        if (command.NodeCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "A definition node count cannot be negative.");
        }

        var definition = await LoadAsync(command.DefinitionId, cancellationToken);
        if (definition.Version != command.ExpectedVersion)
        {
            throw new GraphWorkflowDefinitionConflictException($"The definition version is stale (expected {command.ExpectedVersion}, current {definition.Version}).");
        }

        if (command.Name is not null)
        {
            definition.Name = command.Name;
        }

        if (command.Description is not null)
        {
            definition.Description = command.Description;
        }

        if (graph is not null)
        {
            // Hash, node count and schema version are written together with the graph, every time: that is what lets
            // the list promise never to decrypt a blob and still tell the truth about it.
            definition.GraphJson = graph;
            definition.GraphHash = HashPayload(graph);
            definition.NodeCount = graphNodeCount;

            // The schema version, unlike the node count, may be left to the stored one: this node understands exactly one version and the parser refuses every
            // other, so a graph that reached here IS it. The day a second version ships this becomes a lie, which is why it is spelled out rather than defaulted.
            definition.SchemaVersion = command.SchemaVersion ?? definition.SchemaVersion;
        }

        definition.Version++;
        definition.UpdatedAtUtc = Now();
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // The version check above answers the common stale PUT with the numbers the caller sent. This is the other half: two edits that each read version N both
            // pass that check, and only the token stops the later one from overwriting the earlier without either caller ever learning of the other.
            _dbContext.ChangeTracker.Clear();
            throw new GraphWorkflowDefinitionConflictException("The definition was changed by another writer before this edit could be written, so its version is stale.",
                exception);
        }

        return Snapshot(definition);
    }

    public async Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
        // Projected server-side without graph_json, so no definition blob is ever decrypted to draw the picker.
        await _dbContext.GraphWorkflowDefinitions.AsNoTracking()
                        .OrderBy(entity => entity.Name)
                        .ThenBy(entity => entity.Id)
                        .Select(entity => new GraphWorkflowDefinitionSummary
                        {
                            Id = entity.Id,
                            Name = entity.Name,
                            Description = entity.Description,
                            GraphHash = entity.GraphHash,
                            NodeCount = entity.NodeCount,
                            SchemaVersion = entity.SchemaVersion,
                            Version = entity.Version,
                            CreatedAtUtc = entity.CreatedAtUtc,
                            UpdatedAtUtc = entity.UpdatedAtUtc
                        })
                        .ToListAsync(cancellationToken);

    public async Task<GraphWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        var definition = await _dbContext.GraphWorkflowDefinitions.AsNoTracking()
                                         .SingleOrDefaultAsync(entity => entity.Id == definitionId, cancellationToken)
                         ?? throw new GraphWorkflowNotFoundException($"Graph workflow definition '{definitionId}' was not found.");
        return Snapshot(definition);
    }

    public async Task DeleteDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var definition = await LoadAsync(definitionId, cancellationToken);

            // Inside the transaction, which is the only place the answer can be true: EF opens SQLite transactions as BEGIN IMMEDIATE, so a writer that starts a
            // run while this delete is in flight blocks on the writer lock. It holds only while run start re-checks the definition in its own insert transaction.
            var live = await _dbContext.GraphWorkflowRuns.AsNoTracking()
                                       .AnyAsync(entity => entity.DefinitionId == definitionId && LiveRunStatuses.Contains(entity.Status), cancellationToken);
            if (live)
            {
                throw new GraphWorkflowDefinitionConflictException($"Graph workflow definition '{definitionId}' cannot be deleted while one of its runs is still live.");
            }

            _ = _dbContext.GraphWorkflowDefinitions.Remove(definition);
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

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
            return await FindRunByRequestAsync(command.RequestId, cancellationToken)
                   ?? throw new GraphWorkflowInvalidTransitionException($"Graph workflow run '{command.RunId}' could not be started and no run holds "
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

    public async Task<GraphWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        RunSnapshot(await _dbContext.GraphWorkflowRuns.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken)
                    ?? throw new GraphWorkflowNotFoundException($"Graph workflow run '{runId}' was not found."));

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

        return await _dbContext.GraphWorkflowRuns.AsNoTracking()
                               .Where(entity => ExecutingRunStatuses.Contains(entity.Status))
                               .Take(probeLimit)
                               .CountAsync(cancellationToken);
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

                return new MutationOutcome { EventType = EventTypeFor(previousStatus, command.TargetStatus), NodeKey = null, DetailJson = ReasonDetail(command.SanitizedReason) };
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<GraphWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var nodeRuns = await _dbContext.GraphWorkflowNodeRuns.AsNoTracking()
                                       .Where(entity => entity.RunId == runId)
                                       .OrderBy(entity => entity.NodeKey)
                                       .ToListAsync(cancellationToken);
        return [.. nodeRuns.Select(NodeRunSnapshot)];
    }

    public async Task<GraphWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default)
    {
        EnsureNotBlank(nodeKey, nameof(nodeKey));
        return NodeRunSnapshot(await _dbContext.GraphWorkflowNodeRuns.AsNoTracking()
                                               .SingleOrDefaultAsync(entity => entity.RunId == runId && entity.NodeKey == nodeKey, cancellationToken)
                               ?? throw new GraphWorkflowNotFoundException($"Node run '{nodeKey}' was not found on graph workflow run '{runId}'."));
    }

    public Task<GraphWorkflowMutationResult> TransitionNodeRunAsync(TransitionGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            async run => await ApplyNodeRunTransitionAsync(run, command, cancellationToken),
            cancellationToken);
    }

    public async Task<GraphWorkflowMutationResult?> DecideNodeRunAsync(DecideGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            return await TryExecuteMutationAsync(command.RunId,
                    command.ExpectedVersion,
                    async run =>
                    {
                        // Re-checked INSIDE the transaction, against the run row this mutation has already taken: a cancel committing between the caller's read and
                        // this write would land a decision on a run with no tick left to route it. Declined like a lost compare-and-set — same instruction: re-read.
                        if (run.Status is not (GraphWorkflowRunStatus.Running or GraphWorkflowRunStatus.WaitingForApproval))
                        {
                            return null;
                        }

                        // The compare-and-set, expressed as the predicate the row is LOADED by: a row that is no longer
                        // waiting, or already carries a decision, simply is not found, and the mutation writes nothing.
                        var nodeRun = await _dbContext.GraphWorkflowNodeRuns
                                                      .SingleOrDefaultAsync(entity => entity.Id == command.NodeRunId
                                                                                      && entity.RunId == run.Id
                                                                                      && entity.Status == GraphWorkflowNodeRunStatus.WaitingForApproval
                                                                                      && entity.DecisionOperationId == null,
                                                          cancellationToken);
                        if (nodeRun is null)
                        {
                            return null;
                        }

                        var now = Now();

                        // BOTH answers succeed the pause. Which way the run then goes is the edges' business, read off
                        // the decision inside the output document this write stores.
                        nodeRun.Status = GraphWorkflowNodeRunStatus.Succeeded;
                        nodeRun.PendingDecisionKind = null;
                        nodeRun.DecisionOperationId = command.OperationId;
                        nodeRun.DecidedBySubject = Utf8OrNull(command.DecidedBySubject);
                        nodeRun.OutputJson = Utf8(command.OutputJson);
                        nodeRun.CompletedAtUtc = now;
                        nodeRun.UpdatedAtUtc = now;

                        // gate.decided rather than the node.completed the status alone would derive: an answered pause
                        // is a human act, and a reader following the log has to be able to find it as one.
                        return new MutationOutcome
                        {
                            EventType = GraphWorkflowEventTypes.GateDecided,
                            NodeKey = nodeRun.NodeKey,
                            DetailJson = Utf8(JsonSerializer.Serialize(new GateDecidedDetailPayload(nodeRun.NodeKey, command.Decision.ToString()), JsonOptions))
                        };
                    },
                    cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            // The run-wide operation lookup is what normally catches a reused id; this is the same id arriving on two
            // pauses of one run CONCURRENTLY, where both passed that lookup. It is the same story to the caller.
            throw new GraphWorkflowInvalidTransitionException($"Operation '{command.OperationId}' has already decided another node run of graph workflow run "
                                                              + $"'{command.RunId}'.",
                exception);
        }
    }

    public async Task<GraphWorkflowNodeRunSnapshot?> FindNodeRunByDecisionOperationAsync(Guid runId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var nodeRun = await _dbContext.GraphWorkflowNodeRuns.AsNoTracking()
                                      .SingleOrDefaultAsync(entity => entity.RunId == runId && entity.DecisionOperationId == operationId, cancellationToken);
        return nodeRun is null ? null : NodeRunSnapshot(nodeRun);
    }

    public Task<GraphWorkflowMutationResult> AppendEventAsync(AppendGraphWorkflowEventCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.EventType, nameof(command.EventType));

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            _ => new MutationOutcome { EventType = command.EventType, NodeKey = command.NodeKey, DetailJson = Utf8OrNull(command.DetailJson) },
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

    public async Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default) =>
    [
        .. await InterruptedNodeRuns().AsNoTracking()
                                      .Select(entity => new GraphWorkflowReconciledNodeRun
                                      {
                                          NodeRunId = entity.Id,
                                          RunId = entity.RunId,
                                          NodeKey = entity.NodeKey,
                                          Kind = entity.Kind,
                                          Status = entity.Status,
                                          Attempt = entity.Attempt
                                      })
                                      .ToListAsync(cancellationToken)
    ];

    public async Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<GraphWorkflowNodeRunVerdict> verdicts,
        GraphWorkflowUnjudgedNodeRunSettlement? unjudged = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedReason);
        ArgumentNullException.ThrowIfNull(verdicts);

        // Checked by hand rather than left to ToDictionary, which would throw naming neither the node run nor what a caller did wrong: two verdicts about one row
        // are two answers to the same question, and picking one is not this store's call.
        if (verdicts.GroupBy(static verdict => verdict.NodeRunId).FirstOrDefault(static group => group.Count() > 1) is { } duplicate)
        {
            throw new ArgumentException($"Node run '{duplicate.Key}' was judged more than once in one reconcile pass.", nameof(verdicts));
        }

        // Every pass reads the world afresh. A caller that reconciles more than once holds one scope, and the identity map from its earlier pass would hand back run
        // rows as they stood BEFORE whatever moved these node runs — so this would allocate watermarks that are already taken.
        _dbContext.ChangeTracker.Clear();

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var stranded = await InterruptedNodeRuns().ToListAsync(cancellationToken);
            if (stranded.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return [];
            }

            var runIds = stranded.Select(static entity => entity.RunId).Distinct().ToList();
            var runs = await _dbContext.GraphWorkflowRuns.Where(entity => runIds.Contains(entity.Id))
                                       .ToDictionaryAsync(static entity => entity.Id, cancellationToken);
            var judged = verdicts.ToDictionary(static verdict => verdict.NodeRunId);
            var reconciled = new List<GraphWorkflowReconciledNodeRun>(stranded.Count);
            var repairs = new List<(GraphWorkflowRun Run, TransitionGraphWorkflowNodeRunCommand Command)>();
            foreach (var nodeRun in stranded)
            {
                if (!runs.TryGetValue(nodeRun.RunId, out var run))
                {
                    continue;
                }

                // A row the caller did not judge, or judged as something it no longer is, is left exactly where it is: collapsing it would strand it at Pending with
                // nobody left to decide what re-running it costs. The caller reads again next pass — or ends it with `unjudged`, decided off the row in front of us.
                var matched = judged.TryGetValue(nodeRun.Id, out var verdict)
                              && verdict.ObservedStatus == nodeRun.Status
                              && verdict.ObservedAttempt == nodeRun.Attempt;
                if (!matched && unjudged is null)
                {
                    continue;
                }

                // The status BEFORE the collapse is the informative one: it says whether the node run was merely
                // admitted or actually mid-execution. Where it lands is always Pending.
                reconciled.Add(new GraphWorkflowReconciledNodeRun { NodeRunId = nodeRun.Id, RunId = nodeRun.RunId, NodeKey = nodeRun.NodeKey, Kind = nodeRun.Kind, Status = nodeRun.Status, Attempt = nodeRun.Attempt });

                // Re-dispatchable means clean: a row sitting at Pending must not carry a terminal reason, or a reader
                // takes "the engine restarted" for this attempt's outcome. The reason is on the node.interrupted event.
                ResetToPending(nodeRun);
                _ = AddEvent(run, GraphWorkflowEventTypes.NodeInterrupted, nodeRun.NodeKey, ReasonDetail(sanitizedReason));
                run.Version++;
                repairs.AddRange(matched
                    ? verdict!.Repairs.Select(command => (run, command))
                    : [(run, SettleUnjudged(nodeRun, unjudged!))]);
            }

            foreach (var (run, command) in repairs)
            {
                EnsureVersion(run, command.ExpectedVersion);
                var outcome = await ApplyNodeRunTransitionAsync(run, command, cancellationToken);
                if (outcome.EventType is { } eventType)
                {
                    // Always, for a node-run move: only the run-level cancel settle records no event, and nothing
                    // reconciles one.
                    _ = AddEvent(run, eventType, outcome.NodeKey, outcome.DetailJson);
                }

                run.Version++;
            }

            _ = await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return reconciled;
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>One decision, one event, one transaction: what every run mutation on this store is.</summary>
    private Task<GraphWorkflowMutationResult> ExecuteMutationAsync(Guid runId,
        long expectedVersion,
        Func<GraphWorkflowRun, MutationOutcome> mutate,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(runId, expectedVersion, run => Task.FromResult(mutate(run)), cancellationToken);

    private async Task<GraphWorkflowMutationResult> ExecuteMutationAsync(Guid runId,
        long expectedVersion,
        Func<GraphWorkflowRun, Task<MutationOutcome>> mutate,
        CancellationToken cancellationToken) =>

        // Never null: this overload's mutate always decides. The nullable core is the conditional-write path.
        (await TryExecuteMutationAsync(runId, expectedVersion, async run => await mutate(run), cancellationToken))!;

    /// <summary>
    ///     The same one-decision-one-event-one-transaction shape, for a mutation that may decline to write: a
    ///     <see langword="null" /> outcome rolls the transaction back untouched and answers <see langword="null" />,
    ///     which is how a compare-and-set reports that it matched no row without raising.
    /// </summary>
    private async Task<GraphWorkflowMutationResult?> TryExecuteMutationAsync(Guid runId,
        long expectedVersion,
        Func<GraphWorkflowRun, Task<MutationOutcome?>> mutate,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var run = await _dbContext.GraphWorkflowRuns.SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken)
                      ?? throw new GraphWorkflowNotFoundException($"Graph workflow run '{runId}' was not found.");
            EnsureVersion(run, expectedVersion);

            if (await mutate(run) is not { } outcome)
            {
                await RollbackAsync(transaction);
                return null;
            }

            // The watermark is allocated per COMMIT, not per event row: a mutation that records no event — the settle from Cancelling to Cancelled is the only one —
            // still MOVED the run. The events feed pages WHERE seq > afterSeq over event ROWS, so the number this skips is simply a number no row ever carried.
            var sequence = outcome.EventType is { } eventType ? AddEvent(run, eventType, outcome.NodeKey, outcome.DetailJson) : ++run.Seq;
            run.Version++;
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new GraphWorkflowMutationResult { RunId = runId, Sequence = sequence };
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // The version check above answers the caller that read a stale number. This is the other half: two writers that each read version N both pass it, and
            // only the token stops the later one from overwriting the earlier without either learning of the other.
            await RollbackAsync(transaction);
            throw new GraphWorkflowInvalidTransitionException($"A concurrent writer moved graph workflow run '{runId}' before this write could commit.", exception);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>
    ///     One node-run status move against an already-loaded run row, without the transaction and event append.
    /// </summary>
    /// <remarks>
    ///     Factored out because restart recovery applies a batch of these inside ITS transaction, and two copies of
    ///     what a transition writes would drift.
    /// </remarks>
    private async Task<MutationOutcome> ApplyNodeRunTransitionAsync(GraphWorkflowRun run,
        TransitionGraphWorkflowNodeRunCommand command,
        CancellationToken cancellationToken)
    {
        var nodeRun = await _dbContext.GraphWorkflowNodeRuns
                                      .SingleOrDefaultAsync(entity => entity.Id == command.NodeRunId && entity.RunId == run.Id, cancellationToken)
                      ?? throw new GraphWorkflowNotFoundException($"Node run '{command.NodeRunId}' was not found on graph workflow run '{run.Id}'.");
        var now = Now();

        if (command.IncrementAttempt)
        {
            // In place: one row per node key for its whole life, with the per-attempt history in the event log.
            nodeRun.Attempt++;
        }

        nodeRun.Status = command.TargetStatus;
        nodeRun.PendingDecisionKind = command.PendingDecisionKind;

        if (command.TargetStatus == GraphWorkflowNodeRunStatus.Running)
        {
            nodeRun.StartedAtUtc = now;
        }
        else if (command.TargetStatus == GraphWorkflowNodeRunStatus.Pending)
        {
            ResetToPending(nodeRun);
        }

        if (IsTerminal(command.TargetStatus))
        {
            nodeRun.CompletedAtUtc = now;
        }

        if (command.OutputJson is { } output)
        {
            nodeRun.OutputJson = Utf8(output);
        }

        if (command.InputJson is { } input)
        {
            nodeRun.InputJson = Utf8(input);
        }

        if (command.FailureClass is { } failureClass)
        {
            nodeRun.FailureClass = failureClass;
        }

        // Why a row is queued and why it ended are the same question at different moments, and a row is only ever in
        // one of those states — so they share the row's single reason column.
        if ((command.QueueReason ?? command.TerminalReason) is { } reason)
        {
            nodeRun.Error = Utf8(reason);
        }

        if (command.InvocationId is { } invocationId)
        {
            nodeRun.InvocationId = invocationId;
        }

        nodeRun.UpdatedAtUtc = now;
        return new MutationOutcome
        {
            EventType = command.EventType ?? EventTypeFor(command.TargetStatus),
            NodeKey = nodeRun.NodeKey,
            // The caller's detail wins: a re-attempt has cleared the failure it is re-attempting because of, so the
            // reason alone would leave its event saying nothing about what it is re-attempting.
            DetailJson = command.DetailJson is { } detail ? Utf8(detail) : ReasonDetail(command.TerminalReason)
        };
    }

    /// <summary>
    ///     A re-attempt, and a restart collapse, both start from a clean slate.
    /// </summary>
    /// <remarks>
    ///     The timestamps, or a reader sees the row running since its first try; the failure fields, or it reports the
    ///     previous attempt's outcome while it runs again. What that attempt failed with is already on its own event.
    /// </remarks>
    private static void ResetToPending(GraphWorkflowNodeRun nodeRun)
    {
        nodeRun.Status = GraphWorkflowNodeRunStatus.Pending;
        nodeRun.StartedAtUtc = null;
        nodeRun.CompletedAtUtc = null;
        nodeRun.FailureClass = GraphWorkflowFailureClass.None;
        nodeRun.Error = null;
        nodeRun.PendingDecisionKind = null;
        nodeRun.InvocationId = null;
    }

    /// <summary>Where a settling pass puts a node run it could not judge: failed with the caller's class, costing no attempt.</summary>
    private static TransitionGraphWorkflowNodeRunCommand SettleUnjudged(GraphWorkflowNodeRun nodeRun, GraphWorkflowUnjudgedNodeRunSettlement unjudged) =>
        new()
        {
            RunId = nodeRun.RunId,
            NodeRunId = nodeRun.Id,
            ExpectedVersion = GraphWorkflowVersions.Any,
            TargetStatus = GraphWorkflowNodeRunStatus.Failed,
            FailureClass = unjudged.FailureClass,
            TerminalReason = unjudged.SanitizedReason
        };

    /// <summary>
    ///     The node runs a host death stranded, shared by the read and the write so the set the caller judged cannot
    ///     differ from the set the collapse takes.
    /// </summary>
    /// <remarks>
    ///     Only <c>Queued</c> and <c>Running</c> lost an executor: <c>Pending</c> was never dispatched, and
    ///     <c>WaitingForApproval</c> is a durable human wait a restart does not invalidate.
    /// </remarks>
    private IOrderedQueryable<GraphWorkflowNodeRun> InterruptedNodeRuns() =>
        _dbContext.GraphWorkflowNodeRuns.Where(entity => InterruptedStatuses.Contains(entity.Status))
                  .OrderBy(entity => entity.RunId)
                  .ThenBy(entity => entity.NodeKey);

    private long AddEvent(GraphWorkflowRun run, string eventType, string? nodeKey, byte[]? detailJson)
    {
        var sequence = ++run.Seq;
        _dbContext.GraphWorkflowRunEvents.Add(new GraphWorkflowRunEvent
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Seq = sequence,
            EventType = eventType,
            NodeKey = nodeKey,
            DetailJson = detailJson,
            CreatedAtUtc = Now()
        });
        return sequence;
    }

    private static void EnsureVersion(GraphWorkflowRun run, long expectedVersion)
    {
        if (expectedVersion == GraphWorkflowVersions.Any || run.Version == expectedVersion)
        {
            return;
        }

        throw new GraphWorkflowInvalidTransitionException($"The graph workflow run version is stale (expected {expectedVersion}, current {run.Version}).");
    }

    /// <summary>The event type a node-run status move records, so no call site has to restate it.</summary>
    private static string EventTypeFor(GraphWorkflowNodeRunStatus status) =>
        status switch
        {
            // The collapse a restart writes. A retry-in-place overrides this with node.retried, which is the one move
            // the status alone cannot tell apart from it.
            GraphWorkflowNodeRunStatus.Pending => GraphWorkflowEventTypes.NodeInterrupted,
            GraphWorkflowNodeRunStatus.Queued => GraphWorkflowEventTypes.NodeQueued,
            GraphWorkflowNodeRunStatus.Running => GraphWorkflowEventTypes.NodeStarted,
            GraphWorkflowNodeRunStatus.WaitingForApproval => GraphWorkflowEventTypes.GateRequested,
            GraphWorkflowNodeRunStatus.Succeeded => GraphWorkflowEventTypes.NodeCompleted,
            GraphWorkflowNodeRunStatus.Failed => GraphWorkflowEventTypes.NodeFailed,
            GraphWorkflowNodeRunStatus.Skipped => GraphWorkflowEventTypes.NodeSkipped,
            _ => GraphWorkflowEventTypes.NodeCancelled
        };

    /// <summary>
    ///     The event a run status move records, or <see langword="null" /> for the one move that records none.
    /// </summary>
    /// <remarks>
    ///     <c>Cancelling</c> gets the event of the thing it has BEGUN, because a reader following the log has to see
    ///     the cancel at the moment it was asked for. The settle after it is the run row's business rather than a
    ///     second event, so <c>Cancelling → Cancelled</c> writes nothing and the log carries exactly one
    ///     <c>run.cancelled</c> per cancel. A run reaching <c>Cancelled</c> from anywhere else announced none, so that
    ///     move still writes its own.
    /// </remarks>
    private static string? EventTypeFor(GraphWorkflowRunStatus previousStatus, GraphWorkflowRunStatus status) =>
        previousStatus == GraphWorkflowRunStatus.Cancelling && status == GraphWorkflowRunStatus.Cancelled
            ? null
            : EventTypeFor(status);

    private static string EventTypeFor(GraphWorkflowRunStatus status) =>
        status switch
        {
            GraphWorkflowRunStatus.Running => GraphWorkflowEventTypes.RunStarted,
            GraphWorkflowRunStatus.WaitingForApproval => GraphWorkflowEventTypes.RunWaiting,
            GraphWorkflowRunStatus.Cancelling or GraphWorkflowRunStatus.Cancelled => GraphWorkflowEventTypes.RunCancelled,
            GraphWorkflowRunStatus.Completed => GraphWorkflowEventTypes.RunCompleted,
            GraphWorkflowRunStatus.Failed => GraphWorkflowEventTypes.RunFailed,

            // Pending: a run is created Pending and never moves back to it, so nothing reaches here today.
            _ => GraphWorkflowEventTypes.RunCreated
        };

    /// <summary>
    ///     A node-run status nothing further will happen to on its own. The Application layer's state machine is the
    ///     authority on what a legal MOVE is; this is only what stamps a completion instant, which the database can see
    ///     for itself.
    /// </summary>
    private static bool IsTerminal(GraphWorkflowNodeRunStatus status) =>
        status is GraphWorkflowNodeRunStatus.Succeeded
            or GraphWorkflowNodeRunStatus.Failed
            or GraphWorkflowNodeRunStatus.Skipped
            or GraphWorkflowNodeRunStatus.Cancelled;

    private static byte[]? ReasonDetail(string? sanitizedReason) =>
        string.IsNullOrWhiteSpace(sanitizedReason) ? null : Utf8(JsonSerializer.Serialize(new ReasonDetailPayload(sanitizedReason), JsonOptions));

    private static GraphWorkflowRunSnapshot RunSnapshot(GraphWorkflowRun run) =>
        new()
        {
            Id = run.Id,
            RequestId = run.RequestId,
            DefinitionId = run.DefinitionId,
            DefinitionVersion = run.DefinitionVersion,
            GraphHash = run.GraphHash,
            Status = run.Status,
            FailureClass = run.FailureClass,
            GraphJson = Text(run.GraphJson),
            InputJson = TextOrNull(run.InputJson),
            OutputJson = TextOrNull(run.OutputJson),
            Seq = run.Seq,
            Version = run.Version,
            CancelRequestedAtUtc = run.CancelRequestedAtUtc,
            StartedAtUtc = run.StartedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            CreatedAtUtc = run.CreatedAtUtc
        };

    private static GraphWorkflowNodeRunSnapshot NodeRunSnapshot(GraphWorkflowNodeRun nodeRun) =>
        new()
        {
            Id = nodeRun.Id,
            RunId = nodeRun.RunId,
            NodeKey = nodeRun.NodeKey,
            Kind = nodeRun.Kind,
            Status = nodeRun.Status,
            Attempt = nodeRun.Attempt,
            PendingDecisionKind = nodeRun.PendingDecisionKind,
            DecisionOperationId = nodeRun.DecisionOperationId,
            DecidedBySubject = TextOrNull(nodeRun.DecidedBySubject),
            FailureClass = nodeRun.FailureClass,
            Error = TextOrNull(nodeRun.Error),
            InputJson = TextOrNull(nodeRun.InputJson),
            OutputJson = TextOrNull(nodeRun.OutputJson),
            InvocationId = nodeRun.InvocationId,
            StartedAtUtc = nodeRun.StartedAtUtc,
            CompletedAtUtc = nodeRun.CompletedAtUtc,
            UpdatedAtUtc = nodeRun.UpdatedAtUtc
        };

    private async Task RollbackAsync(IDbContextTransaction transaction)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
    }

    private static byte[]? Utf8OrNull(string? value) =>
        value is null ? null : Encoding.UTF8.GetBytes(value);

    private static string? TextOrNull(byte[]? value) =>
        value is null ? null : Encoding.UTF8.GetString(value);

    /// <summary>
    ///     What one mutation decided. A null <see cref="EventType" /> is the deliberate "no event row": the move
    ///     happened, the run version bumps, and the watermark stays where the last event left it.
    /// </summary>
    private sealed record MutationOutcome
    {
        public required string? EventType { get; init; }

        public required string? NodeKey { get; init; }

        public required byte[]? DetailJson { get; init; }
    }

    private sealed record ReasonDetailPayload(string Reason);

    /// <summary>The gate.decided detail: which pause, and what was answered. Read by name, so camelCase like every other.</summary>
    private sealed record GateDecidedDetailPayload(string NodeKey, string Decision);

    private async Task<GraphWorkflowDefinition> LoadAsync(Guid definitionId, CancellationToken cancellationToken) =>
        await _dbContext.GraphWorkflowDefinitions.SingleOrDefaultAsync(entity => entity.Id == definitionId, cancellationToken)
        ?? throw new GraphWorkflowNotFoundException($"Graph workflow definition '{definitionId}' was not found.");

    private static GraphWorkflowDefinitionSnapshot Snapshot(GraphWorkflowDefinition definition) =>
        new()
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = definition.Description,
            GraphJson = Text(definition.GraphJson),
            GraphHash = definition.GraphHash,
            NodeCount = definition.NodeCount,
            SchemaVersion = definition.SchemaVersion,
            Version = definition.Version,
            CreatedAtUtc = definition.CreatedAtUtc,
            UpdatedAtUtc = definition.UpdatedAtUtc
        };

    /// <summary>
    ///     SHA-256 of the graph's bytes, lowercase hex, computed here — beside the blob it describes — so a hash and
    ///     the document it names can never drift apart.
    /// </summary>
    private static string HashPayload(byte[] payload) =>
        Convert.ToHexStringLower(SHA256.HashData(payload));

    /// <summary>
    ///     Whether a failed write was the unique index refusing a duplicate. SQLite reports both PRIMARY KEY (1555) and
    ///     UNIQUE (2067) as extended codes of the generic constraint error, and only those two mean "already exists".
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 };

    private static void EnsureNotBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be null, empty, or whitespace.", parameterName);
        }
    }

    private long Now() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static byte[] Utf8(string value) =>
        Encoding.UTF8.GetBytes(value);

    private static string Text(byte[] value) =>
        Encoding.UTF8.GetString(value);
}
