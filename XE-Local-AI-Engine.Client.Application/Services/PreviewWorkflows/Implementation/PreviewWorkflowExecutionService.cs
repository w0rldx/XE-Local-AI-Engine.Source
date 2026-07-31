namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows.Implementation;

using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Singleton in-memory run-state machine for Open Canvas (Preview) runs. Owns a
///     <see cref="ConcurrentDictionary{TKey,TValue}" /> registry of <see cref="PreviewWorkflowRunHandle" />s keyed by
///     runId; resolves a NODE-LOCAL <see cref="IChatClient" /> per run via <see cref="ILocalModelProvider" /> (privacy
///     invariant — it injects ONLY the local provider; there is no path here to a shared/cloud <see cref="IChatClient" />
///     or an Azure Foundry factory); drains the .AI.Agent runner in a background task and republishes every update over the hub
///     stamped with the runId (the runId on every event is the real cross-run isolation guard).
/// </summary>
internal sealed class PreviewWorkflowExecutionService : IPreviewWorkflowExecutionService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Task> _drainTasks = new();

    // Per-run ordered event log for late-subscriber replay, SEPARATE from _runs because it must OUTLIVE the run: the
    // whole bug is a client that subscribes AFTER the run finished, so the terminal event must still be replayable. A
    // log is created in StartAsync (so RunStarted is buffered as seq 0), lingers past RemoveAndDisposeAsync, and is
    // evicted by SweepAsync once ReplayRetention elapses past its terminal event.
    private readonly ConcurrentDictionary<Guid, RunEventLog> _eventLogs = new();
    private readonly IPreviewWorkflowEventPublisher _eventPublisher;
    private readonly ILogger<PreviewWorkflowExecutionService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PreviewWorkflowExecutionOptions _options;

    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IPreviewWorkflowRunner _runner;
    private readonly ConcurrentDictionary<Guid, PreviewWorkflowRunHandle> _runs = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly TimeProvider _timeProvider;

    // Authoritative concurrent-run count: reserved-but-not-yet-added slots PLUS live runs in _runs. A slot is reserved
    // atomically at the very top of StartAsync (before any await) and released either on a failure path or when the run
    // leaves _runs, so a burst of concurrent starts can never exceed MaxConcurrentRuns the way a _runs.Count check
    // followed by async setup and a late TryAdd could.
    private int _reservedRunCount;

    public PreviewWorkflowExecutionService(ILocalModelProviderResolver providerResolver,
        IPreviewWorkflowRunner runner,
        IPreviewWorkflowEventPublisher eventPublisher,
        IOptions<PreviewWorkflowExecutionOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<PreviewWorkflowExecutionService>();

        // Host shutdown cancels + disposes every in-flight run so a node restart never leaves a run hanging.
        applicationLifetime?.ApplicationStopping.Register(() =>
        {
            try
            {
                ShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Ignoring error while cancelling in-flight preview runs at shutdown.");
            }
        });
    }

    internal IReadOnlyCollection<Guid> ActiveRunIds => [.. _runs.Keys];

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
    }

    public async Task<Guid> StartAsync(PreviewWorkflowGraph graph, string? connectionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var validation = PreviewWorkflowGraphValidator.Validate(graph);
        if (!validation.IsValid)
        {
            throw new PreviewWorkflowValidationException(validation);
        }

        // Reserve a concurrency slot ATOMICALLY, before any async setup. The old sequence — a _runs.Count check, then
        // an async provider-resolve + runner.StartAsync, then a late _runs.TryAdd — let several concurrent starts all
        // observe an under-cap count and then all add, exceeding MaxConcurrentRuns. The reservation is released on every
        // failure path below (finally) and, once the run is live in _runs, when it leaves (RemoveAndDisposeAsync /
        // ShutdownAsync).
        if (!TryReserveRunSlot())
        {
            throw new PreviewWorkflowCapReachedException(_options.MaxConcurrentRuns);
        }

        var reservationCommitted = false;
        try
        {
            var runId = Guid.NewGuid();

            // Resolve the PROVIDER for each DISTINCT agent model up front (the model→provider map read is async; client/process
            // creation stays lazy). This also enforces the loaded-cap reject-at-start: if the
            // graph's distinct-model count exceeds the supervisor cap, throw NOW — before spawning any process — rather
            // than evict-reload thrashing mid-run.
            var providersByModel = await ResolveProvidersPerDistinctModelAsync(graph, cancellationToken).ConfigureAwait(false);

            // One lazily-created NODE-LOCAL chat client per distinct model (privacy invariant — only the local provider; no path
            // here to a shared/cloud client or an Azure Foundry factory). The client (and, for llama-server, its backing
            // process) is created on FIRST use inside the drain loop, not up front: a deferred llama-server client spawns
            // its process on the first model call; an Ollama client is cheap. Agents sharing a model share one client.
            // CreateChatClient does NO health check — a model-down/not-installed failure surfaces on the first model call
            // inside the drain loop and becomes preview.node.failed + preview.run.failed there.
            var clientsByModel = new ConcurrentDictionary<string, IChatClient>(StringComparer.Ordinal);

            PreviewWorkflowRunHandle handle;
            try
            {
                var definition = PreviewWorkflowGraphMapper.ToDefinition(graph);
                var session = await _runner.StartAsync(definition, ResolveClient, cancellationToken).ConfigureAwait(false);
                handle = new PreviewWorkflowRunHandle(runId,
                    new LiveClientCollection(clientsByModel),
                    session,
                    connectionId,
                    _timeProvider,
                    _loggerFactory.CreateLogger<PreviewWorkflowRunHandle>());
            }
            catch
            {
                // The session never came up — dispose every client we lazily created so none leak.
                foreach (var client in clientsByModel.Values)
                {
                    client.Dispose();
                }

                throw;
            }

            IChatClient ResolveClient(string modelId)
            {
                // Lazy per-model resolution: build the client (and, for llama-server, ensure-run its process) on first use,
                // then cache it for the run so agents sharing a model share one client. The provider was resolved up front
                // within the cap; an unexpected model id (not in the validated graph) is a defensive error.
                return clientsByModel.GetOrAdd(modelId, id =>
                {
                    if (!providersByModel.TryGetValue(id, out var provider))
                    {
                        throw new InvalidOperationException($"No node-local provider was resolved for model '{id}'.");
                    }

                    return provider.CreateChatClient(new LocalModelSelection
                    {
                        ModelName = id,
                        ProviderName = provider.ProviderName
                    });
                });
            }

            if (!_runs.TryAdd(runId, handle))
            {
                await handle.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"A preview run with id '{runId}' already exists.");
            }

            // The reservation now belongs to the live run: it is released when the run leaves _runs, not by the finally.
            reservationCommitted = true;

            // Create the replay log BEFORE the first publish so RunStarted is buffered as seq 0.
            _ = GetOrCreateEventLog(runId);

            await PublishRunAsync(PreviewWorkflowHubEvents.RunStarted, runId, cancellationToken).ConfigureAwait(false);

            // Observe the drain task's faults (no swallowed UnobservedTaskException): the loop itself converts exceptions to
            // preview.run.failed, and the continuation logs anything that still escapes.
            var drainTask = Task.Run(() => DrainAsync(handle), CancellationToken.None);
            _drainTasks[runId] = drainTask;
            _ = drainTask.ContinueWith((t, state) =>
                {
                    var (svc, id) = ((PreviewWorkflowExecutionService Service, Guid RunId))state!;
                    svc._drainTasks.TryRemove(id, out _);
                    if (t.IsFaulted)
                    {
                        svc._logger.LogError(t.Exception, "Preview run {RunId} drain task faulted unexpectedly.", id);
                    }
                },
                (Service: this, RunId: runId),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return runId;
        }
        finally
        {
            if (!reservationCommitted)
            {
                ReleaseRunSlot();
            }
        }
    }

    // Atomic compare-and-increment of the concurrent-run reservation against MaxConcurrentRuns. Returns false (the
    // caller rejects with PreviewWorkflowCapReachedException) when the cap is already taken, so concurrent starts
    // cannot both claim the last slot.
    private bool TryReserveRunSlot()
    {
        var cap = _options.MaxConcurrentRuns;
        while (true)
        {
            var current = Volatile.Read(ref _reservedRunCount);
            if (current >= cap)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _reservedRunCount, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private void ReleaseRunSlot()
    {
        Interlocked.Decrement(ref _reservedRunCount);
    }

    public async Task<PreviewRunCommandOutcome> ContinueAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out var handle))
        {
            return PreviewRunCommandOutcome.NotFound;
        }

        // Serialize continue/cancel through the per-run gate so they never race on the held session.
        if (!await handle.TryEnterCommandGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return PreviewRunCommandOutcome.NotFound;
        }

        try
        {
            if (handle.GetState() != PreviewRunState.Paused || handle.PendingRequestId is null)
            {
                return PreviewRunCommandOutcome.WrongState;
            }

            var requestId = handle.PendingRequestId;
            handle.PendingRequestId = null;
            handle.PausedNodeId = null;
            handle.SetState(PreviewRunState.Running);
            handle.ResetIdleClock();

            await handle.Session.ResumeAsync(requestId, handle.CancellationTokenSource.Token).ConfigureAwait(false);

            // Re-pump the resumed run on a fresh drain task; the prior drain ended when the run paused.
            var drainTask = Task.Run(() => DrainAsync(handle), CancellationToken.None);
            _drainTasks[runId] = drainTask;
            _ = drainTask.ContinueWith((t, state) =>
                {
                    var (svc, id) = ((PreviewWorkflowExecutionService Service, Guid RunId))state!;
                    svc._drainTasks.TryRemove(id, out _);
                    if (t.IsFaulted)
                    {
                        svc._logger.LogError(t.Exception, "Preview run {RunId} resume drain task faulted unexpectedly.", id);
                    }
                },
                (Service: this, RunId: runId),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return PreviewRunCommandOutcome.Accepted;
        }
        finally
        {
            handle.ExitCommandGate();
        }
    }

    public async Task<PreviewRunCommandOutcome> CancelAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out var handle))
        {
            // Idempotent: cancelling an unknown/already-removed run is not an error.
            return PreviewRunCommandOutcome.NotFound;
        }

        if (!await handle.TryEnterCommandGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return PreviewRunCommandOutcome.Accepted;
        }

        try
        {
            // Cancel works in Running AND Paused. Idempotent for terminal states.
            var state = handle.GetState();
            if (state is PreviewRunState.Cancelled or PreviewRunState.Completed or PreviewRunState.Faulted)
            {
                return PreviewRunCommandOutcome.Accepted;
            }

            var wasPaused = state == PreviewRunState.Paused;
            handle.SetState(PreviewRunState.Cancelled);
            await CancelTokenSourceAsync(handle).ConfigureAwait(false);

            // Publish the terminal cancelled event NOW so the UI reflects the cancel immediately on the 202. A Running
            // run's drain may be blocked in a model call that observes the cancelled token only slowly (or never), so
            // relying on the drain to publish leaves the UI stuck "running". This is the single authoritative publish
            // for both the Running and Paused paths.
            await PublishRunAsync(PreviewWorkflowHubEvents.RunCancelled, handle.RunId, nodeId: null, output: null,
                error: null, requestId: null, CancellationToken.None).ConfigureAwait(false);

            if (wasPaused)
            {
                // A Paused run has NO active drain observing the CTS (the drain ended when the run paused) — dispose
                // here. A Running run's drain observes the cancelled token, unwinds, and disposes itself (no re-publish).
                await RemoveAndDisposeAsync(handle).ConfigureAwait(false);
            }

            return PreviewRunCommandOutcome.Accepted;
        }
        finally
        {
            handle.ExitCommandGate();
        }
    }

    public async Task CancelRunsForConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        var owned = _runs.Values
                         .Where(h => string.Equals(h.ConnectionId, connectionId, StringComparison.Ordinal))
                         .Select(h => h.RunId)
                         .ToList();

        foreach (var runId in owned)
        {
            _ = await CancelAsync(runId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<int> CancelAllAsync(CancellationToken cancellationToken = default)
    {
        var cancelled = 0;
        foreach (var runId in _runs.Keys.ToList())
        {
            if (await CancelAsync(runId, cancellationToken).ConfigureAwait(false) == PreviewRunCommandOutcome.Accepted)
            {
                cancelled++;
            }
        }

        return cancelled;
    }

    public IReadOnlyList<PreviewRunSnapshot> ListRuns()
    {
        var snapshots = new List<PreviewRunSnapshot>();

        foreach (var handle in _runs.Values)
        {
            snapshots.Add(ToSnapshot(handle));
        }

        // Retained (terminal but still replayable) runs are listed too: a client that reloads AFTER a run finished
        // must be able to find it and recover the result it already paid GPU time for.
        foreach (var (runId, log) in _eventLogs)
        {
            if (!_runs.ContainsKey(runId))
            {
                snapshots.Add(ToRetainedSnapshot(runId, log));
            }
        }

        return [.. snapshots.OrderBy(static s => s.StartedAtUtc)];
    }

    public PreviewRunSnapshot? GetRun(Guid runId)
    {
        if (_runs.TryGetValue(runId, out var handle))
        {
            return ToSnapshot(handle);
        }

        return _eventLogs.TryGetValue(runId, out var log) ? ToRetainedSnapshot(runId, log) : null;
    }

    private PreviewRunSnapshot ToSnapshot(PreviewWorkflowRunHandle handle)
    {
        // The seq counter lives on the replay log (it outlives the handle), so read it from there; -1 means "nothing
        // buffered yet", which is exactly the afterSeq a client with no history sends.
        var lastSeq = _eventLogs.TryGetValue(handle.RunId, out var log) ? log.LastSeq : -1L;

        return new PreviewRunSnapshot(handle.RunId,
            handle.GetState(),
            IsLive: true,
            handle.StartedAtUtc.ToUnixTimeMilliseconds(),
            lastSeq,
            handle.SubscriberCount,
            handle.PausedNodeId,
            handle.PendingRequestId);
    }

    /// <summary>
    ///     A run that has left <c>_runs</c> but whose replay log is still retained. Its state is recovered from the
    ///     log's terminal event type (the log outlives the handle, so the handle's state is gone by then).
    /// </summary>
    private static PreviewRunSnapshot ToRetainedSnapshot(Guid runId, RunEventLog log)
    {
        var state = log.TerminalEventType switch
        {
            PreviewWorkflowHubEvents.RunCompleted => PreviewRunState.Completed,
            PreviewWorkflowHubEvents.RunFailed => PreviewRunState.Faulted,
            PreviewWorkflowHubEvents.RunCancelled => PreviewRunState.Cancelled,
            // No terminal event buffered and no live handle: the run was torn down without publishing a terminal
            // event (host shutdown). Report it as cancelled — it is definitively not running.
            _ => PreviewRunState.Cancelled
        };

        return new PreviewRunSnapshot(runId,
            state,
            IsLive: false,
            log.CreatedAtUnixMs,
            log.LastSeq,
            SubscriberCount: 0,
            PausedNodeId: null,
            PauseRequestId: null);
    }

    public IReadOnlyList<PreviewWorkflowBufferedEvent> SnapshotBufferedEvents(Guid runId, long afterSeq)
    {
        return _eventLogs.TryGetValue(runId, out var log) ? log.Snapshot(afterSeq) : [];
    }

    public void AddSubscriber(Guid runId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (_runs.TryGetValue(runId, out var handle))
        {
            handle.AddSubscriber(connectionId);
        }
    }

    public void RemoveSubscriber(Guid runId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (_runs.TryGetValue(runId, out var handle))
        {
            handle.RemoveSubscriber(connectionId);
        }
    }

    public void RemoveSubscriberFromAllRuns(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        foreach (var handle in _runs.Values)
        {
            handle.RemoveSubscriber(connectionId);
        }
    }

    /// <summary>Idle/wall-clock/abandoned sweep step, invoked by <see cref="PreviewWorkflowIdleSweeper" />.</summary>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        foreach (var handle in _runs.Values)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // A Paused run's idle clock is suspended (IsIdleExpired returns false) and it is exempt from the wall-clock
            // cap — so it is never swept while waiting on a human Continue. That exemption is deliberate and stays.
            // IsAbandonedPast is the separate, narrower condition it does NOT escape: no hub connection has been
            // watching the run for the whole grace period, so there is no human left to press Continue and the run
            // would otherwise hold its concurrency slot until the node restarts (the reload-leak).
            var abandoned = handle.IsAbandonedPast(_options.AbandonedSubscriberGrace);
            if (abandoned || handle.IsIdleExpired(_options.IdleTimeout) || handle.IsOverWallClock(_options.MaxRunDuration))
            {
                _logger.LogInformation("Sweeping preview run {RunId} (abandoned: {Abandoned}).", handle.RunId, abandoned);
                _ = await CancelAsync(handle.RunId, cancellationToken).ConfigureAwait(false);
            }
        }

        EvictExpiredEventLogs();
    }

    /// <summary>
    ///     Evicts replay logs that have outlived their usefulness: a terminal log once <see cref="PreviewWorkflowExecutionOptions.ReplayRetention" />
    ///     has elapsed past its terminal event, plus a defensive sweep of any log whose run is gone that never got a
    ///     terminal event yet is older than <see cref="PreviewWorkflowExecutionOptions.MaxRunDuration" /> (guards a leak).
    /// </summary>
    private void EvictExpiredEventLogs()
    {
        var nowUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var retentionMs = (long)_options.ReplayRetention.TotalMilliseconds;
        var maxRunMs = (long)_options.MaxRunDuration.TotalMilliseconds;

        foreach (var (runId, log) in _eventLogs)
        {
            var terminalAt = log.TerminalAtUnixMs;
            var expiredTerminal = terminalAt is { } terminal && nowUnixMs - terminal >= retentionMs;
            var orphanedNonTerminal = terminalAt is null
                                      && !_runs.ContainsKey(runId)
                                      && nowUnixMs - log.CreatedAtUnixMs >= maxRunMs;

            if (expiredTerminal || orphanedNonTerminal)
            {
                _ = _eventLogs.TryRemove(runId, out _);
            }
        }
    }

    private async Task DrainAsync(PreviewWorkflowRunHandle handle)
    {
        var runId = handle.RunId;
        var cancellationToken = handle.CancellationTokenSource.Token;

        try
        {
            await foreach (var update in handle.Session.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                handle.ResetIdleClock();

                if (await TryEnforceByteCapAsync(handle, update, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                var handled = await ProcessUpdateAsync(handle, update, cancellationToken).ConfigureAwait(false);
                if (handled == DrainStep.Paused || handled == DrainStep.Terminal)
                {
                    return;
                }
            }

            // The stream ended without a terminal RunCompleted/RunFailed/RunPaused update (defensive — the runner emits
            // a terminal update). Treat a clean end as completion only if still Running.
            if (handle.GetState() == PreviewRunState.Running)
            {
                await CompleteRunAsync(handle, output: null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (handle.GetState() == PreviewRunState.Cancelled)
        {
            // CancelAsync already set Cancelled and published RunCancelled; the drain just disposes once it unwinds.
            await RemoveAndDisposeAsync(handle).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Preview run {RunId} failed during drain.", runId);
            await FailRunAsync(handle, "The preview run failed.", nodeId: null).ConfigureAwait(false);
        }
    }

    private async Task<DrainStep> ProcessUpdateAsync(PreviewWorkflowRunHandle handle,
        PreviewWorkflowUpdate update,
        CancellationToken cancellationToken)
    {
        var runId = handle.RunId;

        switch (update.Kind)
        {
            case PreviewWorkflowUpdateKind.NodeStarted:
                await PublishNodeAsync(PreviewWorkflowHubEvents.NodeStarted, runId, update.NodeId!, output: null, error: null, cancellationToken).ConfigureAwait(false);
                return DrainStep.Continue;

            case PreviewWorkflowUpdateKind.NodeOutput:
                await PublishNodeAsync(PreviewWorkflowHubEvents.NodeOutput, runId, update.NodeId!, update.Output, error: null, cancellationToken).ConfigureAwait(false);
                await PublishNodeAsync(PreviewWorkflowHubEvents.NodeCompleted, runId, update.NodeId!, output: null, error: null, cancellationToken).ConfigureAwait(false);
                return DrainStep.Continue;

            case PreviewWorkflowUpdateKind.NodeDebug:
                await PublishNodeAsync(PreviewWorkflowHubEvents.NodeDebug, runId, update.NodeId!, update.Output, error: null, cancellationToken).ConfigureAwait(false);
                return DrainStep.Continue;

            case PreviewWorkflowUpdateKind.NodeFailed:
                await FailRunAsync(handle, update.Error ?? "A preview node failed.", update.NodeId).ConfigureAwait(false);
                return DrainStep.Terminal;

            case PreviewWorkflowUpdateKind.RunPaused:
                await PauseRunAsync(handle, update, cancellationToken).ConfigureAwait(false);
                return DrainStep.Paused;

            case PreviewWorkflowUpdateKind.RunCompleted:
                await CompleteRunAsync(handle, update.Output, cancellationToken).ConfigureAwait(false);
                return DrainStep.Terminal;

            case PreviewWorkflowUpdateKind.RunFailed:
                await FailRunAsync(handle, update.Error ?? "The preview run failed.", nodeId: null).ConfigureAwait(false);
                return DrainStep.Terminal;

            default:
                _logger.LogDebug("Ignoring unknown preview update kind {Kind} for run {RunId}.", update.Kind, runId);
                return DrainStep.Continue;
        }
    }

    private async Task<bool> TryEnforceByteCapAsync(PreviewWorkflowRunHandle handle,
        PreviewWorkflowUpdate update,
        CancellationToken cancellationToken)
    {
        if (update.Output is not { Length: > 0 } output)
        {
            return false;
        }

        var byteCount = Encoding.UTF8.GetByteCount(output);
        if (!handle.AddOutputBytesAndCheckCap(byteCount, _options.MaxOutputBytes))
        {
            return false;
        }

        _logger.LogWarning("Preview run {RunId} exceeded the output byte cap; cancelling.", handle.RunId);

        // Cancel the held run, mark failed, and emit the terminal failure. Cancellation unblocks the session.
        handle.SetState(PreviewRunState.Faulted);
        await CancelTokenSourceAsync(handle).ConfigureAwait(false);

        await PublishRunAsync(PreviewWorkflowHubEvents.RunFailed, handle.RunId, nodeId: null, output: null,
            "Output limit exceeded.", requestId: null, cancellationToken).ConfigureAwait(false);

        await RemoveAndDisposeAsync(handle).ConfigureAwait(false);
        return true;
    }

    private async Task PauseRunAsync(PreviewWorkflowRunHandle handle, PreviewWorkflowUpdate update, CancellationToken cancellationToken)
    {
        handle.PendingRequestId = update.RequestId;
        handle.PausedNodeId = update.NodeId;
        handle.SetState(PreviewRunState.Paused);
        // Suspend the idle clock so the sweeper does not kill a run waiting on a human Continue.
        handle.SuspendIdleClock();

        await PublishRunAsync(PreviewWorkflowHubEvents.RunPaused, handle.RunId, update.NodeId, update.Output,
            error: null, update.RequestId, cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteRunAsync(PreviewWorkflowRunHandle handle, string? output, CancellationToken cancellationToken)
    {
        handle.SetState(PreviewRunState.Completed);
        await PublishRunAsync(PreviewWorkflowHubEvents.RunCompleted, handle.RunId, nodeId: null, output, error: null,
            requestId: null, cancellationToken).ConfigureAwait(false);
        await RemoveAndDisposeAsync(handle).ConfigureAwait(false);
    }

    private async Task FailRunAsync(PreviewWorkflowRunHandle handle, string error, string? nodeId)
    {
        if (nodeId is not null)
        {
            await PublishNodeAsync(PreviewWorkflowHubEvents.NodeFailed, handle.RunId, nodeId, output: null, error,
                CancellationToken.None).ConfigureAwait(false);
        }

        handle.SetState(PreviewRunState.Faulted);
        await PublishRunAsync(PreviewWorkflowHubEvents.RunFailed, handle.RunId, nodeId: null, output: null, error,
            requestId: null, CancellationToken.None).ConfigureAwait(false);
        await RemoveAndDisposeAsync(handle).ConfigureAwait(false);
    }

    private async Task RemoveAndDisposeAsync(PreviewWorkflowRunHandle handle)
    {
        if (_runs.TryRemove(handle.RunId, out _))
        {
            // The run leaving _runs frees its reserved concurrency slot for the next start.
            ReleaseRunSlot();
            await handle.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Resolves the node-local PROVIDER for each DISTINCT agent model in the graph (keyed by model id), and enforces
    ///     the loaded-process cap reject-at-start: if the distinct-model count exceeds the
    ///     supervisor cap, throws BEFORE any client/process is created. Provider resolution reads the model→provider map (async)
    ///     but starts no process — that is deferred to first use. Validation guarantees at least one Agent node; an
    ///     Agent node without a model is rejected upstream.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ILocalModelProvider>> ResolveProvidersPerDistinctModelAsync(PreviewWorkflowGraph graph,
        CancellationToken cancellationToken)
    {
        var distinctModels = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            if (node.Kind != PreviewWorkflowNodeKind.Agent)
            {
                continue;
            }

            var modelId = node.Model
                          ?? throw new InvalidOperationException($"Agent node '{node.Id}' has no node-local model to resolve a chat client from.");

            if (seen.Add(modelId))
            {
                distinctModels.Add(modelId);
            }
        }

        if (distinctModels.Count == 0)
        {
            throw new InvalidOperationException("The workflow has no Agent node to resolve a node-local model from.");
        }

        // Reject at start: refuse a graph that would need more concurrent (model) processes than the
        // shared loaded-cap allows, rather than evict-reload thrashing mid-run or doing partial work. Each distinct
        // model is at least one process; embeddings are not used here so distinct-model count is the relevant bound.
        var cap = _providerResolver.MaxLoadedProcesses;
        if (distinctModels.Count > cap)
        {
            throw new PreviewWorkflowModelCapExceededException(distinctModels.Count, cap);
        }

        var providersByModel = new Dictionary<string, ILocalModelProvider>(StringComparer.Ordinal);
        foreach (var modelId in distinctModels)
        {
            providersByModel[modelId] = await _providerResolver
                                              .ResolveProviderForModelAsync(modelId, cancellationToken)
                                              .ConfigureAwait(false);
        }

        return providersByModel;
    }

    private Task PublishRunAsync(string eventType, Guid runId, CancellationToken cancellationToken)
    {
        return PublishRunAsync(eventType, runId, nodeId: null, output: null, error: null, requestId: null, cancellationToken);
    }

    private Task PublishRunAsync(string eventType,
        Guid runId,
        string? nodeId,
        string? output,
        string? error,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var log = GetOrCreateEventLog(runId);
        var nowUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var isTerminal = IsTerminalRunEvent(eventType);

        // Assign the next seq, stamp the event with it, and append to the replay log atomically — then publish the
        // seq-stamped event as before. A terminal run event marks the log for later eviction.
        var runEvent = (PreviewWorkflowRunHubEvent)log.Append(eventType,
            seq => new PreviewWorkflowRunHubEvent(eventType, runId, nodeId, output, error, requestId, nowUnixMs, seq),
            isTerminal,
            nowUnixMs,
            out var truncated);

        if (truncated)
        {
            _logger.LogWarning("Preview run {RunId} replay buffer exceeded {Cap}; dropping oldest events.",
                runId, _options.MaxBufferedEventsPerRun);
        }

        return SafePublishAsync(() => _eventPublisher.PublishRunAsync(runEvent, cancellationToken), runId);
    }

    private Task PublishNodeAsync(string eventType,
        Guid runId,
        string nodeId,
        string? output,
        string? error,
        CancellationToken cancellationToken)
    {
        var log = GetOrCreateEventLog(runId);
        var nowUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var nodeEvent = (PreviewWorkflowNodeHubEvent)log.Append(eventType,
            seq => new PreviewWorkflowNodeHubEvent(eventType, runId, nodeId, output, error, nowUnixMs, seq),
            isTerminal: false,
            terminalAtUnixMs: null,
            out var truncated);

        if (truncated)
        {
            _logger.LogWarning("Preview run {RunId} replay buffer exceeded {Cap}; dropping oldest events.",
                runId, _options.MaxBufferedEventsPerRun);
        }

        return SafePublishAsync(() => _eventPublisher.PublishNodeAsync(nodeEvent, cancellationToken), runId);
    }

    /// <summary>Returns the run's replay log, creating it on first use so a publish never NREs.</summary>
    private RunEventLog GetOrCreateEventLog(Guid runId)
    {
        return _eventLogs.GetOrAdd(runId,
            _ => new RunEventLog(_options.MaxBufferedEventsPerRun, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    /// <summary>A terminal run event (completed/failed/cancelled) marks the log for eviction after ReplayRetention.</summary>
    private static bool IsTerminalRunEvent(string eventType)
    {
        return eventType is PreviewWorkflowHubEvents.RunCompleted
            or PreviewWorkflowHubEvents.RunFailed
            or PreviewWorkflowHubEvents.RunCancelled;
    }

    private async Task SafePublishAsync(Func<Task> publish, Guid runId)
    {
        try
        {
            await publish().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Best-effort: a hub publish failure must not abort the run drain.
            _logger.LogDebug(exception, "Ignoring error while publishing a preview event for run {RunId}.", runId);
        }
    }

    private async Task ShutdownAsync()
    {
        if (!_shutdownCts.IsCancellationRequested)
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
        }

        foreach (var handle in _runs.Values)
        {
            await CancelTokenSourceAsync(handle).ConfigureAwait(false);
        }

        foreach (var runId in _runs.Keys.ToList())
        {
            if (_runs.TryRemove(runId, out var handle))
            {
                ReleaseRunSlot();
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }

        // Replay logs outlive their runs; a shutdown must not leave them behind.
        _eventLogs.Clear();
    }

    /// <summary>Fires a run's cancellation token, tolerating a concurrent dispose (no <see cref="ObjectDisposedException" />).</summary>
    private static async Task CancelTokenSourceAsync(PreviewWorkflowRunHandle handle)
    {
        try
        {
            if (!handle.CancellationTokenSource.IsCancellationRequested)
            {
                await handle.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            // The handle was disposed by a concurrent terminal path; nothing more to cancel.
        }
    }

    /// <summary>
    ///     A live read-only view over the run's lazily-populated <see cref="IChatClient" /> cache, so the run handle
    ///     disposes exactly the clients created during the run (the cache grows as agents first touch their models).
    /// </summary>
    private sealed class LiveClientCollection(ConcurrentDictionary<string, IChatClient> clients) : IReadOnlyCollection<IChatClient>
    {
        public int Count => clients.Count;

        public IEnumerator<IChatClient> GetEnumerator()
        {
            return clients.Values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private enum DrainStep
    {
        Continue,
        Paused,
        Terminal
    }

    /// <summary>One buffered event in a run's replay log: the SignalR method name, the seq-stamped payload, and its seq.</summary>
    private sealed record BufferedPreviewEvent(string MethodName, object Payload, long Seq);

    /// <summary>
    ///     A per-run ordered, bounded event log for late-subscriber replay. Seq assignment + append are atomic under a
    ///     lock so concurrent publishes (the drain task and CancelAsync can publish for the same run) never collide on a
    ///     seq or append out of order; <see cref="Snapshot" /> copies under the same lock for a consistent ordered view.
    /// </summary>
    private sealed class RunEventLog(int maxEvents, long createdAtUnixMs)
    {
        private readonly List<BufferedPreviewEvent> _events = [];
        private readonly Lock _gate = new();

        private long _nextSeq;

        public long CreatedAtUnixMs { get; } = createdAtUnixMs;

        /// <summary>Set to the terminal event's timestamp once a terminal run event is buffered; drives eviction.</summary>
        public long? TerminalAtUnixMs { get; private set; }

        /// <summary>The terminal run event's type once one is buffered — the only surviving record of a retained run's outcome.</summary>
        public string? TerminalEventType { get; private set; }

        /// <summary>The highest seq assigned so far, or -1 when nothing has been buffered.</summary>
        public long LastSeq
        {
            get
            {
                lock (_gate)
                {
                    return _nextSeq - 1;
                }
            }
        }

        /// <summary>
        ///     Assigns the next seq, builds the seq-stamped payload via <paramref name="payloadFactory" />, appends it,
        ///     and returns that payload. Drops the oldest entry when the cap is exceeded (setting
        ///     <paramref name="truncated" />). Records the terminal timestamp when <paramref name="isTerminal" /> is set.
        /// </summary>
        public object Append(string methodName,
            Func<long, object> payloadFactory,
            bool isTerminal,
            long? terminalAtUnixMs,
            out bool truncated)
        {
            lock (_gate)
            {
                var seq = _nextSeq++;
                var payload = payloadFactory(seq);
                _events.Add(new BufferedPreviewEvent(methodName, payload, seq));

                truncated = false;
                if (_events.Count > maxEvents)
                {
                    _events.RemoveAt(0);
                    truncated = true;
                }

                if (isTerminal)
                {
                    TerminalAtUnixMs = terminalAtUnixMs;
                    TerminalEventType = methodName;
                }

                return payload;
            }
        }

        /// <summary>
        ///     Ordered copy of the buffered events with <c>Seq</c> strictly greater than <paramref name="afterSeq" />.
        ///     A reattaching client passes the highest seq it has already applied, so a reconnect after a transient
        ///     drop re-sends only the gap instead of the whole log.
        /// </summary>
        public IReadOnlyList<PreviewWorkflowBufferedEvent> Snapshot(long afterSeq)
        {
            lock (_gate)
            {
                var copy = new List<PreviewWorkflowBufferedEvent>(_events.Count);
                foreach (var buffered in _events)
                {
                    if (buffered.Seq > afterSeq)
                    {
                        copy.Add(new PreviewWorkflowBufferedEvent(buffered.MethodName, buffered.Payload));
                    }
                }

                return copy;
            }
        }
    }
}
