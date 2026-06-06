namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows.Implementation;

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Singleton in-memory run-state machine for Open Canvas (Preview) runs (plan §7.3). Owns a
///     <see cref="ConcurrentDictionary{TKey,TValue}" /> registry of <see cref="PreviewWorkflowRunHandle" />s keyed by
///     runId; resolves a NODE-LOCAL <see cref="IChatClient" /> per run via <see cref="ILocalModelProvider" /> (invariant
///     #1 — it injects ONLY the local provider; there is no path here to a shared/cloud <see cref="IChatClient" /> or an
///     Azure Foundry factory); drains the Lane B runner in a background task and republishes every update over the hub
///     stamped with the runId (the runId on every event is the real cross-run isolation guard).
/// </summary>
internal sealed class PreviewWorkflowExecutionService : IPreviewWorkflowExecutionService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, PreviewWorkflowRunHandle> _runs = new();
    private readonly ConcurrentDictionary<Guid, Task> _drainTasks = new();

    private readonly ILocalModelProvider _localModelProvider;
    private readonly IPreviewWorkflowRunner _runner;
    private readonly IPreviewWorkflowEventPublisher _eventPublisher;
    private readonly PreviewWorkflowExecutionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PreviewWorkflowExecutionService> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();

    public PreviewWorkflowExecutionService(ILocalModelProvider localModelProvider,
        IPreviewWorkflowRunner runner,
        IPreviewWorkflowEventPublisher eventPublisher,
        IOptions<PreviewWorkflowExecutionOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        _localModelProvider = localModelProvider ?? throw new ArgumentNullException(nameof(localModelProvider));
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

    public async Task<Guid> StartAsync(PreviewWorkflowGraph graph, string? connectionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var validation = PreviewWorkflowGraphValidator.Validate(graph);
        if (!validation.IsValid)
        {
            throw new PreviewWorkflowValidationException(validation);
        }

        if (_runs.Count >= _options.MaxConcurrentRuns)
        {
            throw new PreviewWorkflowCapReachedException(_options.MaxConcurrentRuns);
        }

        var runId = Guid.NewGuid();

        // Resolve a NODE-LOCAL chat client per DISTINCT model used by the graph's agent nodes (invariant #1 — only the
        // local provider; there is no path here to a shared/cloud client or an Azure Foundry factory). Each agent runs
        // on its OWN selected model: agents sharing a model share one client (OllamaApiClient is cheap, but building N
        // identical clients for the same model is pointless), while agents on different models each get their own.
        // CreateChatClient does NO health check — a model-down/not-installed failure surfaces on the first model call
        // inside the drain loop and becomes preview.node.failed + preview.run.failed there.
        var clientsByModel = CreateClientsPerDistinctModel(graph);

        PreviewWorkflowRunHandle handle;
        try
        {
            var definition = PreviewWorkflowGraphMapper.ToDefinition(graph);
            var session = await _runner.StartAsync(definition, ResolveClient, cancellationToken).ConfigureAwait(false);
            handle = new PreviewWorkflowRunHandle(runId,
                clientsByModel.Values,
                session,
                connectionId,
                _timeProvider,
                _loggerFactory.CreateLogger<PreviewWorkflowRunHandle>());
        }
        catch
        {
            // The session never came up — dispose every client we created so none leak.
            foreach (var client in clientsByModel.Values)
            {
                client.Dispose();
            }

            throw;
        }

        IChatClient ResolveClient(string modelId)
        {
            return clientsByModel.TryGetValue(modelId, out var client)
                ? client
                : throw new InvalidOperationException(
                    $"No node-local chat client was created for model '{modelId}'.");
        }

        if (!_runs.TryAdd(runId, handle))
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"A preview run with id '{runId}' already exists.");
        }

        await PublishRunAsync(PreviewWorkflowHubEvents.RunStarted, runId, cancellationToken).ConfigureAwait(false);

        // Observe the drain task's faults (no swallowed UnobservedTaskException): the loop itself converts exceptions to
        // preview.run.failed, and the continuation logs anything that still escapes.
        var drainTask = Task.Run(() => DrainAsync(handle), CancellationToken.None);
        _drainTasks[runId] = drainTask;
        _ = drainTask.ContinueWith(
            (t, state) =>
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
            handle.SetState(PreviewRunState.Running);
            handle.ResetIdleClock();

            await handle.Session.ResumeAsync(requestId, handle.CancellationTokenSource.Token).ConfigureAwait(false);

            // Re-pump the resumed run on a fresh drain task; the prior drain ended when the run paused.
            var drainTask = Task.Run(() => DrainAsync(handle), CancellationToken.None);
            _drainTasks[runId] = drainTask;
            _ = drainTask.ContinueWith(
                (t, state) =>
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

    /// <summary>Idle/wall-clock sweep step, invoked by <see cref="PreviewWorkflowIdleSweeper" />.</summary>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        foreach (var handle in _runs.Values)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // A Paused run's idle clock is suspended (IsIdleExpired returns false) and it is exempt from the wall-clock
            // cap — so it is never swept while waiting on a human Continue (findings item 6).
            if (handle.IsIdleExpired(_options.IdleTimeout) || handle.IsOverWallClock(_options.MaxRunDuration))
            {
                _logger.LogInformation("Sweeping idle/expired preview run {RunId}.", handle.RunId);
                _ = await CancelAsync(handle.RunId, cancellationToken).ConfigureAwait(false);
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
            error: "Output limit exceeded.", requestId: null, cancellationToken).ConfigureAwait(false);

        await RemoveAndDisposeAsync(handle).ConfigureAwait(false);
        return true;
    }

    private async Task PauseRunAsync(PreviewWorkflowRunHandle handle, PreviewWorkflowUpdate update, CancellationToken cancellationToken)
    {
        handle.PendingRequestId = update.RequestId;
        handle.SetState(PreviewRunState.Paused);
        // Suspend the idle clock so the sweeper does not kill a run waiting on a human Continue (findings item 6).
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
            await handle.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Creates one node-local <see cref="IChatClient" /> per DISTINCT agent model in the graph, keyed by model id.
    ///     Validation guarantees at least one Agent node; an Agent node without a model is rejected upstream.
    /// </summary>
    private Dictionary<string, IChatClient> CreateClientsPerDistinctModel(PreviewWorkflowGraph graph)
    {
        var clientsByModel = new Dictionary<string, IChatClient>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            if (node.Kind != PreviewWorkflowNodeKind.Agent)
            {
                continue;
            }

            var modelId = node.Model
                          ?? throw new InvalidOperationException(
                              $"Agent node '{node.Id}' has no node-local model to resolve a chat client from.");

            if (clientsByModel.ContainsKey(modelId))
            {
                continue;
            }

            var selection = new LocalModelSelection
            {
                ModelName = modelId,
                ProviderName = _localModelProvider.ProviderName
            };
            clientsByModel[modelId] = _localModelProvider.CreateChatClient(selection);
        }

        if (clientsByModel.Count == 0)
        {
            throw new InvalidOperationException("The workflow has no Agent node to resolve a node-local model from.");
        }

        return clientsByModel;
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
        var runEvent = new PreviewWorkflowRunHubEvent(eventType, runId, nodeId, output, error, requestId,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        return SafePublishAsync(() => _eventPublisher.PublishRunAsync(runEvent, cancellationToken), runId);
    }

    private Task PublishNodeAsync(string eventType,
        Guid runId,
        string nodeId,
        string? output,
        string? error,
        CancellationToken cancellationToken)
    {
        var nodeEvent = new PreviewWorkflowNodeHubEvent(eventType, runId, nodeId, output, error,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        return SafePublishAsync(() => _eventPublisher.PublishNodeAsync(nodeEvent, cancellationToken), runId);
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
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
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

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
    }

    private enum DrainStep
    {
        Continue,
        Paused,
        Terminal
    }
}
