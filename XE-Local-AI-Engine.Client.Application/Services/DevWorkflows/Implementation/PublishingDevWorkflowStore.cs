namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Announces every committed workflow mutation, and forwards everything else untouched.</summary>
/// <remarks>
///     The publish sits HERE rather than at each call site because a missed one is a pane that silently stops
///     updating, with no test that would notice. The change kind comes from the COMMAND, not the event row.
///     ponytail: one ping per committed mutation, no coalescing window; a parallel stage would want a debounce here
///     keyed by run id. See docs/wiki/25-dev-workflows.md ("Node telemetry").
/// </remarks>
internal sealed class PublishingDevWorkflowStore : IDevWorkflowStore
{
    /// <summary>How long a cost collection may take before the settle goes ahead without it.</summary>
    /// <remarks>
    ///     A HARD wall-clock bound on the WAIT, not a request to stop, and it bounds the whole ask rather than one
    ///     command; a SPENT deadline schedules nothing. The parameter exists so a test can prove that without waiting
    ///     the real seconds; production takes the default.
    ///     See docs/wiki/25-dev-workflows.md ("Node telemetry").
    /// </remarks>
    private static readonly TimeSpan DefaultCollectionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The telemetry members that are NOT additive across attempts.</summary>
    /// <remarks>
    ///     A route belongs to one settle, a served model is a name rather than a quantity, tool names do not sum, and
    ///     the VRAM figures are a READING of the box at one load. The retry snapshot carries everything else.
    ///     See docs/wiki/25-dev-workflows.md ("Node telemetry").
    /// </remarks>
    private static readonly HashSet<string> NonAdditiveTelemetryMembers = new(StringComparer.Ordinal)
    {
        "routeJson",
        "servedModelName",
        "toolNamesJson",
        "vramFreeAtLoadBytes",
        "vramAdmittedBytes"
    };

    private readonly IDevWorkflowStore _inner;
    private readonly IDevWorkflowEventPublisher _publisher;
    private readonly IServiceScopeFactory _scopes;
    private readonly DevWorkflowGraphCache _graphs;
    private readonly ILogger<PublishingDevWorkflowStore> _logger;
    private readonly TimeSpan _collectionTimeout;
    private readonly DevWorkflowNodeTelemetryCollectionPool _collections;

    public PublishingDevWorkflowStore(
        IDevWorkflowStore inner,
        IDevWorkflowEventPublisher publisher,
        IServiceScopeFactory scopes,
        DevWorkflowGraphCache graphs,
        DevWorkflowNodeTelemetryCollectionPool collections,
        ILogger<PublishingDevWorkflowStore> logger,
        TimeSpan? collectionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
        ArgumentNullException.ThrowIfNull(graphs);
        _graphs = graphs;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _collectionTimeout = collectionTimeout ?? DefaultCollectionTimeout;
        ArgumentNullException.ThrowIfNull(collections);
        _collections = collections;
    }

    public Task<DevWorkflowWorkItemSnapshot> CreateWorkItemAsync(CreateDevWorkflowWorkItemCommand command, CancellationToken cancellationToken = default) =>
        _inner.CreateWorkItemAsync(command, cancellationToken);

    public Task<DevWorkflowWorkItemSnapshot> UpdateWorkItemAsync(UpdateDevWorkflowWorkItemCommand command, CancellationToken cancellationToken = default) =>
        _inner.UpdateWorkItemAsync(command, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowWorkItemSnapshot>> ListWorkItemsAsync(DevWorkflowWorkItemStatus? status = null,
        CancellationToken cancellationToken = default) =>
        _inner.ListWorkItemsAsync(status, cancellationToken);

    public Task<DevWorkflowWorkItemSnapshot> GetWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default) =>
        _inner.GetWorkItemAsync(workItemId, cancellationToken);

    public Task<DevWorkflowWorkItemDeletion> DeleteWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default) =>
        _inner.DeleteWorkItemAsync(workItemId, cancellationToken);

    public Task<DevWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateDevWorkflowDefinitionCommand command, CancellationToken cancellationToken = default) =>
        _inner.CreateDefinitionAsync(command, cancellationToken);

    public Task<DevWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateDevWorkflowDefinitionCommand command, CancellationToken cancellationToken = default) =>
        _inner.UpdateDefinitionAsync(command, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowDefinitionSummary>> ListDefinitionsAsync(bool includeArchived = false, CancellationToken cancellationToken = default) =>
        _inner.ListDefinitionsAsync(includeArchived, cancellationToken);

    public Task<DevWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _inner.GetDefinitionAsync(definitionId, cancellationToken);

    public Task<DevWorkflowDefinitionSnapshot> ArchiveDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _inner.ArchiveDefinitionAsync(definitionId, cancellationToken);

    // Rule sets are forwarded unwrapped, like the definitions above them: no run's pane renders one, and the runtime
    // consumes a rule set only at the NEXT materialization — whose own node-run write publishes a Run-kind ping.
    public Task<DevWorkflowRuleSetSnapshot> CreateRuleSetAsync(CreateDevWorkflowRuleSetCommand command, CancellationToken cancellationToken = default) =>
        _inner.CreateRuleSetAsync(command, cancellationToken);

    public Task<DevWorkflowRuleSetSnapshot> UpdateRuleSetAsync(UpdateDevWorkflowRuleSetCommand command, CancellationToken cancellationToken = default) =>
        _inner.UpdateRuleSetAsync(command, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowRuleSetSummary>> ListRuleSetsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListRuleSetsAsync(cancellationToken);

    public Task<DevWorkflowRuleSetSnapshot> GetRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken = default) =>
        _inner.GetRuleSetAsync(ruleSetId, cancellationToken);

    public Task DeleteRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken = default) =>
        _inner.DeleteRuleSetAsync(ruleSetId, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowRuleSetSnapshot>> ListEnabledRuleSetsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListEnabledRuleSetsAsync(cancellationToken);

    /// <summary>Nothing is subscribed to a run that does not exist yet, so a start publishes nothing.</summary>
    public Task<DevWorkflowRunSnapshot> StartRunAsync(StartDevWorkflowRunCommand command, CancellationToken cancellationToken = default) =>
        _inner.StartRunAsync(command, cancellationToken);

    public Task<DevWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _inner.GetRunAsync(runId, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowRunSnapshot>> ListRunsAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        _inner.ListRunsAsync(workItemId, status, limit, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowRunSummary>> ListRunSummariesAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        _inner.ListRunSummariesAsync(workItemId, status, limit, cancellationToken);

    public Task<DevWorkflowMutationResult> TransitionRunAsync(TransitionDevWorkflowRunCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.TransitionRunAsync(command, cancellationToken), DevWorkflowChangeKind.Run, cancellationToken);

    /// <summary>
    ///     Startup recovery, before any client can be watching: the run rows it touches are re-read whole by whoever
    ///     subscribes afterwards.
    /// </summary>
    public Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<DevWorkflowNodeRunVerdict> verdicts,
        DevWorkflowUnjudgedNodeRunBlock? unjudged = null,
        CancellationToken cancellationToken = default) =>
        _inner.ReconcileNonTerminalNodeRunsAsync(sanitizedReason, verdicts, unjudged, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListInterruptedNodeRunsAsync(cancellationToken);

    public Task<DevWorkflowMutationResult> MaterializeNodeRunsAsync(MaterializeDevWorkflowNodesCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.MaterializeNodeRunsAsync(command, cancellationToken), DevWorkflowChangeKind.Node, cancellationToken);

    public async Task<DevWorkflowMutationResult> TransitionNodeRunAsync(TransitionDevWorkflowNodeRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A node run entering a human wait is the one status move a client does more than repaint for.
        var kind = command.TargetStatus is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked
            ? DevWorkflowChangeKind.Gate
            : DevWorkflowChangeKind.Node;
        var enriched = await EnrichAsync(command, cancellationToken);
        return await PublishAsync(_inner.TransitionNodeRunAsync(enriched, cancellationToken), kind, cancellationToken);
    }

    /// <summary>ONE announcement for the whole route, because it is one commit.</summary>
    /// <remarks>
    ///     Its watermark names the routing event and every reset the same transaction wrote sits after it. Each reset
    ///     is enriched exactly as a same-node re-attempt is, re-derived on every ask and never cached.
    ///     See docs/wiki/25-dev-workflows.md ("Node telemetry").
    /// </remarks>
    public async Task<DevWorkflowMutationResult> RouteRetryAsync(RouteDevWorkflowRetryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // ONE deadline for every reset, not one each: `Resets` is built from the retry target's descendants and is
        // bounded only by the graph's width, so a per-reset budget would hold the dispatcher tick for N times it.
        using var deadline = NewCollectionDeadline(cancellationToken);
        var resets = new List<TransitionDevWorkflowNodeRunCommand>(command.Resets.Count);
        foreach (var reset in command.Resets)
        {
            resets.Add(await EnrichWithinDeadlineAsync(reset, deadline.Token, cancellationToken));
        }

        return await PublishAsync(_inner.RouteRetryAsync(command with
            {
                Resets = resets
            }, cancellationToken), DevWorkflowChangeKind.Node, cancellationToken);
    }

    public Task<DevWorkflowMutationResult> AttachWorkSessionAsync(AttachDevWorkflowWorkSessionCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.AttachWorkSessionAsync(command, cancellationToken), DevWorkflowChangeKind.Node, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _inner.ListNodeRunsAsync(runId, cancellationToken);

    public Task<DevWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid nodeRunId, CancellationToken cancellationToken = default) =>
        _inner.GetNodeRunAsync(nodeRunId, cancellationToken);

    public Task<Guid?> FindRunIdForDevelopmentTaskAsync(Guid developmentTaskId, CancellationToken cancellationToken = default) =>
        _inner.FindRunIdForDevelopmentTaskAsync(developmentTaskId, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, Guid>> FindRunIdsForDevelopmentTasksAsync(IReadOnlyList<Guid> developmentTaskIds,
        CancellationToken cancellationToken = default) =>
        _inner.FindRunIdsForDevelopmentTasksAsync(developmentTaskIds, cancellationToken);

    public Task<DevWorkflowMutationResult> AppendArtifactAsync(AppendDevWorkflowArtifactCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.AppendArtifactAsync(command, cancellationToken), DevWorkflowChangeKind.Artifact, cancellationToken);

    public Task<DevWorkflowMutationResult> RecordArtifactUsesAsync(RecordDevWorkflowArtifactUsesCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.RecordArtifactUsesAsync(command, cancellationToken), DevWorkflowChangeKind.Artifact, cancellationToken);

    public Task<DevWorkflowMutationResult> MarkDependentsStaleAsync(MarkDevWorkflowStaleCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.MarkDependentsStaleAsync(command, cancellationToken), DevWorkflowChangeKind.Artifact, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> ListArtifactsAsync(Guid runId, long sinceSequence = 0, CancellationToken cancellationToken = default) =>
        _inner.ListArtifactsAsync(runId, sinceSequence, cancellationToken);

    public Task<DevWorkflowArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default) =>
        _inner.GetArtifactAsync(artifactId, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListConsumedArtifactIdsAsync(Guid nodeRunId, CancellationToken cancellationToken = default) =>
        _inner.ListConsumedArtifactIdsAsync(nodeRunId, cancellationToken);

    public Task<DevWorkflowMutationResult> RecordDecisionAsync(RecordDevWorkflowDecisionCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.RecordDecisionAsync(command, cancellationToken), DevWorkflowChangeKind.Gate, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowDecisionSnapshot>> ListDecisionsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _inner.ListDecisionsAsync(runId, cancellationToken);

    public Task<DevWorkflowDecisionSnapshot?> FindDecisionByOperationAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default) =>
        _inner.FindDecisionByOperationAsync(runId, operationId, cancellationToken);

    /// <summary>A read: nothing committed, so there is nothing to announce.</summary>
    public Task<string?> FindOperationEventTypeAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default) =>
        _inner.FindOperationEventTypeAsync(runId, operationId, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListOwnedWorkSessionIdsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListOwnedWorkSessionIdsAsync(cancellationToken);

    public Task<DevWorkflowMutationResult> AppendEventAsync(AppendDevWorkflowEventCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.AppendEventAsync(command, cancellationToken), DevWorkflowChangeKind.Run, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long sinceSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default) =>
        _inner.ListEventsAsync(runId, sinceSequence, limit, cancellationToken);

    /// <summary>Attaches what the settling attempt cost, or forwards the command untouched.</summary>
    /// <remarks>
    ///     The gate is the target STATUS, not the caller, so a call site added later crosses this method whether or
    ///     not anyone remembers it. The whole enrichment is contained: any throw, any timeout, and the ORIGINAL
    ///     command goes through. See docs/wiki/25-dev-workflows.md ("Node telemetry").
    /// </remarks>
    private async Task<TransitionDevWorkflowNodeRunCommand> EnrichAsync(TransitionDevWorkflowNodeRunCommand command, CancellationToken cancellationToken)
    {
        // The deadline is opened only for a command that will actually collect, so an ordinary Running transition
        // pays for no timer at all.
        if (!IsReAttempt(command) && !WritesTelemetry(command.TargetStatus))
        {
            return command;
        }

        using var deadline = NewCollectionDeadline(cancellationToken);
        return await EnrichWithinDeadlineAsync(command, deadline.Token, cancellationToken);
    }

    /// <summary>The enrichment itself, under a deadline its CALLER owns — one per settle, one per retry route.</summary>
    /// <remarks>
    ///     Enforced by ABANDONING THE WAIT, not by asking the collection to stop; <c>WaitAsync</c> gives the thread
    ///     back and leaves the collection to finish into <see cref="ObserveLateCollection" />.
    ///     <paramref name="cancellationToken" /> is kept only to tell an expired deadline (swallowed) from a
    ///     cancelled caller (rethrown). See docs/wiki/25-dev-workflows.md ("Node telemetry").
    /// </remarks>
    private async Task<TransitionDevWorkflowNodeRunCommand> EnrichWithinDeadlineAsync(TransitionDevWorkflowNodeRunCommand command,
        CancellationToken deadline,
        CancellationToken cancellationToken)
    {
        if (!IsReAttempt(command) && !WritesTelemetry(command.TargetStatus))
        {
            return command;
        }

        // A cancelled CALLER is still a cancelled caller, and is told so rather than quietly settling unenriched.
        cancellationToken.ThrowIfCancellationRequested();

        // A SPENT budget schedules nothing. The deadline is shared across a retry route, so once it has expired every
        // remaining reset would start a collection whose answer is already too late to be used.
        if (deadline.IsCancellationRequested)
        {
            _logger.LogDebug("The cost-collection budget was spent before node run {NodeRunId} was reached; it is forwarded without a measurement.",
                command.NodeRunId);
            return command;
        }

        // Admission BEFORE scheduling, because the deadline bounds the wait and not the work behind it. No slot free
        // means no collection at all — the same trade an expired deadline makes.
        if (!_collections.TryEnter())
        {
            _logger.LogWarning("All {CollectionSlots} cost-collection slots are in use; node run {NodeRunId} is forwarded without a measurement.",
                _collections.Slots,
                command.NodeRunId);
            return command;
        }

        // Task.Run, so the boundary holds even against a collector that blocks BEFORE its first await. The cost is
        // that a reset is offered to the collector EVENTUALLY rather than synchronously.
        var startedAt = Stopwatch.GetTimestamp();
        var collection = Task.Run(async () =>
            {
                try
                {
                    return await CollectAsync(command, deadline, cancellationToken);
                }
                finally
                {
                    // The slot comes back when the COLLECTOR terminates, not when the caller stops waiting: releasing
                    // it on the abandoned wait would let the next settle start work beside the stuck one.
                    _collections.Release();
                }
            },
            CancellationToken.None);

        try
        {
            return await collection.WaitAsync(deadline);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ObserveLateCollection(collection, command.NodeRunId, startedAt);
            return command;
        }
    }

    /// <summary>The reads a cost collection needs, on a service scope the COLLECTION owns and disposes.</summary>
    /// <remarks>
    ///     The isolation is the point: this work can outlive the settle that started it, whose next act is to write
    ///     through <c>_inner</c>, and a second concurrent operation on that <c>DbContext</c> is a hard failure rather
    ///     than a lost measurement. Everything here READS; the only write is the enriched command it returns.
    ///     See docs/wiki/25-dev-workflows.md ("Node telemetry").
    /// </remarks>
    private async Task<TransitionDevWorkflowNodeRunCommand> CollectAsync(TransitionDevWorkflowNodeRunCommand command,
        CancellationToken deadline,
        CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        try
        {
            var reads = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
            var telemetry = scope.ServiceProvider.GetRequiredService<IDevWorkflowNodeTelemetrySource>();

            return IsReAttempt(command)
                ? await EnrichReAttemptAsync(command, reads, telemetry, deadline)
                : await EnrichSettleAsync(command, reads, telemetry, deadline);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception,
                "Cost telemetry could not be collected for node run {NodeRunId}; it settles without it.",
                command.NodeRunId);
            return command;
        }
    }

    /// <summary>
    ///     A settle's own cost and the route it took, read on the collection's isolated scope.
    /// </summary>
    private async Task<TransitionDevWorkflowNodeRunCommand> EnrichSettleAsync(TransitionDevWorkflowNodeRunCommand command,
        IDevWorkflowStore reads,
        IDevWorkflowNodeTelemetrySource telemetry,
        CancellationToken deadline)
    {
        // The PRE-write row, read for four things only: the work session, the development task, the attempt's start
        // and the node type. Its Status and OutputJson are the previous attempt's.
        var snapshot = await reads.GetNodeRunAsync(command.NodeRunId, deadline);
        var routeJson = await RouteJsonAsync(command, snapshot, reads, deadline);
        var collected = await telemetry.CollectAsync(snapshot, command.TargetStatus, deadline);

        if (collected is null && routeJson is null)
        {
            return command;
        }

        return command with
        {
            Telemetry = (collected ?? new DevWorkflowNodeTelemetry()) with
            {
                RouteJson = routeJson
            }
        };
    }

    /// <summary>Watches a collection that outlived its deadline, so its completion is observed.</summary>
    /// <remarks>
    ///     Rather than left to the unobserved-exception handler, and it says so once, at warning, with counts only.
    ///     Whatever it answers is DROPPED: the transition it would have enriched has already gone through.
    /// </remarks>
    private void ObserveLateCollection(Task<TransitionDevWorkflowNodeRunCommand> collection, Guid nodeRunId, long startedAt)
    {
        _ = collection.ContinueWith(task =>
            {
                var lateBy = Stopwatch.GetElapsedTime(startedAt) - _collectionTimeout;
                if (task.IsFaulted)
                {
                    _logger.LogWarning(task.Exception,
                        "A cost collection for node run {NodeRunId} outlived its {BudgetMs} ms budget and then failed {LateByMs} ms in; nothing was written.",
                        nodeRunId,
                        _collectionTimeout.TotalMilliseconds,
                        lateBy.TotalMilliseconds);
                    return;
                }

                _logger.LogWarning(
                    "A cost collection for node run {NodeRunId} outlived its {BudgetMs} ms budget and finished {LateByMs} ms late; the result was discarded and the node run settled without it.",
                    nodeRunId,
                    _collectionTimeout.TotalMilliseconds,
                    lateBy.TotalMilliseconds);
            },
            CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    /// <summary>The route this settle took, or null when the move is not terminal.</summary>
    /// <remarks>
    ///     A <c>Blocked</c> node run or one waiting on a human has routed nowhere yet, and a null says so where an
    ///     empty document would not. The command's target status and output are projected onto the pre-write row
    ///     FIRST, or every edge would answer <c>Pending</c>.
    ///     See docs/wiki/25-dev-workflows.md ("Node telemetry").
    /// </remarks>
    private async Task<string?> RouteJsonAsync(TransitionDevWorkflowNodeRunCommand command,
        DevWorkflowNodeRunSnapshot snapshot,
        IDevWorkflowStore reads,
        CancellationToken cancellationToken)
    {
        if (!DevWorkflowStateMachine.IsTerminal(command.TargetStatus))
        {
            return null;
        }

        var routeSource = snapshot with
        {
            Status = command.TargetStatus,
            OutputJson = command.OutputJson ?? snapshot.OutputJson
        };

        var run = await reads.GetRunAsync(command.RunId, cancellationToken);
        var decision = routeSource.NodeType == DevWorkflowNodeType.HumanGate
            ? DevWorkflowStateMachine.GateDecisionFrom(routeSource.OutputJson)
            : null;

        // The run's other rows, because whether a SKIP was waived is a walk back over the graph rather than something
        // this row carries. Without them a waived skip's out-edges would record as dead.
        var nodeRuns = await reads.ListNodeRunsAsync(command.RunId, cancellationToken);
        var nodeRunsByKey = nodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);

        return DevWorkflowStateMachine.RouteJson(DevWorkflowStateMachine.RouteTaken(_graphs.Resolve(run), routeSource, nodeRunsByKey, decision));
    }

    /// <summary>The failing attempt's cost, captured onto the retry event BEFORE the reset that empties the row.</summary>
    /// <remarks>
    ///     The node-run row keeps the LAST attempt only. The pre-write row is also the last place the failing
    ///     attempt's work session still exists: the command clears it downstream, inside the store's own transition.
    ///     See docs/wiki/25-dev-workflows.md ("Node telemetry").
    /// </remarks>
    private static async Task<TransitionDevWorkflowNodeRunCommand> EnrichReAttemptAsync(TransitionDevWorkflowNodeRunCommand command,
        IDevWorkflowStore reads,
        IDevWorkflowNodeTelemetrySource telemetry,
        CancellationToken deadline)
    {
        var snapshot = await reads.GetNodeRunAsync(command.NodeRunId, deadline);

        // Collected as the FAILED attempt it is. The row itself may still read Running — a re-attempt is written
        // straight over a live row — but what this cost vector describes is an attempt that is over.
        var collected = await telemetry.CollectAsync(snapshot, DevWorkflowNodeRunStatus.Failed, deadline);
        if (collected is null)
        {
            return command;
        }

        return MergeAttemptCost(command.DetailJson!, collected) is { } merged
            ? command with
            {
                DetailJson = merged
            }
            : command;
    }

    /// <summary>Merges the COMPLETE additive cost vector into an existing retry detail, or answers null.</summary>
    /// <remarks>
    ///     Null when the payload is not a JSON object and must be forwarded verbatim. The members come from the
    ///     telemetry record itself minus the non-additive ones, so a column added to that record later rides here
    ///     automatically, or it is not additive; nothing enumerates them by hand.
    /// </remarks>
    private static string? MergeAttemptCost(string detailJson, DevWorkflowNodeTelemetry telemetry)
    {
        if (JsonNode.Parse(detailJson) is not JsonObject detail || JsonSerializer.SerializeToNode(telemetry, JsonOptions) is not JsonObject cost)
        {
            return null;
        }

        foreach (var member in cost.Where(member => !NonAdditiveTelemetryMembers.Contains(member.Key)))
        {
            detail[member.Key] = member.Value?.DeepClone();
        }

        return detail.ToJsonString(JsonOptions);
    }

    /// <summary>
    ///     The re-attempt write, recognised from the command alone — target <c>Pending</c>, the attempt incremented, a
    ///     detail to merge into. Both re-attempt write paths build exactly this shape from the same composer, which is
    ///     why one predicate covers them.
    /// </summary>
    private static bool IsReAttempt(TransitionDevWorkflowNodeRunCommand command) =>
        command.TargetStatus == DevWorkflowNodeRunStatus.Pending && command.IncrementAttempt && command.DetailJson is not null;

    /// <summary>One collection budget, linked to the caller's own token so a cancelled request is still a cancelled request.</summary>
    private CancellationTokenSource NewCollectionDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_collectionTimeout);
        return deadline;
    }

    /// <summary>Where an attempt's spend stops changing: it has settled, been abandoned, or stopped to ask a human.</summary>
    private static bool WritesTelemetry(DevWorkflowNodeRunStatus status) =>
        DevWorkflowStateMachine.IsTerminal(status)
        || status is DevWorkflowNodeRunStatus.Blocked or DevWorkflowNodeRunStatus.WaitingForApproval;

    private async Task<DevWorkflowMutationResult> PublishAsync(Task<DevWorkflowMutationResult> mutation,
        DevWorkflowChangeKind kind,
        CancellationToken cancellationToken)
    {
        var result = await mutation;
        await _publisher.PublishAsync(result.RunId, result.Sequence, kind, cancellationToken);
        return result;
    }
}
