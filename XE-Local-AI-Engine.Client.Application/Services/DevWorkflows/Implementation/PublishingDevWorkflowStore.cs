namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Announces every committed workflow mutation, and forwards everything else untouched.
///     <para>
///         The publish sits HERE rather than at each of the fifteen call sites in the runtime for one reason: a missed
///         call site is a pane that silently stops updating, and there is no test that would notice. Every mutation
///         returns the watermark its commit allocated, so wrapping the one interface they all go through makes the
///         notification impossible to forget — including from code written later.
///     </para>
///     <para>
///         The change kind comes from the COMMAND, not from the event row: a caller that transitions a node run into a
///         human wait is asking for a person, and that is the one push with a consequence beyond re-rendering.
///     </para>
/// </summary>
/// <remarks>
///     ponytail: one ping per committed mutation, with no coalescing window. Slice A's graphs are linear, so a tick
///     writes one or two; a parallel stage would want a debounce here, keyed by run id, before the client turns each
///     ping into a refetch.
/// </remarks>
internal sealed class PublishingDevWorkflowStore(IDevWorkflowStore inner,
    IDevWorkflowEventPublisher publisher,
    IServiceScopeFactory scopes,
    DevWorkflowGraphCache graphs,
    DevWorkflowNodeTelemetryCollectionPool collections,
    ILogger<PublishingDevWorkflowStore> logger,
    TimeSpan? collectionTimeout = null) : IDevWorkflowStore
{
    /// <summary>
    ///     How long a cost collection may take before the settle goes ahead without it. It runs on a dispatcher tick,
    ///     and a measurement that delays a run is worse than a measurement that is missing.
    ///     <para>
    ///         It is a HARD wall-clock bound, not a request to stop: the settle stops WAITING when it expires, whether
    ///         or not the collection notices. A collector that ignores its token, or a database call that never
    ///         returns, therefore costs a measurement and nothing else — see <c>CollectAsync</c> for why the abandoned
    ///         work cannot touch the mutation's own <c>DbContext</c>.
    ///     </para>
    ///     <para>
    ///         It bounds the whole ASK, not one command: a retry route enriches every reset it carries under ONE
    ///         deadline, because the graph's width is what decides how many resets there are and a per-command budget
    ///         would multiply by it. The parameter exists so a test can prove that without waiting five real seconds;
    ///         production takes the default.
    ///     </para>
    ///     <para>
    ///         A SPENT deadline schedules nothing. Once the shared budget has expired, a route's remaining resets are
    ///         forwarded unenriched without a collection being started at all: the answer would already be too late to
    ///         use, so starting it would only pile up work behind a boundary the caller has stopped watching.
    ///     </para>
    /// </summary>
    private static readonly TimeSpan DefaultCollectionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The telemetry members that are NOT additive across attempts. A route belongs to one settle, a served model
    ///     is a name rather than a quantity, and a set of tool names does not sum — so the retry snapshot carries
    ///     everything else and only these three are dropped.
    /// </summary>
    private static readonly HashSet<string> NonAdditiveTelemetryMembers = new(StringComparer.Ordinal)
    {
        "routeJson",
        "servedModelName",
        "toolNamesJson"
    };

    private readonly IDevWorkflowStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IDevWorkflowEventPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    private readonly DevWorkflowGraphCache _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
    private readonly ILogger<PublishingDevWorkflowStore> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeSpan _collectionTimeout = collectionTimeout ?? DefaultCollectionTimeout;
    private readonly DevWorkflowNodeTelemetryCollectionPool _collections = collections ?? throw new ArgumentNullException(nameof(collections));

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
        var enriched = await EnrichAsync(command, cancellationToken).ConfigureAwait(false);
        return await PublishAsync(_inner.TransitionNodeRunAsync(enriched, cancellationToken), kind, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     ONE announcement for the whole route, because it is one commit. Its watermark names the routing event, and
    ///     every reset the same transaction wrote sits after it, so a subscriber replaying from there still sees them.
    ///     <para>
    ///         Each reset is enriched exactly as a same-node re-attempt is: this is the OTHER write path into the store's
    ///         node-run transition, and a cross-node retry that skipped it would lose an attempt from every cost total.
    ///         The enrichment is re-derived on every ask and never cached, because the fix loop re-sends the same command
    ///         object after a lost concurrency race — reading the rows again is what keeps the second pass correct.
    ///     </para>
    /// </summary>
    public async Task<DevWorkflowMutationResult> RouteRetryAsync(RouteDevWorkflowRetryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // ONE deadline for every reset, not one each: `Resets` is built from the retry target's descendants and is
        // bounded only by the graph's width, so a per-reset budget would hold the dispatcher tick for N times it.
        using var deadline = NewCollectionDeadline(cancellationToken);
        var resets = new List<TransitionDevWorkflowNodeRunCommand>(command.Resets.Count);
        foreach (var reset in command.Resets)
        {
            resets.Add(await EnrichWithinDeadlineAsync(reset, deadline.Token, cancellationToken).ConfigureAwait(false));
        }

        return await PublishAsync(_inner.RouteRetryAsync(command with { Resets = resets }, cancellationToken), DevWorkflowChangeKind.Node, cancellationToken)
            .ConfigureAwait(false);
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

    /// <summary>
    ///     Attaches what the settling attempt cost, or forwards the command untouched. The gate is the target STATUS,
    ///     not the caller: a terminal, <c>Blocked</c> or <c>WaitingForApproval</c> move is where an attempt's spend
    ///     stops changing, and a call site added later crosses this method whether or not anyone remembers it.
    ///     <para>
    ///         <c>Blocked</c> and <c>WaitingForApproval</c> are in the set deliberately. They are LIVE statuses, yet
    ///         they are where the most expensive node runs land — retry-exhausted, budget-exhausted, resume-exhausted,
    ///         session-less — and gating on terminality alone would leave every abandoned node run reporting nothing.
    ///     </para>
    ///     <para>
    ///         The whole enrichment is contained: any throw, any timeout, and the ORIGINAL command goes through. It
    ///         runs before the inner store call and never inside its transaction, so a crash mid-collect loses a
    ///         measurement and nothing else.
    ///     </para>
    /// </summary>
    private async Task<TransitionDevWorkflowNodeRunCommand> EnrichAsync(TransitionDevWorkflowNodeRunCommand command, CancellationToken cancellationToken)
    {
        // The deadline is opened only for a command that will actually collect, so an ordinary Running transition
        // pays for no timer at all.
        if (!IsReAttempt(command) && !WritesTelemetry(command.TargetStatus))
        {
            return command;
        }

        using var deadline = NewCollectionDeadline(cancellationToken);
        return await EnrichWithinDeadlineAsync(command, deadline.Token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The enrichment itself, under a deadline its CALLER owns — one per settle, one per retry route.
    ///     <para>
    ///         The deadline is enforced by ABANDONING THE WAIT, not by asking the collection to stop. Cancellation is
    ///         cooperative, so a collector that never observes its token — or a database call that does not — would
    ///         otherwise hold a terminal transition or a retry route open forever, which is a workflow that never
    ///         settles rather than a measurement that is missing. <c>WaitAsync</c> gives back the thread when the
    ///         deadline fires and leaves the collection to finish into <see cref="ObserveLateCollection" />, where
    ///         its result is logged and dropped.
    ///     </para>
    ///     <para>
    ///         <paramref name="cancellationToken" /> is kept only to tell an expired deadline (swallowed, the command
    ///         goes through unenriched) from a cancelled caller (rethrown).
    ///     </para>
    /// </summary>
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

        // Admission BEFORE scheduling, because the deadline bounds the wait and not the work behind it: a collector
        // that never terminates would otherwise keep its worker and its scope for the life of the process, one per
        // settle. No slot free means no collection at all — the same trade an expired deadline makes.
        if (!_collections.TryEnter())
        {
            _logger.LogWarning("All {CollectionSlots} cost-collection slots are in use; node run {NodeRunId} is forwarded without a measurement.",
                _collections.Slots,
                command.NodeRunId);
            return command;
        }

        // Task.Run, so the boundary holds even against a collector that blocks BEFORE its first await — a call on
        // this stack would never reach the WaitAsync below. The consequence is that a reset is offered to the
        // collector EVENTUALLY rather than synchronously, which is what the route test waits for.
        var startedAt = Stopwatch.GetTimestamp();
        var collection = Task.Run(async () =>
            {
                try
                {
                    return await CollectAsync(command, deadline, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    // The slot comes back when the COLLECTOR terminates, not when the caller stops waiting for it —
                    // releasing it on the abandoned wait would let the next settle start work beside the stuck one,
                    // which is the accumulation the pool exists to stop.
                    _collections.Release();
                }
            },
            CancellationToken.None);

        try
        {
            return await collection.WaitAsync(deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ObserveLateCollection(collection, command.NodeRunId, startedAt);
            return command;
        }
    }

    /// <summary>
    ///     The reads a cost collection needs, on a service scope the COLLECTION owns and disposes.
    ///     <para>
    ///         The isolation is the point, not tidiness. This work can outlive the settle that started it, and the
    ///         settle's next act is to write through <c>_inner</c> — so a collection reading on the mutation's own
    ///         <c>DbContext</c> would be a second concurrent operation on it, which is a hard failure rather than a
    ///         lost measurement. Its own scope means its own <c>DbContext</c>, disposed when it finishes, whenever
    ///         that is.
    ///     </para>
    ///     <para>
    ///         Everything here READS. Nothing an abandoned collection can still be doing writes a row: the only write
    ///         is the enriched command it returns, and a late return is dropped by the caller.
    ///     </para>
    ///     <para>
    ///         The scope's <c>IDevWorkflowStore</c> is this same decorator around a fresh inner store; its read
    ///         methods forward untouched, and asking for the concrete inner store instead would bind this class to a
    ///         registration rather than to the interface it already depends on.
    ///     </para>
    /// </summary>
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
                ? await EnrichReAttemptAsync(command, reads, telemetry, deadline).ConfigureAwait(false)
                : await EnrichSettleAsync(command, reads, telemetry, deadline).ConfigureAwait(false);
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
        // The PRE-write row, and it is read for four things only: the work session, the development task, the
        // attempt's start and the row's node type. Its Status and OutputJson are the previous attempt's and must
        // never be the ones a route question is asked about.
        var snapshot = await reads.GetNodeRunAsync(command.NodeRunId, deadline).ConfigureAwait(false);
        var routeJson = await RouteJsonAsync(command, snapshot, reads, deadline).ConfigureAwait(false);
        var collected = await telemetry.CollectAsync(snapshot, command.TargetStatus, deadline).ConfigureAwait(false);

        if (collected is null && routeJson is null)
        {
            return command;
        }

        return command with { Telemetry = (collected ?? new DevWorkflowNodeTelemetry()) with { RouteJson = routeJson } };
    }

    /// <summary>
    ///     Watches a collection that outlived its deadline, so its completion is observed rather than left to the
    ///     unobserved-exception handler — and says so once, at warning, with counts only. Whatever it answers is
    ///     DROPPED: the transition it would have enriched has already gone through.
    /// </summary>
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

    /// <summary>
    ///     The route this settle took, or null when the move is not terminal — a node run that is <c>Blocked</c> or
    ///     waiting on a human has not finished, so it has routed nowhere yet, and saying so with a null is honest where
    ///     an empty document would not be.
    ///     <para>
    ///         The command's own target status and output are projected onto the pre-write row FIRST. Asked of the row
    ///         as it stands, every edge would answer <c>Pending</c> — the row still reads <c>Running</c>, carrying the
    ///         previous attempt's output — and the route document would be empty in every real run.
    ///     </para>
    /// </summary>
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

        var run = await reads.GetRunAsync(command.RunId, cancellationToken).ConfigureAwait(false);
        var decision = routeSource.NodeType == DevWorkflowNodeType.HumanGate
            ? DevWorkflowStateMachine.GateDecisionFrom(routeSource.OutputJson)
            : null;

        // The run's other rows, because whether a SKIP was waived is a walk back over the graph rather than something
        // this row carries. Without them a waived skip's out-edges would record as dead, which is the exact reading the
        // dispatcher does not make.
        var nodeRuns = await reads.ListNodeRunsAsync(command.RunId, cancellationToken).ConfigureAwait(false);
        var nodeRunsByKey = nodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);

        return DevWorkflowStateMachine.RouteJson(DevWorkflowStateMachine.RouteTaken(_graphs.Resolve(run), routeSource, nodeRunsByKey, decision));
    }

    /// <summary>
    ///     Arm B: the failing attempt's cost, captured onto the retry event BEFORE the reset that empties the row.
    ///     <para>
    ///         The node-run row keeps the LAST attempt only, so without this a node that failed twice and succeeded on
    ///         the third try would report one attempt of three, and every total that summed the rows would silently
    ///         under-report. The event log is where the per-attempt history already lives, and the retry event's own
    ///         detail is the place the event catalog names for it — so no event type is added and the retry policy,
    ///         which is a singleton and cannot hold a scoped collector, is not touched at all.
    ///     </para>
    ///     <para>
    ///         The pre-write row is also the last place the failing attempt's work session still exists: the command
    ///         clears it, but downstream, inside the store's own transition.
    ///     </para>
    /// </summary>
    private static async Task<TransitionDevWorkflowNodeRunCommand> EnrichReAttemptAsync(TransitionDevWorkflowNodeRunCommand command,
        IDevWorkflowStore reads,
        IDevWorkflowNodeTelemetrySource telemetry,
        CancellationToken deadline)
    {
        var snapshot = await reads.GetNodeRunAsync(command.NodeRunId, deadline).ConfigureAwait(false);

        // Collected as the FAILED attempt it is. The row itself may still read Running — a re-attempt is written
        // straight over a live row — but what this cost vector describes is an attempt that is over.
        var collected = await telemetry.CollectAsync(snapshot, DevWorkflowNodeRunStatus.Failed, deadline).ConfigureAwait(false);
        if (collected is null)
        {
            return command;
        }

        return MergeAttemptCost(command.DetailJson!, collected) is { } merged ? command with { DetailJson = merged } : command;
    }

    /// <summary>
    ///     Merges the COMPLETE additive cost vector into an existing retry detail, or answers null when the payload is
    ///     not a JSON object and must be forwarded verbatim.
    ///     <para>
    ///         The members come from the telemetry record itself minus the three that cannot be added up — the route,
    ///         the served model and the tool names. A column added to that record later therefore rides here
    ///         automatically, or it is not additive; nothing enumerates the ten by hand.
    ///     </para>
    /// </summary>
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
        var result = await mutation.ConfigureAwait(false);
        await _publisher.PublishAsync(result.RunId, result.Sequence, kind, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
