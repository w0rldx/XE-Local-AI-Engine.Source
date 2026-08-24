namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
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
    }

    public bool HasCapacity =>
        _options.Enabled && !_shutdown.IsCancellationRequested && _runs.Count < _options.MaxConcurrentSessions;

    public bool TryStart(Guid sessionId)
    {
        if (!_options.Enabled || _shutdown.IsCancellationRequested)
        {
            return false;
        }

        CancellationTokenSource? cancellation = null;
        try
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var run = new SessionRun(cancellation);
            if (!_runs.TryAdd(sessionId, run))
            {
                return false;
            }

            // ponytail: the cap is checked after the add, so two simultaneous admissions can each see room for a moment.
            // At the shipped default of 1 that admits at most one extra session, which the node's single invocation slot
            // then serializes anyway. Take a real gate here if the cap ever becomes a resource promise.
            if (_runs.Count > _options.MaxConcurrentSessions)
            {
                _ = _runs.TryRemove(sessionId, out _);
                return false;
            }

            // Ownership passes to the run: its finally removes the entry and disposes the source.
            cancellation = null;
            run.Completion = RunSessionObservedAsync(sessionId, run);
            return true;
        }
        finally
        {
            cancellation?.Dispose();
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
                state = state with { Session = moved };
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

        // Bound what this step will replay BEFORE the send. Every earlier step's state block, answer and reasoning is
        // otherwise re-sent verbatim for the life of the session, and the step's own tool loop — a single knowledge-base
        // read is capped at 50,000 characters — needs that room. Over budget, the older turns fold into the synopsis the
        // send path splices. Nothing durable is lost: the state block below is rebuilt from the database every step.
        await turnScope.ServiceProvider.GetRequiredService<WorkSessionStepContextBound>()
                       .ApplyAsync(state.Session.ConversationId, _options.StepContextBudgetTokens, CancellationToken.None)
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
            AgentDefinitionId: state.Session.AgentDefinitionId);

        // Tighten the tool-result ceiling for this step, seeded BEFORE the enumeration starts so the value flows into
        // the invocation's async context (the send path calls the runner inline, not through a detached Task). The
        // node-wide budget is larger than read_document's own 50,000-character cap, so without this nothing clips a
        // knowledge-base read and three of them fill a 65,536-token window on their own. Disposal restores the prior
        // value; a step still running after the drain keeps the value its context already captured.
        using var resultBudget = _options.MaxToolResultCharacters > 0
            ? ToolResultBudgetScope.BeginScope(_options.MaxToolResultCharacters)
            : null;

        string terminal;
        try
        {
            terminal = await DrainStepAsync(turnScope.ServiceProvider.GetRequiredService<INodeChatStreamService>(), guard, request, sessionId).ConfigureAwait(false);
        }
        finally
        {
            run.Correlation = null;
        }

        return await SettleStepAsync(run, guard, sessionId, step, stepsThisRun, started.Sequence, terminal).ConfigureAwait(false);
    }

    /// <summary>Drains one step's stream to its terminal, mapping parks onto the session status as they happen.</summary>
    private async Task<string> DrainStepAsync(INodeChatStreamService stream, StepCancellationGuard guard, NodeChatStreamRequest request, Guid sessionId)
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
                    return streamEvent.Type;

                default:
                    break;
            }
        }

        // The stream ended without a terminal event. Read that as a failure rather than looping: the assistant row was
        // terminalized by the pump either way, and a second step would go out over an unknown state.
        return ChatStreamEventTypes.AssistantFailed;
    }

    private async Task<StepOutcome> SettleStepAsync(SessionRun run,
        StepCancellationGuard guard,
        Guid sessionId,
        int step,
        int stepsThisRun,
        long stepStartedSequence,
        string terminal)
    {
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
                                step.ToString(CultureInfo.InvariantCulture)),
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
            var settled = await WithStoreAsync(store => store.TransitionStatusAsync(
                        new TransitionWorkSessionStatusCommand(sessionId, WorkSessionVersions.Any, target, CurrentTaskId: null, reason),
                        CancellationToken.None))
                .ConfigureAwait(false);
            await _publisher.PublishAsync(sessionId, settled.LastSequence, WorkSessionChangeKind.Status, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WorkSessionInvalidTransitionException exception)
        {
            _logger.LogWarning(exception, "Work session {SessionId} was already past {Status} when the step ended.", sessionId, target);
        }
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

    private sealed class SessionRun(CancellationTokenSource cancellation)
    {
        private NodeChatMessageCorrelation? _correlation;
        private int _stopReason = -1;

        public CancellationTokenSource Cancellation { get; } = cancellation;

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
