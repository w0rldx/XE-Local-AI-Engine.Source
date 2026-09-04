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
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

/// <summary>
///     Runs a work session as a bounded sequence of steps, each one an ordinary chat turn on the session's owned
///     conversation.
///     <para>
///         A step drives <see cref="INodeChatStreamService.SendMessageAsync" /> and drains the stream, rather than the
///         invocation runner directly. The runner persists nothing into a conversation: the message rows, the ordered
///         parts, the pump's terminalization, the resume registry a reloading browser re-attaches through, and the
///         approval/question lifecycle all live in the send path. Driving the runner instead would mean rebuilding
///         every one of them.
///     </para>
///     <para>
///         The enumeration is deliberately NOT cancelled to stop a step. Cancelling it only stops the supervisor
///         watching — the run keeps going, holding the node's one invocation slot, and the loop would never see its
///         terminal. A pause, a cancel, an unanswered park and a step deadline therefore all stop a step the way the
///         operator's stop button does: through <see cref="INodeChatStreamCancellationRegistry" />, which cancels the
///         runner so the pump persists the real terminal and the stream yields it.
///     </para>
///     <para>
///         Store calls here pass <see cref="CancellationToken.None" /> on purpose. Everything the loop writes after a
///         step is the record of what already happened — a checkpoint, a terminal status — and abandoning those writes
///         because a stop was requested is precisely how a session would be left mid-flight by the operation meant to
///         settle it. The loop stops by checking its token between steps instead.
///     </para>
/// </summary>
internal sealed class WorkSessionExecutionSupervisor : IWorkSessionExecutionSupervisor, IHostedService, IAsyncDisposable
{
    /// <summary>How long a stop waits for the loop to land before answering. A stuck provider must not hang the caller.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The <see cref="WorkSessionEventTypes.StepEnded" /> outcome for a step whose turn simply finished — as opposed
    ///     to <c>nameof(ProviderCallBudget)</c>, which names the bound that stopped it. Both rows carry the same
    ///     consumption detail; the outcome is what tells a reader whether the step was clipped.
    /// </summary>
    private const string StepCompletedOutcome = "Completed";

    /// <summary>
    ///     The <see cref="WorkSessionEventTypes.StepEnded" /> outcome for a step that was never sent because the node's
    ///     tool-capable allow-list no longer admits the session's model. It carries no consumption detail, deliberately:
    ///     nothing ran, and an empty record would read as a step that cost nothing rather than one that never happened.
    /// </summary>
    private const string ToolGateOutcome = "ToolGate";

    /// <summary>
    ///     Web defaults, so the consumption record reaches the browser in the same camelCase convention as every other
    ///     JSON payload the session surface carries.
    /// </summary>
    private static readonly JsonSerializerOptions ConsumptionJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The admission gate. A slot is taken before the run is registered and released in the run's finally, so the
    ///     cap holds across concurrent starts for DIFFERENT sessions — a count checked after the add lets two
    ///     admissions each see room and then each back out, admitting fewer sessions than the cap allows.
    ///     <para>
    ///         Never disposed, deliberately: <see cref="SemaphoreSlim.Dispose()" /> only matters once
    ///         <c>AvailableWaitHandle</c> has been touched, which nothing here does, and
    ///         <see cref="DisposeAsync" /> does not wait for the in-flight runs — so a run landing after the host went
    ///         down still releases its slot instead of faulting an unobserved task on a disposed gate.
    ///     </para>
    /// </summary>
    private readonly SemaphoreSlim _admission;

    private readonly INodeChatStreamCancellationRegistry _cancellationRegistry;
    private readonly ILogger<WorkSessionExecutionSupervisor> _logger;
    private readonly IWorkSessionEventPublisher _publisher;
    private readonly WorkSessionOptions _options;
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
        ILogger<WorkSessionExecutionSupervisor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _cancellationRegistry = cancellationRegistry ?? throw new ArgumentNullException(nameof(cancellationRegistry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
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

        await run.Cancellation.CancelAsync().ConfigureAwait(false);
        if (run.Completion is { } completion)
        {
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(StopGrace);
            var landed = await Task.WhenAny(completion, Task.Delay(Timeout.InfiniteTimeSpan, grace.Token)).ConfigureAwait(false);
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
        await _shutdown.CancelAsync().ConfigureAwait(false);
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

        await _shutdown.CancelAsync().ConfigureAwait(false);
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
            await RunSessionAsync(sessionId, run).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Work session {SessionId} execution stopped.", sessionId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Work session {SessionId} execution failed.", sessionId);
            await TerminalizeFailureAsync(sessionId, "The work session could not continue because a step failed unexpectedly.").ConfigureAwait(false);
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
            var state = await WithStoreAsync(store => LoadStateAsync(store, sessionId)).ConfigureAwait(false);
            if (state.Session.Status is AgentWorkSessionStatus.Completed or AgentWorkSessionStatus.Cancelled or AgentWorkSessionStatus.Failed)
            {
                return;
            }

            if (state.Session.Status != AgentWorkSessionStatus.Running)
            {
                var moved = await WithStoreAsync(store => store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId,
                            WorkSessionVersions.Any,
                            AgentWorkSessionStatus.Running,
                            WorkSessionStateBlockComposer.ResolveCurrentTask(state)?.Id),
                        CancellationToken.None))
                    .ConfigureAwait(false);
                await _publisher.PublishAsync(sessionId, moved.LastSequence, WorkSessionChangeKind.Status, CancellationToken.None).ConfigureAwait(false);
                state = state with
                {
                    Session = moved
                };
            }

            var outcome = await RunStepAsync(run, state, stepsThisRun).ConfigureAwait(false);
            stepsThisRun++;
            if (outcome == StepOutcome.Settled)
            {
                return;
            }
        }

        // The loop was stopped between steps rather than mid-turn, so no terminal has been written yet.
        await SettleStoppedRunAsync(sessionId, run).ConfigureAwait(false);
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
            await CheckpointAsync(sessionId).ConfigureAwait(false);
        }

        await SettleAsync(sessionId,
                reason == WorkSessionStopReason.Cancel ? AgentWorkSessionStatus.Cancelled : AgentWorkSessionStatus.Paused,
                reason == WorkSessionStopReason.Cancel ? "The operator cancelled the work session." : "The operator paused the work session.")
            .ConfigureAwait(false);
    }

    private async Task<StepOutcome> RunStepAsync(SessionRun run, WorkSessionState state, int stepsThisRun)
    {
        var sessionId = state.Session.Id;
        var step = state.Session.StepCount + 1;

        // ONE scope for the turn, and it holds only the scoped stream service the enumeration belongs to. Every store
        // write goes through its own short-lived scope instead: the tool handlers write the same session row from their
        // own scopes mid-turn, and a DbContext held across that would raise a stale-row concurrency failure on the
        // supervisor's next write even though nothing was actually lost.
        await using var turnScope = _scopeFactory.CreateAsyncScope();

        // Guard the conversation before anything is written. A session whose conversation was deleted through another
        // path can never take another step, and failing here is legible where an exception out of the send path is not.
        var persistence = turnScope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>();
        if (await persistence.GetConversationOriginAsync(state.Session.ConversationId, CancellationToken.None).ConfigureAwait(false) is null)
        {
            await SettleAsync(sessionId, AgentWorkSessionStatus.Failed, "The conversation this work session owns no longer exists.").ConfigureAwait(false);
            return StepOutcome.Settled;
        }

        // The operator's tool-capable allow-list is read LIVE on every offer, so a model that was listed when the
        // session was created can be gone from the list by the time it takes a step. Nothing downstream would say so:
        // the step runs, the model receives the offer WITHOUT the four state tools, every update_work_plan call comes
        // back "Requested function update_work_plan not found", and the session spends its whole step budget writing
        // nothing. Stop the step here instead, naming the list and the model.
        //
        // Only the allow-list is asked (InspectAllowListAsync): the capability probe the create boundary also runs can
        // be a provider round-trip, and this guard would not read its answer. A session whose agent definition has been
        // deleted is deliberately NOT judged either — the create path could not have judged it, and which model that
        // turn resolves is then the send path's decision, not this one's.
        WorkSessionToolGateVerdict? toolGate = null;
        try
        {
            toolGate = await turnScope.ServiceProvider.GetRequiredService<WorkSessionToolGate>()
                                      .InspectAllowListAsync(state.Session.AgentDefinitionId, run.Runtime?.ModelProfile, CancellationToken.None)
                                      .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
        {
            // Fail OPEN, and the asymmetry is the point: gate 4 is ENFORCED by the offer, not by this guard. The tools
            // are withheld with or without it, so all a failed check costs is the legible pause — whereas failing
            // closed would turn a transient store hiccup into a stopped session on a check that is advisory by
            // construction. The worst case is one ordinary step that goes out tool-less, which the next step re-checks
            // and which MaxStepsPerRun still bounds.
            _logger.LogWarning(exception,
                "Could not check the tool-capable allow-list for work session {SessionId} before step {Step}; taking the step anyway.",
                sessionId,
                step);
        }

        if (toolGate is { AgentExists: true, EffectiveModel: not null, IsAllowListed: false } refused)
        {
            var refusal = WorkSessionToolGate.AllowListRefusal(refused);
            _logger.LogWarning("Work session {SessionId} step {Step} was not sent: {Reason}", sessionId, step, refusal);

            // PAUSED, not Failed, and that is what makes the refusal's own advice actionable: Resume accepts only
            // Paused/Interrupted and a repoint only Draft/Paused/Interrupted, so a Failed session could not be started
            // again after the operator did exactly what the message asked. Checkpoint first, then the status — the same
            // order, and for the same reason, as the step-budget pause.
            //
            // The row is StepEnded with an outcome naming the gate, never StepFailed: nothing failed, and a paused
            // session carrying a failure row reads as a contradiction. Its own phase keeps the operation id distinct
            // from the Ended row the retried step writes when it really does run, which idempotency would otherwise
            // swallow.
            _ = await WithStoreAsync(store => store.AppendEventAsync(new AppendWorkSessionEventCommand(sessionId,
                        WorkSessionVersions.Any,
                        WorkSessionEventTypes.StepEnded,
                        WorkSessionOperationId.For(sessionId, step, WorkSessionStepPhases.ToolGate),
                        ToolGateOutcome),
                    CancellationToken.None))
                .ConfigureAwait(false);
            await CheckpointAsync(sessionId).ConfigureAwait(false);
            await SettleAsync(sessionId, AgentWorkSessionStatus.Paused, refusal).ConfigureAwait(false);
            return StepOutcome.Settled;
        }

        // Bound what this step will replay BEFORE the send. Every earlier step's state block, answer and reasoning is
        // otherwise re-sent verbatim for the life of the session, and the step's own tool loop — a single knowledge-base
        // read is capped at 50,000 characters — needs that room. Over budget, the older turns fold into the synopsis the
        // send path splices. Nothing durable is lost: the state block below is rebuilt from the database every step.
        //
        // The gate verdict above already resolved which model THIS step will run on, so hand it over: calibration is
        // per-model, and a session repointed while paused (or an unpinned agent whose node default moved) would
        // otherwise be measured under the model the LAST step ran on. Null when the agent was deleted or the gate read
        // failed, which is exactly when the bound's transcript fallback is the best answer available.
        await turnScope.ServiceProvider.GetRequiredService<ConversationStepContextBound>()
                       .ApplyAsync(state.Session.ConversationId, _options.StepContextBudgetTokens, toolGate?.EffectiveModel, CancellationToken.None)
                       .ConfigureAwait(false);

        // Published BEFORE the send, not after. By the time a step terminalizes, the invocation resume registry has
        // dropped its entry, so a client told about the step only then re-attaches to an empty stream and never sees the
        // turn go live.
        var started = await WithStoreAsync(store => store.AppendEventAsync(new AppendWorkSessionEventCommand(sessionId,
                    WorkSessionVersions.Any,
                    WorkSessionEventTypes.StepStarted,
                    WorkSessionOperationId.For(sessionId, step, WorkSessionStepPhases.Started),
                    step.ToString(CultureInfo.InvariantCulture)),
                CancellationToken.None))
            .ConfigureAwait(false);
        await _publisher.PublishAsync(sessionId, started.Sequence, WorkSessionChangeKind.Step, CancellationToken.None).ConfigureAwait(false);

        var correlation = new NodeChatMessageCorrelation(state.Session.ConversationId, Guid.NewGuid(), Guid.NewGuid());
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
            // The caller's pins for this session, or nulls that leave every turn resolving exactly as it does today. A
            // model here suppresses the bound agent's own pin the same way the chat dropdown's pick does; the effort
            // needs the flag beside it, because a caller-supplied effort otherwise LOSES to the agent's.
            Model: run.Runtime?.ModelProfile,
            ReasoningEffort: run.Runtime?.ReasoningEffort,
            ReasoningEffortOverridesAgentPin: run.Runtime?.ReasoningEffort is { Length: > 0 },
            // Every step of every session, whatever the caller pinned: this turn is autonomous, so the send path must
            // never let the adaptive-effort dispatcher serve it on a model nobody chose.
            IsWorkSessionTurn: true,
            // GRAPH-C4-2's runtime half, handed to the turn that has to obey it rather than asked here. The node's
            // declaration and its template's waiver ride on the runtime override, which is re-supplied on every start
            // and resume off the run's PINNED graph — so nothing an operator edits mid-run can widen what a running
            // session is allowed to be offered. The decision itself belongs to the send: it resolves the mutable agent
            // definition once and judges the offer THAT resolution produced, which is the only projection a check can
            // be sure the turn will actually use. Asked here instead, it would answer about a definition the send is
            // free to re-resolve differently a moment later.
            RefuseUndeclaredWrites: run.Runtime?.RefuseUndeclaredWrites == true,
            // No operator is attached to a workflow-owned session, and its embedded chat is read-only, so an ask_user
            // question could only ever go unanswered — see the flag's own comment for what that costs.
            SuppressAskUser: state.Session.Kind == AgentWorkSessionKind.Workflow);

        // Tighten the tool-result ceiling for this step, seeded BEFORE the enumeration starts so the value flows into
        // the invocation's async context (the send path calls the runner inline, not through a detached Task). The
        // node-wide budget is larger than read_document's own 50,000-character cap, so without this nothing clips a
        // knowledge-base read and three of them fill a 65,536-token window on their own. Disposal restores the prior
        // value; a step still running after the drain keeps the value its context already captured.
        using var resultBudget = _options.MaxToolResultCharacters > 0
            ? ToolResultBudgetScope.BeginScope(_options.MaxToolResultCharacters)
            : null;

        // Cap the tool-calling loop for this step, seeded the same way and for the same reason. Each iteration re-sends
        // every prior result and reasoning block, so a step's context grows quadratically in its own calls; clipping
        // each result is not enough once there are fourteen of them. Hitting the cap ends the step, not the session.
        using var callBudget = _options.MaxProviderCallsPerStep > 0
            ? ProviderCallBudget.BeginCallCapScope(_options.MaxProviderCallsPerStep)
            : null;

        ChatStreamEvent terminal;
        try
        {
            terminal = await DrainStepAsync(turnScope.ServiceProvider.GetRequiredService<INodeChatStreamService>(), guard, request, sessionId).ConfigureAwait(false);
        }
        catch (WorkSessionUndeclaredWriteException refusal)
        {
            // The send resolved the agent definition, saw a write/execute tool the node never declared in the offer it
            // was about to hand the model, and stopped rather than sending it (GRAPH-C4-2). Nothing ran, so this is the
            // gate's own row and not a step failure — and the row is what the owning run reads back, because the state
            // it was decided from is mutable and re-deriving the cause later would answer differently.
            //
            // Failed rather than Paused: a paused workflow session is resumed by its owning run, so a pause would loop
            // until the resume budget ran out and report a budget it did not really exhaust. Failed settles it once,
            // and the run's next poll blocks the node run with this rule's own class and this sentence.
            return await SettleWriteGateAsync(sessionId, step, refusal.Message).ConfigureAwait(false);
        }
        finally
        {
            run.Correlation = null;
        }

        return await SettleStepAsync(run, guard, sessionId, step, stepsThisRun, started.Sequence, terminal, callBudget).ConfigureAwait(false);
    }

    /// <summary>Drains one step's stream to its terminal, mapping parks onto the session status as they happen.</summary>
    private async Task<ChatStreamEvent> DrainStepAsync(INodeChatStreamService stream, StepCancellationGuard guard, NodeChatStreamRequest request, Guid sessionId)
    {
        var parked = false;
        await foreach (var streamEvent in stream.SendMessageAsync(request, CancellationToken.None).ConfigureAwait(false))
        {
            switch (streamEvent.Type)
            {
                case ChatStreamEventTypes.ApprovalRequested:
                case ChatStreamEventTypes.QuestionRequested:
                    parked = true;
                    guard.ArmPark(TimeSpan.FromSeconds(_options.MaxParkedSeconds), streamEvent.ToolName);
                    await MoveAsync(sessionId,
                            streamEvent.Type == ChatStreamEventTypes.ApprovalRequested
                                ? AgentWorkSessionStatus.WaitingForApproval
                                : AgentWorkSessionStatus.WaitingForInput)
                        .ConfigureAwait(false);
                    break;

                case ChatStreamEventTypes.AssistantDelta:
                case ChatStreamEventTypes.ToolCallCompleted:
                    if (parked)
                    {
                        parked = false;
                        guard.DisarmPark();
                        await MoveAsync(sessionId, AgentWorkSessionStatus.Running).ConfigureAwait(false);
                    }

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
        return new ChatStreamEvent(ChatStreamEventTypes.AssistantFailed,
            request.ConversationId,
            request.MessageId.GetValueOrDefault(),
            request.RequestId.GetValueOrDefault(),
            NodeChatMessageStatusValues.Failed,
            Sequence: 0,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private async Task<StepOutcome> SettleStepAsync(SessionRun run,
        StepCancellationGuard guard,
        Guid sessionId,
        int step,
        int stepsThisRun,
        long stepStartedSequence,
        ChatStreamEvent terminalEvent,
        ProviderCallCapScope? callBudget)
    {
        var terminal = terminalEvent.Type;

        // What the step actually spent, captured once now that the enumeration has landed and the run's own budget has
        // stopped moving. It rides on whichever terminal row this step writes, because the cap it is measured against
        // is a guess until there are recorded steps to size it from.
        var consumption = ComposeStepConsumptionDetail(callBudget);
        var endedRecorded = false;

        // A step that spent its provider-call cap is BOUNDED, not broken: the tools it ran are already persisted and
        // the next step resumes from the state block. Recognised by the budget's own fixed, path-free terminal messages,
        // which the classifier forwards verbatim onto the failed row. Falling through to the failure branch would end
        // the session on its own safety limit.
        // Both messages are matched: StepCallCapReachedMessage is the per-step cap this supervisor itself seeds, and
        // CeilingExceededMessage is the node-wide invocation ceiling — a session that hits the wider one is still only
        // bounded, so ending it there would be the same bug one ceiling further out.
        if (terminal == ChatStreamEventTypes.AssistantFailed
            && (string.Equals(terminalEvent.Error, ProviderCallBudget.StepCallCapReachedMessage, StringComparison.Ordinal)
                || string.Equals(terminalEvent.Error, ProviderCallBudget.CeilingExceededMessage, StringComparison.Ordinal)))
        {
            _logger.LogInformation("Work session {SessionId} step {Step} reached its provider-call budget; ending the step and continuing.", sessionId, step);
            await AppendStepEndedAsync(sessionId, step, nameof(ProviderCallBudget), consumption).ConfigureAwait(false);
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
                _ = await WithStoreAsync(store => store.AppendEventAsync(new AppendWorkSessionEventCommand(sessionId,
                            WorkSessionVersions.Any,
                            WorkSessionEventTypes.StepFailed,
                            WorkSessionOperationId.For(sessionId, step, WorkSessionStepPhases.Failed),
                            step.ToString(CultureInfo.InvariantCulture),
                            consumption),
                        CancellationToken.None))
                    .ConfigureAwait(false);
                await CheckpointAsync(sessionId).ConfigureAwait(false);
                await SettleAsync(sessionId, AgentWorkSessionStatus.Failed, "A work session step failed.").ConfigureAwait(false);
                return StepOutcome.Settled;

            case ChatStreamEventTypes.AssistantCancelled:
                return await SettleCancelledStepAsync(run, guard, sessionId, step).ConfigureAwait(false);

            default:
                break;
        }

        // An ordinary step records its spend too, not only the one that tripped the cap — a record that only ever reads
        // "10/10" measures the bound rather than the work, and sizing the cap needs the steps that stayed under it.
        // Written BEFORE AdvanceStepAsync so the row lands on the step it describes rather than on the next one.
        if (!endedRecorded)
        {
            await AppendStepEndedAsync(sessionId, step, StepCompletedOutcome, consumption).ConfigureAwait(false);
        }

        var summary = await WithStoreAsync(store => ReadCompletionSummaryAsync(store, sessionId, stepStartedSequence)).ConfigureAwait(false);
        var advanced = await WithStoreAsync(store => store.AdvanceStepAsync(sessionId, WorkSessionVersions.Any, CancellationToken.None)).ConfigureAwait(false);
        await _publisher.PublishAsync(sessionId, advanced.Sequence, WorkSessionChangeKind.Step, CancellationToken.None).ConfigureAwait(false);

        if (summary is not null)
        {
            await CheckpointAsync(sessionId).ConfigureAwait(false);
            await SettleAsync(sessionId, AgentWorkSessionStatus.Completed, summary).ConfigureAwait(false);
            return StepOutcome.Settled;
        }

        if (stepsThisRun + 1 >= _options.MaxStepsPerRun)
        {
            await CheckpointAsync(sessionId).ConfigureAwait(false);
            await SettleAsync(sessionId,
                    AgentWorkSessionStatus.Paused,
                    string.Create(CultureInfo.InvariantCulture, $"The run reached its step budget of {_options.MaxStepsPerRun} steps."))
                .ConfigureAwait(false);
            return StepOutcome.Settled;
        }

        if (advanced.Step % _options.CheckpointEveryNSteps == 0)
        {
            await CheckpointAsync(sessionId).ConfigureAwait(false);
        }

        // A stop that landed while this step was finishing is handled by the loop condition, so the run settles through
        // one path instead of two.
        return StepOutcome.Continue;
    }

    /// <summary>
    ///     Appends the step's <see cref="WorkSessionEventTypes.StepEnded" /> row. The operation id is derived from the
    ///     step and its phase, so the two callers here can never write two rows for one step: whichever runs second is
    ///     swallowed by the store's idempotency.
    /// </summary>
    private async Task AppendStepEndedAsync(Guid sessionId, int step, string outcome, string? detailJson)
    {
        _ = await WithStoreAsync(store => store.AppendEventAsync(new AppendWorkSessionEventCommand(sessionId,
                    WorkSessionVersions.Any,
                    WorkSessionEventTypes.StepEnded,
                    WorkSessionOperationId.For(sessionId, step, WorkSessionStepPhases.Ended),
                    outcome,
                    detailJson),
                CancellationToken.None))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Serializes what one step consumed into the shape described on
    ///     <see cref="WorkSessionEventDto.DetailJson" />, or <see langword="null" /> when nothing ran under a cap scope
    ///     (no budget was created, so there is nothing to report — an empty record would read as "this step was free").
    ///     <para>
    ///         The counts and the tool names come off the cap scope the step itself seeded, which is the only readable
    ///         seam: the run's ambient <see cref="ProviderCallBudget" /> is written INSIDE the send path and an
    ///         <see cref="AsyncLocal{T}" /> write does not flow back out, so <c>ProviderCallBudget.Current</c> is null
    ///         again by the time the enumeration returns — and the scope itself is disposed a moment later, which is
    ///         why this row is where the names have to land if anything is ever to read them.
    ///     </para>
    ///     <para>
    ///         Every member is a STEP TOTAL, and the provider's own reported usage is deliberately not among them. The
    ///         terminal event does carry <c>InputTokens</c>/<c>OutputTokens</c>, but the runner assigns
    ///         <c>UsageSnapshot</c> per provider round rather than accumulating, so on a multi-round step those are the
    ///         LAST round's numbers — beside a step-total call count and a step-total estimate they would read as a
    ///         contradiction. Estimate-versus-truth is measured per round where the two halves actually match, by
    ///         <c>ProviderCallBudgetChatClient</c>'s observed-usage write-back.
    ///     </para>
    /// </summary>
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

    /// <summary>
    ///     A cancelled step. The checkpoint is committed FIRST and the status LAST: a crash in that window reconciles to
    ///     <c>Interrupted</c> off a valid checkpoint, whereas writing the status first would leave a paused session
    ///     resuming from a stale state block.
    /// </summary>
    private async Task<StepOutcome> SettleCancelledStepAsync(SessionRun run, StepCancellationGuard guard, Guid sessionId, int step)
    {
        if (run.StopReason == WorkSessionStopReason.Cancel)
        {
            await SettleAsync(sessionId, AgentWorkSessionStatus.Cancelled, "The operator cancelled the work session.").ConfigureAwait(false);
            return StepOutcome.Settled;
        }

        await CheckpointAsync(sessionId).ConfigureAwait(false);

        var reason = "The operator paused the work session.";
        if (guard.ParkExpired)
        {
            reason = "The work session was paused because a prompt went unanswered.";
            _ = await WithStoreAsync(store => store.AppendEventAsync(new AppendWorkSessionEventCommand(sessionId,
                        WorkSessionVersions.Any,
                        WorkSessionEventTypes.ParkTimedOut,
                        WorkSessionOperationId.For(sessionId, step, WorkSessionStepPhases.ParkExpired),
                        guard.ParkedToolName),
                    CancellationToken.None))
                .ConfigureAwait(false);

            // Recorded as a finding, not only as an event, so the next step's state block re-asks it. The park itself is
            // in-memory and survives neither this timeout nor a restart; this sentence is what makes the question
            // durable, and it is written BEFORE the status so a crash in between cannot lose it.
            var findingId = Guid.NewGuid();
            _ = await WithStoreAsync(store => store.AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                        findingId,
                        WorkSessionVersions.Any,
                        WorkSessionOperationId.For(sessionId, step, $"park-question:{findingId:N}"),
                        AgentWorkSessionFindingKind.OpenQuestion,
                        ParkedQuestionText(guard.ParkedToolName)),
                    CancellationToken.None))
                .ConfigureAwait(false);
        }
        else if (guard.DeadlineExpired)
        {
            reason = "The work session step ran past its time budget.";
        }

        await SettleAsync(sessionId, AgentWorkSessionStatus.Paused, reason).ConfigureAwait(false);
        return StepOutcome.Settled;
    }

    private string ParkedQuestionText(string? toolName) =>
        toolName is { Length: > 0 }
            ? string.Create(CultureInfo.InvariantCulture,
                $"The tool '{toolName}' asked for a decision and nobody answered within {_options.MaxParkedSeconds} seconds, so the step was stopped. Ask again, or find another way forward.")
            : string.Create(CultureInfo.InvariantCulture,
                $"A prompt went unanswered for {_options.MaxParkedSeconds} seconds, so the step was stopped. Ask again, or find another way forward.");

    /// <summary>
    ///     Reads back whether <c>complete_work_session</c> fired during the step, and the summary it carried. The tool
    ///     records an event rather than setting an in-memory flag, so the request survives a crash between the call and
    ///     the end of the turn — and reading it back from the watermark the step opened with keeps the query bounded.
    /// </summary>
    private async Task<string?> ReadCompletionSummaryAsync(IAgentWorkSessionStore store, Guid sessionId, long stepStartedSequence)
    {
        const string Fallback = "The agent declared the work session complete.";
        var events = await store.ListEventsAsync(sessionId, stepStartedSequence, CancellationToken.None).ConfigureAwait(false);
        var recorded = events.LastOrDefault(static candidate => candidate.EventType == WorkSessionEventTypes.CompletionRequested);
        if (recorded is null)
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
            var result = await composer.ComposeAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            await _publisher.PublishAsync(sessionId, result.Sequence, WorkSessionChangeKind.Checkpoint, CancellationToken.None).ConfigureAwait(false);
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
                    store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, WorkSessionVersions.Any, target), CancellationToken.None))
                .ConfigureAwait(false);
            await _publisher.PublishAsync(sessionId, moved.LastSequence, WorkSessionChangeKind.Status, CancellationToken.None).ConfigureAwait(false);
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
            var settled = await WithStoreAsync(store => store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, WorkSessionVersions.Any, target, CurrentTaskId: null, reason),
                    CancellationToken.None))
                .ConfigureAwait(false);
            await _publisher.PublishAsync(sessionId, settled.LastSequence, WorkSessionChangeKind.Status, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WorkSessionInvalidTransitionException exception)
        {
            _logger.LogWarning(exception, "Work session {SessionId} was already past {Status} when the step ended.", sessionId, target);
        }
    }

    /// <summary>
    ///     Records the write-declaration gate's refusal against the step it stopped, then settles the session on it.
    ///     <para>
    ///         The sentence is written as the step row's DETAIL and not only as the session's terminal reason, because
    ///         the development-workflow run that owns this session has to answer with <c>GRAPH-C4-2</c>'s own failure
    ///         class and the only honest source for a historical cause is the record written when it happened. A cause
    ///         re-derived from the agent definition's CURRENT state goes quiet the moment an operator restores or
    ///         narrows that definition, and the node run then falls through as an ordinary retryable provider failure.
    ///     </para>
    /// </summary>
    private async Task<StepOutcome> SettleWriteGateAsync(Guid sessionId, int step, string refusal)
    {
        _logger.LogWarning("Work session {SessionId} step {Step} was not sent: {Reason}", sessionId, step, refusal);
        _ = await WithStoreAsync(store => store.AppendEventAsync(new AppendWorkSessionEventCommand(sessionId,
                    WorkSessionVersions.Any,
                    WorkSessionEventTypes.StepEnded,
                    WorkSessionOperationId.For(sessionId, step, WorkSessionStepPhases.WriteGate),
                    WorkSessionEventTypes.WriteGateOutcome,
                    WorkSessionEventTypes.WriteGateDetail(refusal)),
                CancellationToken.None))
            .ConfigureAwait(false);
        await CheckpointAsync(sessionId).ConfigureAwait(false);
        await SettleAsync(sessionId, AgentWorkSessionStatus.Failed, refusal).ConfigureAwait(false);
        return StepOutcome.Settled;
    }

    private async Task TerminalizeFailureAsync(Guid sessionId, string reason)
    {
        try
        {
            await SettleAsync(sessionId, AgentWorkSessionStatus.Failed, reason).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
        {
            _logger.LogError(exception, "Work session {SessionId} could not be terminalized after a failure.", sessionId);
        }
    }

    /// <summary>
    ///     Runs one store operation in its own scope, so no <c>DbContext</c> outlives the write it made. The tool
    ///     handlers mutate the same session row from their own scopes while a step is in flight; a context held across
    ///     that would carry a stale row version into the supervisor's next write and fail it as a lost update.
    /// </summary>
    private async Task<T> WithStoreAsync<T>(Func<IAgentWorkSessionStore, Task<T>> operation)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()).ConfigureAwait(false);
    }

    private static async Task<WorkSessionState> LoadStateAsync(IAgentWorkSessionStore store, Guid sessionId)
    {
        var session = await store.GetAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        var tasks = await store.ListTasksAsync(sessionId, sinceSequence: 0, CancellationToken.None).ConfigureAwait(false);
        var findings = await store.ListFindingsAsync(sessionId, sinceSequence: 0, CancellationToken.None).ConfigureAwait(false);
        var artifacts = await store.ListArtifactsAsync(sessionId, sinceSequence: 0, CancellationToken.None).ConfigureAwait(false);
        var checkpoint = await store.GetLatestCheckpointAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        return new WorkSessionState(session, tasks, findings, artifacts, checkpoint);
    }

    private enum StepOutcome
    {
        Continue,
        Settled
    }

    private sealed class SessionRun(CancellationTokenSource cancellation, WorkSessionRuntimeOverride? runtime)
    {
        private NodeChatMessageCorrelation? _correlation;
        private int _stopReason = -1;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        /// <summary>
        ///     What this run was told to run on instead of the bound agent's own pins, or null for the agent's. Held for
        ///     the life of the run rather than stored on the session: the caller re-supplies it every time it starts or
        ///     resumes the session, which is what makes a restart cost nothing.
        /// </summary>
        public WorkSessionRuntimeOverride? Runtime { get; } = runtime is { IsEmpty: false } ? runtime : null;

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
