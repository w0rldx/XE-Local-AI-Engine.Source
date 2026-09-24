namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

/// <summary>
///     Runs a work session as a bounded sequence of steps, each one an ordinary chat turn on the session's owned
///     conversation.
/// </summary>
/// <remarks>
///     A step drives <see cref="INodeChatStreamService.SendMessageAsync" />, never the invocation runner, and is
///     stopped only through <see cref="INodeChatStreamCancellationRegistry" />: cancelling the enumeration leaves the
///     run holding the node's invocation slot with nobody left to read its terminal. Store calls after a step pass
///     <see cref="CancellationToken.None" /> because they record what already happened; the loop stops by checking its
///     token between steps. See docs/wiki/04-agent-mode.md ("One step").
/// </remarks>
internal sealed class WorkSessionExecutionSupervisor : IWorkSessionExecutionSupervisor, IHostedService, IAsyncDisposable
{
    /// <summary>How long a stop waits for the loop to land before answering. A stuck provider must not hang the caller.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(30);

    /// <summary>The <see cref="WorkSessionEventTypes.StepEnded" /> outcome for a step whose turn simply finished.</summary>
    /// <remarks>
    ///     <c>nameof(ProviderCallBudget)</c> names the bound that stopped a step instead. Both rows carry the same
    ///     consumption detail; the outcome is what tells a reader whether the step was clipped.
    /// </remarks>
    private const string StepCompletedOutcome = "Completed";

    /// <summary>
    ///     The <see cref="WorkSessionEventTypes.StepEnded" /> outcome for a step that was never sent because the node's
    ///     tool-capable allow-list no longer admits the session's model.
    /// </summary>
    /// <remarks>
    ///     It carries no consumption detail, deliberately: nothing ran, and an empty record would read as a step that
    ///     cost nothing rather than one that never happened.
    /// </remarks>
    private const string ToolGateOutcome = "ToolGate";

    /// <summary>
    ///     Web defaults, so the consumption record reaches the browser in the same camelCase convention as the rest of
    ///     the session surface.
    /// </summary>
    private static readonly JsonSerializerOptions ConsumptionJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The admission gate: a slot is taken before the run is registered and released in the run's finally.</summary>
    /// <remarks>
    ///     Taking it first is what makes the cap hold across concurrent starts for DIFFERENT sessions — a count checked
    ///     after the add lets two admissions each see room and then each back out. Never disposed, deliberately:
    ///     <see cref="SemaphoreSlim.Dispose()" /> only matters once <c>AvailableWaitHandle</c> has been touched, which
    ///     nothing here does, and <see cref="DisposeAsync" /> does not wait for the in-flight runs, so a run landing
    ///     after the host went down still releases its slot instead of faulting on a disposed gate.
    /// </remarks>
    private readonly SemaphoreSlim _admission;

    private readonly INodeChatStreamCancellationRegistry _cancellationRegistry;
    private readonly ILogger<WorkSessionExecutionSupervisor> _logger;
    private readonly IWorkSessionEventPublisher _publisher;
    private readonly WorkSessionOptions _options;
    private readonly TimeSpan _pendingToolCallAge;

    /// <summary>
    ///     The node's one set of tool calls parked on an out-of-stream answer. Read only to tell a dropped approval
    ///     request from an ordinary stream drop — see the <c>AssistantReconcile</c> case in <see cref="DrainStepAsync" />.
    /// </summary>
    private readonly PendingToolCallRegistry _pendingToolCalls;

    private readonly ConcurrentDictionary<Guid, SessionRun> _runs = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    public WorkSessionExecutionSupervisor(IServiceScopeFactory scopeFactory,
        INodeChatStreamCancellationRegistry cancellationRegistry,
        IWorkSessionEventPublisher publisher,
        IOptions<WorkSessionOptions> options,
        TimeProvider timeProvider,
        PendingToolCallRegistry pendingToolCalls,
        ToolApprovalCoordinator approvalCoordinator,
        ILogger<WorkSessionExecutionSupervisor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pendingToolCalls = pendingToolCalls ?? throw new ArgumentNullException(nameof(pendingToolCalls));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _cancellationRegistry = cancellationRegistry ?? throw new ArgumentNullException(nameof(cancellationRegistry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        ArgumentNullException.ThrowIfNull(approvalCoordinator);
        // Use the wait owner's snapshot: separately reading settings could observe a later edit.
        _pendingToolCallAge = approvalCoordinator.PendingToolCallAge;
        _admission = new SemaphoreSlim(_options.MaxConcurrentSessions, _options.MaxConcurrentSessions);
    }

    /// <summary>
    ///     A hint, not a reservation — the slot can be taken between this read and the caller's start. The authority is
    ///     <see cref="TryStart" />, which <c>WorkSessionService.BeginAsync</c> re-checks.
    /// </summary>
    public bool HasCapacity => _options.Enabled && !_shutdown.IsCancellationRequested && _admission.CurrentCount > 0;

    public bool TryStart(Guid sessionId, WorkSessionRuntimeOverride? runtime = null)
    {
        if (!_options.Enabled || _shutdown.IsCancellationRequested || !_admission.Wait(millisecondsTimeout: 0, CancellationToken.None))
        {
            return false;
        }

        CancellationTokenSource? cancellation = null;
        var admitted = false;
        try
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var run = new SessionRun(cancellation, runtime);
            if (!_runs.TryAdd(sessionId, run))
            {
                return false;
            }

            // Ownership passes to the run: its finally removes the entry, disposes the source, and releases the slot.
            cancellation = null;
            admitted = true;
            run.Completion = RunSessionObservedAsync(sessionId, run);
            return true;
        }
        finally
        {
            cancellation?.Dispose();
            if (!admitted)
            {
                _ = _admission.Release();
            }
        }
    }

    public async ValueTask<bool> TryStopAsync(Guid sessionId, WorkSessionStopReason reason, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(sessionId, out var run))
        {
            return false;
        }

        run.RequestStop(reason);
        if (run.Correlation is { } correlation)
        {
            _ = _cancellationRegistry.TryCancel(correlation);
        }

        await run.Cancellation.CancelAsync();
        if (run.Completion is { } completion)
        {
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(StopGrace);
            var landed = await Task.WhenAny(completion, Task.Delay(Timeout.InfiniteTimeSpan, grace.Token));
            if (landed != completion)
            {
                _logger.LogWarning("Work session {SessionId} did not land within the stop grace period; its terminal will be written when the step ends.", sessionId);
            }
        }

        return true;
    }

    public bool IsRunning(Guid sessionId) =>
        _runs.ContainsKey(sessionId);

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    ///     Cancels every in-flight step so the pump writes its terminal before the host goes. The session rows are left
    ///     as they stand: <c>Interrupted</c> is the startup reconciler's to write, and it collapses them on the next
    ///     start.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        foreach (var (_, run) in _runs)
        {
            if (run.Correlation is { } correlation)
            {
                _ = _cancellationRegistry.TryCancel(correlation);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync();
        _shutdown.Dispose();
        foreach (var (_, run) in _runs)
        {
            run.Cancellation.Dispose();
        }

        _runs.Clear();
    }

    private async Task RunSessionObservedAsync(Guid sessionId, SessionRun run)
    {
        try
        {
            await RunSessionAsync(sessionId, run);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Work session {SessionId} execution stopped.", sessionId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Work session {SessionId} execution failed.", sessionId);
            await TerminalizeFailureAsync(sessionId, "The work session could not continue because a step failed unexpectedly.");
        }
        finally
        {
            if (_runs.TryRemove(sessionId, out var removed))
            {
                removed.Cancellation.Dispose();
            }

            _ = _admission.Release();
        }
    }

    private async Task RunSessionAsync(Guid sessionId, SessionRun run)
    {
        var stepsThisRun = 0;
        while (!run.Cancellation.IsCancellationRequested)
        {
            var state = await WithStoreAsync(store => LoadStateAsync(store, sessionId));
            if (state.Session.Status is AgentWorkSessionStatus.Completed or AgentWorkSessionStatus.Cancelled or AgentWorkSessionStatus.Failed)
            {
                return;
            }

            if (state.Session.Status != AgentWorkSessionStatus.Running)
            {
                var moved = await WithStoreAsync(store => store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand
                    {
                        SessionId = sessionId,
                        ExpectedVersion = WorkSessionVersions.Any,
                        TargetStatus = AgentWorkSessionStatus.Running,
                        CurrentTaskId = WorkSessionStateBlockComposer.ResolveCurrentTask(state)?.Id
                    },
                    CancellationToken.None));
                await _publisher.PublishAsync(sessionId, moved.LastSequence, WorkSessionChangeKind.Status, CancellationToken.None);
                state = state with
                {
                    Session = moved
                };
            }

            var outcome = await RunStepAsync(run, state, stepsThisRun);
            stepsThisRun++;
            if (outcome == StepOutcome.Settled)
            {
                return;
            }
        }

        // The loop was stopped between steps rather than mid-turn, so no terminal has been written yet.
        await SettleStoppedRunAsync(sessionId, run);
    }

    private async Task SettleStoppedRunAsync(Guid sessionId, SessionRun run)
    {
        if (run.StopReason is not { } reason)
        {
            // Host shutdown, not an operator stop: leave the row alone for the startup reconciler.
            return;
        }

        if (reason == WorkSessionStopReason.Pause)
        {
            await CheckpointAsync(sessionId);
        }

        await SettleAsync(sessionId,
            reason == WorkSessionStopReason.Cancel ? AgentWorkSessionStatus.Cancelled : AgentWorkSessionStatus.Paused,
            reason == WorkSessionStopReason.Cancel ? "The operator cancelled the work session." : "The operator paused the work session.");
    }

    private async Task<StepOutcome> RunStepAsync(SessionRun run, WorkSessionState state, int stepsThisRun)
    {
        var sessionId = state.Session.Id;
        var step = state.Session.StepCount + 1;

        // The attempt key for this step's operation ids. Every attempt writes at least one row, so a re-run under the same
        // step index always reads a higher value, and idempotency no longer swallows its StepStarted or ParkTimedOut.
        var attempt = state.Session.LastSequence;

        // ONE scope for the turn, holding only the scoped stream service the enumeration belongs to; every store write
        // takes its own. The tool handlers write this session row mid-turn, so a held DbContext goes stale under them.
        await using var turnScope = _scopeFactory.CreateAsyncScope();

        // Guard the conversation before anything is written. A session whose conversation was deleted through another
        // path can never take another step, and failing here is legible where an exception out of the send path is not.
        var persistence = turnScope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>();
        if (await persistence.GetConversationOriginAsync(state.Session.ConversationId, CancellationToken.None) is null)
        {
            await SettleAsync(sessionId, AgentWorkSessionStatus.Failed, "The conversation this work session owns no longer exists.");
            return StepOutcome.Settled;
        }

        // Read live per offer, so a model listed at create time can be gone by this step and the step would silently
        // run tool-less. Allow-list only; a deleted agent is not judged — wiki 04-agent-mode.md ("what a repoint may not do").
        WorkSessionToolGateVerdict? toolGate = null;
        try
        {
            toolGate = await turnScope.ServiceProvider.GetRequiredService<WorkSessionToolGate>()
                                      .InspectAllowListAsync(state.Session.AgentDefinitionId, run.Runtime?.ModelProfile, CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
        {
            // Fail OPEN: the offer enforces this gate anyway, so the guard is advisory and a transient store hiccup
            // must not stop a session. Worst case is one tool-less step, which the next step re-checks.
            _logger.LogWarning(exception,
                "Could not check the tool-capable allow-list for work session {SessionId} before step {Step}; taking the step anyway.",
                sessionId,
                step);
        }

        if (toolGate is { AgentExists: true, EffectiveModel: not null, IsAllowListed: false } refused)
        {
            var refusal = WorkSessionToolGate.AllowListRefusal(refused);
            _logger.LogWarning("Work session {SessionId} step {Step} was not sent: {Reason}", sessionId, step, refusal);

            // PAUSED, not Failed: Resume accepts only Paused/Interrupted, so Failed would lock out the operator who did
            // exactly what the refusal asked. Checkpoint before status, StepEnded with its own phase — wiki 04-agent-mode.md.
            _ = await WithStoreAsync(store => store.AppendEventAsync(new AppendWorkSessionEventCommand
                {
                    SessionId = sessionId,
                    ExpectedVersion = WorkSessionVersions.Any,
                    EventType = WorkSessionEventTypes.StepEnded,
                    OperationId = WorkSessionOperationId.For(sessionId, step, WorkSessionStepPhases.ToolGate, attempt),
                    Outcome = ToolGateOutcome
                },
                CancellationToken.None));
            await CheckpointAsync(sessionId);
            await SettleAsync(sessionId, AgentWorkSessionStatus.Paused, refusal);
            return StepOutcome.Settled;
        }

        // Bound the replay BEFORE the send; nothing durable is lost, the state block is rebuilt from the database every
        // step. The model comes from the gate verdict, because calibration is per-model and a repoint moves it.
        await turnScope.ServiceProvider.GetRequiredService<ConversationStepContextBound>()
                       .ApplyAsync(state.Session.ConversationId, _options.StepContextBudgetTokens, toolGate?.EffectiveModel, CancellationToken.None);

        // Recorded BEFORE the send (it is the completion read's watermark) but PUBLISHED from DrainStepAsync once the
        // invocation is live, because a client re-attaches on this push and an earlier one finds nothing to attach to.
        var started = await WithStoreAsync(store => store.AppendEventAsync(new AppendWorkSessionEventCommand
            {
                SessionId = sessionId,
                ExpectedVersion = WorkSessionVersions.Any,
                EventType = WorkSessionEventTypes.StepStarted,
                OperationId = WorkSessionOperationId.For(sessionId, step, WorkSessionStepPhases.Started, attempt),
                Outcome = step.ToString(CultureInfo.InvariantCulture)
            },
            CancellationToken.None));

        var correlation = new NodeChatMessageCorrelation
        {
            ConversationId = state.Session.ConversationId,
            MessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid()
        };
        using var guard = new StepCancellationGuard(_cancellationRegistry, correlation, _timeProvider);
        run.Correlation = correlation;
        if (_options.StepTimeoutSeconds > 0)
        {
            guard.ArmDeadline(TimeSpan.FromSeconds(_options.StepTimeoutSeconds));
        }

        var request = new NodeChatStreamRequest(state.Session.ConversationId,
            WorkSessionStateBlockComposer.Compose(state, step, _options.MaxStepsPerRun),
            MessageId: correlation.MessageId,
            RequestId: correlation.RequestId,
            UseLocalTools: true,
            AgentDefinitionId: state.Session.AgentDefinitionId,
            // The session's pins, or nulls that leave the turn resolving as usual. A model here suppresses the agent's
            // own pin as the chat dropdown does; the effort needs the flag beside it, or it LOSES to the agent's.
            Model: run.Runtime?.ModelProfile,
            ReasoningEffort: run.Runtime?.ReasoningEffort,
            ReasoningEffortOverridesAgentPin: run.Runtime?.ReasoningEffort is { Length: > 0 },
            // Every step of every session, whatever the caller pinned: this turn is autonomous, so the send path must
            // never let the adaptive-effort dispatcher serve it on a model nobody chose.
            IsWorkSessionTurn: true,
            // GRAPH-C4-2's runtime half: the declaration rides on the override, re-supplied on every start and resume
            // off the run's PINNED graph, so no mid-run edit widens it. The send decides — only it sees the real offer.
            RefuseUndeclaredWrites: run.Runtime?.RefuseUndeclaredWrites == true,
            // No operator is attached to a workflow-owned session, and its embedded chat is read-only, so an ask_user
            // question could only ever go unanswered — see the flag's own comment for what that costs.
            SuppressAskUser: state.Session.Kind == AgentWorkSessionKind.Workflow);

        // Seeded BEFORE the enumeration so it flows into the invocation's async context. The node-wide budget is larger
        // than read_document's 50,000-character cap, so without this nothing clips a knowledge-base read.
        using var resultBudget = _options.MaxToolResultCharacters > 0
            ? ToolResultBudgetScope.BeginScope(_options.MaxToolResultCharacters)
            : null;

        // Cap the tool loop, seeded the same way: each iteration re-sends every prior result, so context grows
        // quadratically in a step's own calls and clipping each result alone is not enough. The cap ends the step only.
        using var callBudget = _options.MaxProviderCallsPerStep > 0
            ? ProviderCallBudget.BeginCallCapScope(_options.MaxProviderCallsPerStep)
            : null;

        ChatStreamEvent terminal;
        try
        {
            terminal = await DrainStepAsync(turnScope.ServiceProvider.GetRequiredService<INodeChatStreamService>(), guard, request, sessionId, step, started.Sequence);
        }
        catch (WorkSessionUndeclaredWriteException refusal)
        {
            // The send saw an undeclared write/execute tool in the offer and stopped (GRAPH-C4-2); nothing ran, so this
            // is the gate's own row. Failed, not Paused — the owning run would resume a pause until its budget died.
            return await SettleWriteGateAsync(sessionId, step, attempt, refusal.Message);
        }
        finally
        {
            run.Correlation = null;
        }

        return await SettleStepAsync(run, guard, sessionId, step, attempt, stepsThisRun, terminal, callBudget);
    }

    private void ArmPark(StepCancellationGuard guard, Guid invocationId, string? toolName, long occurredAtUtc = 0)
    {
        var now = _timeProvider.GetUtcNow();
        // Questions have no registry row; their producer timestamp still accounts for time queued in the stream.
        var requestedAt = occurredAtUtc > 0 && occurredAtUtc <= now.ToUnixTimeMilliseconds()
            ? DateTimeOffset.FromUnixTimeMilliseconds(occurredAtUtc)
            : now;
        var createdAt = _pendingToolCalls.Calls.Values
                                         .Where(call => call.InvocationId == invocationId)
                                         .Select(static call => call.CreatedAt)
                                         .DefaultIfEmpty(requestedAt)
                                         .Min();
        var remaining = createdAt + _pendingToolCallAge - TimeSpan.FromSeconds(1) - now;
        var budget = TimeSpan.FromSeconds(_options.MaxParkedSeconds);
        var capped = remaining < budget ? remaining : budget;
        guard.ArmPark(capped > TimeSpan.Zero ? capped : TimeSpan.Zero, toolName);
    }

    /// <summary>Drains one step's stream to its terminal, mapping parks onto the session status as they happen.</summary>
    private async Task<ChatStreamEvent> DrainStepAsync(INodeChatStreamService stream,
        StepCancellationGuard guard,
        NodeChatStreamRequest request,
        Guid sessionId,
        int step,
        long stepStartedSequence)
    {
        var parked = false;
        var announced = false;
        await foreach (var streamEvent in stream.SendMessageAsync(request, CancellationToken.None))
        {
            // Only once the turn is live: the send registers the invocation (mirrored by the resume registry synchronously)
            // before AssistantStreaming, while a pre-run AssistantNotice is emitted BEFORE the slot, when nothing is attachable.
            if (!announced
                && streamEvent.Type is ChatStreamEventTypes.AssistantStreaming or ChatStreamEventTypes.AssistantPhase
                    or ChatStreamEventTypes.AssistantCompleted or ChatStreamEventTypes.AssistantFailed
                    or ChatStreamEventTypes.AssistantCancelled or ChatStreamEventTypes.AssistantInterrupted)
            {
                announced = true;
                await _publisher.PublishAsync(sessionId, stepStartedSequence, WorkSessionChangeKind.Step, CancellationToken.None);
            }

            switch (streamEvent.Type)
            {
                case ChatStreamEventTypes.ApprovalRequested:
                case ChatStreamEventTypes.QuestionRequested:
                    parked = true;
                    ArmPark(guard, request.RequestId.GetValueOrDefault(), streamEvent.ToolName, streamEvent.OccurredAtUtc);
                    await MoveAsync(sessionId,
                        streamEvent.Type == ChatStreamEventTypes.ApprovalRequested
                            ? AgentWorkSessionStatus.WaitingForApproval
                            : AgentWorkSessionStatus.WaitingForInput);
                    break;

                case ChatStreamEventTypes.AssistantDelta:
                case ChatStreamEventTypes.ToolCallCompleted:
                    if (parked)
                    {
                        parked = false;
                        guard.DisarmPark();
                        await MoveAsync(sessionId, AgentWorkSessionStatus.Running);
                    }

                    break;

                case ChatStreamEventTypes.AssistantReconcile:
                    // A reconcile stands in for dropped events, which may include the one that arms the park — see
                    // docs/wiki/04-agent-mode.md ("A dropped park event"). Already parked: the ORIGINAL deadline stands.
                    if (parked)
                    {
                        _logger.LogWarning("Work session {SessionId} step {Step} took a stream reconcile while already parked on '{ToolName}'; the original park deadline stands.",
                            sessionId,
                            step,
                            guard.ParkedToolName);
                        break;
                    }

                    // Not a guess: the coordinator registers the parked call BEFORE broadcasting the event, so the entry
                    // is already there. No entry means the drop was not an approval, and arming would stop a live turn.
                    if (!_pendingToolCalls.Calls.Values.Any(call => call.InvocationId == request.RequestId.GetValueOrDefault()))
                    {
                        _logger.LogInformation("Work session {SessionId} step {Step} took a stream reconcile with no tool call parked under it, so nothing was armed.", sessionId, step);
                        break;
                    }

                    // The turn IS waiting on a human, so arm the clock off the signal that survived. The registry entry
                    // carries no tool name, hence null; the delta/completed case disarms it if the turn turns out live.
                    parked = true;
                    ArmPark(guard, request.RequestId.GetValueOrDefault(), toolName: null);
                    await MoveAsync(sessionId, AgentWorkSessionStatus.WaitingForApproval);
                    _logger.LogWarning("Work session {SessionId} step {Step} lost the event for a parked tool call to a stream drop; the park clock was armed off the reconcile.",
                        sessionId,
                        step);
                    break;

                case ChatStreamEventTypes.AssistantCompleted:
                case ChatStreamEventTypes.AssistantFailed:
                case ChatStreamEventTypes.AssistantCancelled:
                case ChatStreamEventTypes.AssistantInterrupted:
                    return streamEvent;

                default:
                    break;
            }
        }

        // The stream ended without a terminal event. Read that as a failure rather than looping: the assistant row was
        // terminalized by the pump either way, and a second step would go out over an unknown state.
        return new ChatStreamEvent
        {
            Type = ChatStreamEventTypes.AssistantFailed,
            ConversationId = request.ConversationId,
            MessageId = request.MessageId.GetValueOrDefault(),
            RequestId = request.RequestId.GetValueOrDefault(),
            Status = NodeChatMessageStatusValues.Failed,
            Sequence = 0,
            OccurredAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };
    }

    private async Task<StepOutcome> SettleStepAsync(SessionRun run,
        StepCancellationGuard guard,
        Guid sessionId,
        int step,
        long attempt,
        int stepsThisRun,
        ChatStreamEvent terminalEvent,
        ProviderCallCapScope? callBudget)
    {
        var terminal = terminalEvent.Type;

        // What the step spent, captured once the enumeration has landed and the budget has stopped moving. It rides on
        // whichever terminal row this step writes: the cap is a guess until there are recorded steps to size it from.
        var consumption = ComposeStepConsumptionDetail(callBudget);
        var endedRecorded = false;

        // A spent call cap is BOUNDED, not broken; falling through would end the session on its own safety limit. Both
        // fixed messages match — the per-step cap seeded above and the node-wide ceiling bound the step alike.
        if (terminal == ChatStreamEventTypes.AssistantFailed
            && (string.Equals(terminalEvent.Error, ProviderCallBudget.StepCallCapReachedMessage, StringComparison.Ordinal)
                || string.Equals(terminalEvent.Error, ProviderCallBudget.CeilingExceededMessage, StringComparison.Ordinal)))
        {
            _logger.LogInformation("Work session {SessionId} step {Step} reached its provider-call budget; ending the step and continuing.", sessionId, step);
            await AppendStepEndedAsync(sessionId, step, attempt, nameof(ProviderCallBudget), consumption);
            endedRecorded = true;
            terminal = ChatStreamEventTypes.AssistantCompleted;
        }

        switch (terminal)
        {
            case ChatStreamEventTypes.AssistantInterrupted:
                // The host is going down. The row stays as it is on purpose: Interrupted is the startup reconciler's to
                // write, and it collapses this session on the next start.
                _logger.LogInformation("Work session {SessionId} step {Step} was interrupted by host shutdown.", sessionId, step);
                return StepOutcome.Settled;

            case ChatStreamEventTypes.AssistantFailed:
                _ = await WithStoreAsync(store => store.AppendEventAsync(new AppendWorkSessionEventCommand
                    {
                        SessionId = sessionId,
                        ExpectedVersion = WorkSessionVersions.Any,
                        EventType = WorkSessionEventTypes.StepFailed,
                        OperationId = WorkSessionOperationId.For(sessionId, step, WorkSessionStepPhases.Failed, attempt),
                        Outcome = step.ToString(CultureInfo.InvariantCulture),
                        DetailJson = consumption
                    },
                    CancellationToken.None));
                await CheckpointAsync(sessionId);
                await SettleAsync(sessionId, AgentWorkSessionStatus.Failed, "A work session step failed.");
                return StepOutcome.Settled;

            case ChatStreamEventTypes.AssistantCancelled:
                return await SettleCancelledStepAsync(run, guard, sessionId, step, attempt);

            default:
                break;
        }

        // An ordinary step records its spend too: a record that only ever reads "10/10" measures the bound, not the
        // work. Written BEFORE AdvanceStepAsync so the row lands on the step it describes rather than on the next one.
        if (!endedRecorded)
        {
            await AppendStepEndedAsync(sessionId, step, attempt, StepCompletedOutcome, consumption);
        }

        var summary = await WithStoreAsync(store => ReadCompletionSummaryAsync(store, sessionId));
        var advanced = await WithStoreAsync(store => store.AdvanceStepAsync(sessionId, WorkSessionVersions.Any, CancellationToken.None));
        await _publisher.PublishAsync(sessionId, advanced.Sequence, WorkSessionChangeKind.Step, CancellationToken.None);

        if (summary is not null)
        {
            await CheckpointAsync(sessionId);
            await SettleAsync(sessionId, AgentWorkSessionStatus.Completed, summary);
            return StepOutcome.Settled;
        }

        if (stepsThisRun + 1 >= _options.MaxStepsPerRun)
        {
            await CheckpointAsync(sessionId);
            await SettleAsync(sessionId,
                AgentWorkSessionStatus.Paused,
                string.Create(CultureInfo.InvariantCulture, $"The run reached its step budget of {_options.MaxStepsPerRun} steps."));
            return StepOutcome.Settled;
        }

        if (advanced.Step % _options.CheckpointEveryNSteps == 0)
        {
            await CheckpointAsync(sessionId);
        }

        // A stop that landed while this step was finishing is handled by the loop condition, so the run settles through
        // one path instead of two.
        return StepOutcome.Continue;
    }

    /// <summary>
    ///     Appends the step's <see cref="WorkSessionEventTypes.StepEnded" /> row. The operation id is derived from the
    ///     step, its attempt and its phase, so the two callers here can never write two rows for one attempt: whichever
    ///     runs second is swallowed by the store's idempotency.
    /// </summary>
    private async Task AppendStepEndedAsync(Guid sessionId, int step, long attempt, string outcome, string? detailJson)
    {
        _ = await WithStoreAsync(store => store.AppendEventAsync(new AppendWorkSessionEventCommand
            {
                SessionId = sessionId,
                ExpectedVersion = WorkSessionVersions.Any,
                EventType = WorkSessionEventTypes.StepEnded,
                OperationId = WorkSessionOperationId.For(sessionId, step, WorkSessionStepPhases.Ended, attempt),
                Outcome = outcome,
                DetailJson = detailJson
            },
            CancellationToken.None));
    }

    /// <summary>
    ///     Serializes what one step consumed into the shape described on
    ///     <see cref="WorkSessionEventDto.DetailJson" />, or <see langword="null" /> when no cap scope ran.
    /// </summary>
    /// <remarks>
    ///     Null rather than an empty record, which would read as "this step was free". Every member is a STEP TOTAL off
    ///     the scope the step itself seeded — the only readable seam, because the send path's ambient
    ///     <see cref="ProviderCallBudget" /> is an <see cref="AsyncLocal{T}" /> write that does not flow back out, so
    ///     <c>ProviderCallBudget.Current</c> is null again once the enumeration returns. How the numbers may be read:
    ///     docs/wiki/04-agent-mode.md ("The per-step consumption record").
    /// </remarks>
    private static string? ComposeStepConsumptionDetail(ProviderCallCapScope? capScope)
    {
        if (capScope?.CaptureConsumption() is not { } consumption)
        {
            return null;
        }

        return JsonSerializer.Serialize(new WorkSessionStepConsumptionDetail(consumption.ProviderCalls,
                consumption.EstimatedInputTokens,
                consumption.ToolCallsCompleted,
                consumption.ProviderCallCap,
                consumption.AttachedBudgets,
                consumption.ToolSchemaTokens,
                consumption.ToolNames),
            ConsumptionJsonOptions);
    }

    /// <summary>Settles a cancelled step: the checkpoint is committed FIRST and the status LAST.</summary>
    /// <remarks>
    ///     A crash in that window reconciles to <c>Interrupted</c> off a valid checkpoint, whereas writing the status
    ///     first would leave a paused session resuming from a stale state block.
    /// </remarks>
    private async Task<StepOutcome> SettleCancelledStepAsync(SessionRun run, StepCancellationGuard guard, Guid sessionId, int step, long attempt)
    {
        if (run.StopReason == WorkSessionStopReason.Cancel)
        {
            await SettleAsync(sessionId, AgentWorkSessionStatus.Cancelled, "The operator cancelled the work session.");
            return StepOutcome.Settled;
        }

        await CheckpointAsync(sessionId);

        var reason = "The operator paused the work session.";
        if (guard.ParkExpired)
        {
            reason = "The work session was paused because a prompt went unanswered.";
            _ = await WithStoreAsync(store => store.AppendEventAsync(new AppendWorkSessionEventCommand
                {
                    SessionId = sessionId,
                    ExpectedVersion = WorkSessionVersions.Any,
                    EventType = WorkSessionEventTypes.ParkTimedOut,
                    OperationId = WorkSessionOperationId.For(sessionId, step, WorkSessionStepPhases.ParkExpired, attempt),
                    Outcome = guard.ParkedToolName
                },
                CancellationToken.None));

            // A finding, not only an event, so the next step's state block re-asks it: the park is in-memory and
            // survives neither the timeout nor a restart. Written BEFORE the status so a crash cannot lose it.
            var findingId = Guid.NewGuid();
            var finding = await WithStoreAsync(store => store.AppendFindingAsync(new AppendWorkSessionFindingCommand
                {
                    SessionId = sessionId,
                    FindingId = findingId,
                    ExpectedVersion = WorkSessionVersions.Any,
                    OperationId = WorkSessionOperationId.For(sessionId, step, $"park-question:{findingId:N}"),
                    Kind = AgentWorkSessionFindingKind.OpenQuestion,
                    Text = ParkedQuestionText(guard.ParkedToolName)
                },
                CancellationToken.None));
            await _publisher.PublishAsync(sessionId, finding.Sequence, WorkSessionChangeKind.Finding, CancellationToken.None);
        }
        else if (guard.DeadlineExpired)
        {
            reason = "The work session step ran past its time budget.";
        }

        await SettleAsync(sessionId, AgentWorkSessionStatus.Paused, reason);
        return StepOutcome.Settled;
    }

    private static string ParkedQuestionText(string? toolName) =>
        toolName is { Length: > 0 }
            ? string.Create(CultureInfo.InvariantCulture,
                $"The tool '{toolName}' asked for a decision and nobody answered before the response deadline, so the step was stopped. Ask again, or find another way forward.")
            : "A prompt went unanswered before the response deadline, so the step was stopped. Ask again, or find another way forward.";

    /// <summary>
    ///     Reads back whether <c>complete_work_session</c> fired during the step, and the summary it carried.
    /// </summary>
    /// <remarks>
    ///     The tool records an event rather than setting an in-memory flag, so the request survives a crash between the
    ///     call and the end of the turn; reading from the watermark the step opened with keeps the query bounded.
    /// </remarks>
    private async Task<string?> ReadCompletionSummaryAsync(IAgentWorkSessionStore store, Guid sessionId)
    {
        const string Fallback = "The agent declared the work session complete.";
        // Any completion recorded since this step NUMBER began counts, whichever attempt recorded it: the tool handler dedups
        // per step, so a retried attempt cannot record it again, and reading after its own StepStarted would hide it for good.
        var stepBoundary = (await store.FindLatestEventAsync(sessionId, WorkSessionEventTypes.StepAdvanced, CancellationToken.None))?.Sequence ?? 0;
        var recorded = await store.FindLatestEventAsync(sessionId, WorkSessionEventTypes.CompletionRequested, CancellationToken.None);
        if (recorded is null || recorded.Sequence <= stepBoundary)
        {
            return null;
        }

        if (recorded.DetailJson is not { Length: > 0 } detail)
        {
            return Fallback;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<WorkSessionCompletionDetail>(detail)?.Summary;
            return string.IsNullOrWhiteSpace(parsed) ? Fallback : parsed;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Work session {SessionId} recorded an unreadable completion detail.", sessionId);
            return Fallback;
        }
    }

    private async Task CheckpointAsync(Guid sessionId)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var composer = scope.ServiceProvider.GetRequiredService<WorkSessionCheckpointComposer>();
            var result = await composer.ComposeAsync(sessionId, CancellationToken.None);
            await _publisher.PublishAsync(sessionId, result.Sequence, WorkSessionChangeKind.Checkpoint, CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
        {
            // A checkpoint is how a session survives a restart, but failing to take one must not also lose the terminal
            // status that follows it.
            _logger.LogWarning(exception, "Could not checkpoint work session {SessionId}.", sessionId);
        }
    }

    /// <summary>A status move that is not the session's terminal — the park transitions. Never throws the loop over.</summary>
    private async Task MoveAsync(Guid sessionId, AgentWorkSessionStatus target)
    {
        try
        {
            var moved = await WithStoreAsync(store =>
                store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand
                {
                    SessionId = sessionId,
                    ExpectedVersion = WorkSessionVersions.Any,
                    TargetStatus = target
                }, CancellationToken.None));
            await _publisher.PublishAsync(sessionId, moved.LastSequence, WorkSessionChangeKind.Status, CancellationToken.None);
        }
        catch (WorkSessionInvalidTransitionException exception)
        {
            _logger.LogDebug(exception, "Work session {SessionId} could not move to {Status} mid-step.", sessionId, target);
        }
    }

    private async Task SettleAsync(Guid sessionId, AgentWorkSessionStatus target, string reason)
    {
        try
        {
            var settled = await WithStoreAsync(store => store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand
                {
                    SessionId = sessionId,
                    ExpectedVersion = WorkSessionVersions.Any,
                    TargetStatus = target,
                    CurrentTaskId = null,
                    SanitizedReason = reason
                },
                CancellationToken.None));
            await _publisher.PublishAsync(sessionId, settled.LastSequence, WorkSessionChangeKind.Status, CancellationToken.None);
        }
        catch (WorkSessionInvalidTransitionException exception)
        {
            _logger.LogWarning(exception, "Work session {SessionId} was already past {Status} when the step ended.", sessionId, target);
        }
    }

    /// <summary>
    ///     Records the write-declaration gate's refusal against the step it stopped, then settles the session on it.
    /// </summary>
    /// <remarks>
    ///     The sentence is the step row's DETAIL, not only the session's terminal reason: the owning workflow run must
    ///     answer with <c>GRAPH-C4-2</c>'s own failure class, and the record written when it happened is the only honest
    ///     source for a historical cause. Re-derived from the definition's CURRENT state it goes quiet the moment an
    ///     operator restores or narrows it, and the node run falls through as a retryable provider failure.
    /// </remarks>
    private async Task<StepOutcome> SettleWriteGateAsync(Guid sessionId, int step, long attempt, string refusal)
    {
        _logger.LogWarning("Work session {SessionId} step {Step} was not sent: {Reason}", sessionId, step, refusal);
        _ = await WithStoreAsync(store => store.AppendEventAsync(new AppendWorkSessionEventCommand
            {
                SessionId = sessionId,
                ExpectedVersion = WorkSessionVersions.Any,
                EventType = WorkSessionEventTypes.StepEnded,
                OperationId = WorkSessionOperationId.For(sessionId, step, WorkSessionStepPhases.WriteGate, attempt),
                Outcome = WorkSessionEventTypes.WriteGateOutcome,
                DetailJson = WorkSessionEventTypes.WriteGateDetail(refusal)
            },
            CancellationToken.None));
        await CheckpointAsync(sessionId);
        await SettleAsync(sessionId, AgentWorkSessionStatus.Failed, refusal);
        return StepOutcome.Settled;
    }

    private async Task TerminalizeFailureAsync(Guid sessionId, string reason)
    {
        try
        {
            await SettleAsync(sessionId, AgentWorkSessionStatus.Failed, reason);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
        {
            _logger.LogError(exception, "Work session {SessionId} could not be terminalized after a failure.", sessionId);
        }
    }

    /// <summary>
    ///     Runs one store operation in its own scope, so no <c>DbContext</c> outlives the write it made.
    /// </summary>
    /// <remarks>
    ///     The tool handlers mutate the same session row from their own scopes while a step is in flight; a context
    ///     held across that carries a stale row version into the supervisor's next write and fails it as a lost update.
    /// </remarks>
    private async Task<T> WithStoreAsync<T>(Func<IAgentWorkSessionStore, Task<T>> operation)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>());
    }

    private static async Task<WorkSessionState> LoadStateAsync(IAgentWorkSessionStore store, Guid sessionId)
    {
        var session = await store.GetAsync(sessionId, CancellationToken.None);
        var tasks = await store.ListTasksAsync(sessionId, sinceSequence: 0, CancellationToken.None);
        var findings = await store.ListFindingsAsync(sessionId, sinceSequence: 0, CancellationToken.None);
        var artifacts = await store.ListArtifactsAsync(sessionId, sinceSequence: 0, CancellationToken.None);
        var checkpoint = await store.GetLatestCheckpointAsync(sessionId, CancellationToken.None);
        return new WorkSessionState
        {
            Session = session,
            Tasks = tasks,
            Findings = findings,
            Artifacts = artifacts,
            LastCheckpoint = checkpoint
        };
    }

    private enum StepOutcome
    {
        Continue,
        Settled
    }

    private sealed class SessionRun
    {
        private NodeChatMessageCorrelation? _correlation;
        private int _stopReason = -1;

        public SessionRun(CancellationTokenSource cancellation, WorkSessionRuntimeOverride? runtime)
        {
            Cancellation = cancellation;
            Runtime = runtime is { IsEmpty: false } ? runtime : null;
        }

        public CancellationTokenSource Cancellation { get; }

        /// <summary>
        ///     What this run was told to run on instead of the bound agent's own pins, or null for the agent's.
        /// </summary>
        /// <remarks>
        ///     Held for the life of the run rather than stored on the session: the caller re-supplies it on every start
        ///     and resume, which is what makes a restart cost nothing.
        /// </remarks>
        public WorkSessionRuntimeOverride? Runtime { get; }

        public Task? Completion { get; set; }

        /// <summary>The in-flight step's correlation, or null between steps. Written by the loop, read by a stopper.</summary>
        public NodeChatMessageCorrelation? Correlation
        {
            get => Volatile.Read(ref _correlation);
            set => Volatile.Write(ref _correlation, value);
        }

        public WorkSessionStopReason? StopReason
        {
            get
            {
                var value = Volatile.Read(ref _stopReason);
                return value < 0 ? null : (WorkSessionStopReason)value;
            }
        }

        public void RequestStop(WorkSessionStopReason reason) =>
            Volatile.Write(ref _stopReason, (int)reason);
    }
}
