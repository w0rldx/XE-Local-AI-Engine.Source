namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     Claims queued durable runs and owns their stop-marker-before-signal lifecycle. Execution tokens are deliberately
///     not linked to the host token: user, watchdog, and host cancellation first win the durable CAS, then signal.
/// </summary>
internal sealed class McpAgentRunDispatcher : BackgroundService
{
    private const string InterruptedCode = "interrupted";
    private const string WatchdogExpiredCode = "watchdog_expired";
    private const string InternalFailureCode = "internal_failure";

    private readonly McpAgentRunCancellationRegistry _cancellations;
    private readonly ILogger<McpAgentRunDispatcher> _logger;
    private readonly McpAgentRunMetrics _metrics;
    private readonly McpAgentRunOptions _options;
    private readonly SemaphoreSlim _claimGate = new(initialCount: 1, maxCount: 1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private int _stopping;

    public McpAgentRunDispatcher(IServiceScopeFactory scopeFactory,
        McpAgentRunCancellationRegistry cancellations,
        McpAgentRunMetrics metrics,
        IOptions<McpAgentRunOptions> options,
        TimeProvider timeProvider,
        ILogger<McpAgentRunDispatcher> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _cancellations = cancellations ?? throw new ArgumentNullException(nameof(cancellations));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _options.MaxConcurrentWorkers)
                                .Select(_ => RunWorkerAsync(stoppingToken))
                                .ToArray();
        return Task.WhenAll(workers);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _stopping, value: 1);
        IReadOnlyList<McpAgentRunCancellationHandle> active;
        await _claimGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            active = _cancellations.BeginShutdown();
        }
        finally
        {
            _claimGate.Release();
        }

        foreach (var handle in active)
        {
            await PersistStopThenSignalAsync(handle,
                McpAgentRunStopReason.HostShutdown,
                "host",
                cancellationToken).ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var registered = await TryClaimAndRegisterNextAsync(stoppingToken).ConfigureAwait(false);
                if (registered is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds),
                        _timeProvider,
                        stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ExecuteClaimAsync(registered.Value.Claimed, registered.Value.ExecutionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                NodeSqliteContention.Record("raw", exception, _logger);
                _logger.LogError(exception, "Durable MCP agent run dispatch iteration failed; the worker will retry.");
                await DelayAfterFailureAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<RegisteredClaim?> TryClaimAndRegisterNextAsync(CancellationToken cancellationToken)
    {
        await _claimGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                return null;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMcpAgentRunStore>();
            var queued = await store.ListAsync(limit: 32, McpAgentRunStatus.Queued, cancellationToken).ConfigureAwait(false);
            foreach (var candidate in queued)
            {
                if (Volatile.Read(ref _stopping) != 0)
                {
                    return null;
                }

                var claimedAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                var result = await store.TryClaimAsync(candidate.RequestId,
                    candidate.Version,
                    claimedAt,
                    cancellationToken).ConfigureAwait(false);
                if (result.Kind != McpAgentRunClaimKind.Claimed || result.Run is not { } claimed)
                {
                    continue;
                }

                await _metrics.RefreshAsync(store, CancellationToken.None).ConfigureAwait(false);
                _metrics.RecordLifecycle("claimed");
                _metrics.RecordClaimAge(claimedAt - claimed.CreatedAtUtc);

                var registration = _cancellations.TryRegister(claimed.RequestId,
                    claimed.ClaimToken!.Value,
                    claimed.Version,
                    out var executionToken);
                if (registration == McpAgentRunRegistrationKind.Duplicate)
                {
                    await FinalizeRegistrationFailureAsync(claimed, claimed.ClaimToken.Value).ConfigureAwait(false);
                    return null;
                }

                if (registration == McpAgentRunRegistrationKind.ShuttingDown)
                {
                    await FinalizeClaimDuringShutdownAsync(claimed, claimed.ClaimToken.Value).ConfigureAwait(false);
                    return null;
                }

                return new RegisteredClaim(claimed, executionToken);
            }

            return null;
        }
        finally
        {
            _claimGate.Release();
        }
    }

    private async Task ExecuteClaimAsync(McpAgentRunRecord claimed, CancellationToken executionToken)
    {
        if (claimed.ClaimToken is not { } claimToken)
        {
            _logger.LogError("Claimed durable MCP agent run had no claim token; it will be reconciled on restart.");
            return;
        }

        using var watchdogCompleted = new CancellationTokenSource();
        var watchdog = RunWatchdogAsync(new McpAgentRunCancellationHandle(claimed.RequestId, claimToken, claimed.Version),
            watchdogCompleted.Token);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMcpAgentRunStore>();

            // A stop may have committed after claim and before the process-local handle existed. Reload before resolving
            // binding or acquiring any later workspace lease so the durable marker always wins without inference.
            var current = await store.GetAsync(claimed.RequestId, CancellationToken.None).ConfigureAwait(false);
            if (current is null || current.ClaimToken != claimToken)
            {
                return;
            }

            if (current.StopReason != McpAgentRunStopReason.None)
            {
                await FinalizeMarkerWinnerAsync(store, current, claimToken).ConfigureAwait(false);
                return;
            }

            SpawnOutcome outcome;
            try
            {
                var executor = scope.ServiceProvider.GetRequiredService<IMcpAgentRunExecutor>();
                outcome = await executor.ExecuteAsync(current, executionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outcome = SpawnOutcome.Failed(InternalFailureCode, "The run ended before producing a result.");
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Durable MCP agent execution failed unexpectedly.");
                outcome = SpawnOutcome.Failed(InternalFailureCode, "The run failed unexpectedly.");
            }

            current = await store.GetAsync(claimed.RequestId, CancellationToken.None).ConfigureAwait(false);
            if (current is null || current.ClaimToken != claimToken)
            {
                return;
            }

            if (current.StopReason != McpAgentRunStopReason.None)
            {
                await FinalizeMarkerWinnerAsync(store, current, claimToken).ConfigureAwait(false);
                return;
            }

            var finalization = CreateNormalFinalization(current, claimToken, outcome);
            if (!await store.TryFinalizeAsync(finalization, CancellationToken.None).ConfigureAwait(false))
            {
                // A stop CAS may have won after the reload. Discard the external result and let its immutable marker
                // choose the terminal outcome.
                current = await store.GetAsync(claimed.RequestId, CancellationToken.None).ConfigureAwait(false);
                if (current is not null && current.ClaimToken == claimToken && current.StopReason != McpAgentRunStopReason.None)
                {
                    await FinalizeMarkerWinnerAsync(store, current, claimToken).ConfigureAwait(false);
                }
            }
            else
            {
                await _metrics.RefreshAsync(store, CancellationToken.None).ConfigureAwait(false);
                _metrics.RecordLifecycle(McpAgentRunText.ToLowercaseInvariant(finalization.Status));
            }
        }
        catch (Exception exception)
        {
            NodeSqliteContention.Record("raw", exception, _logger);
            _logger.LogError(exception, "Durable MCP agent run claim could not be finalized; startup recovery remains authoritative.");
        }
        finally
        {
            await watchdogCompleted.CancelAsync().ConfigureAwait(false);
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when execution completes before the watchdog.
            }

            _cancellations.Remove(claimed.RequestId, claimToken);
        }
    }

    private readonly record struct RegisteredClaim(McpAgentRunRecord Claimed, CancellationToken ExecutionToken);

    private async Task RunWatchdogAsync(McpAgentRunCancellationHandle active, CancellationToken completedToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(_options.WatchdogMinutes), _timeProvider, completedToken).ConfigureAwait(false);
        await PersistStopThenSignalAsync(active,
            McpAgentRunStopReason.WatchdogExpired,
            "watchdog",
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PersistStopThenSignalAsync(McpAgentRunCancellationHandle active,
        McpAgentRunStopReason reason,
        string metricReason,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMcpAgentRunStore>();
            var expectedVersion = active.Version;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var stopped = await store.RequestStopAsync(active.RequestId,
                    expectedVersion,
                    reason,
                    _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    cancellationToken).ConfigureAwait(false);
                if (stopped.Kind != McpAgentRunStopKind.VersionConflict)
                {
                    if (stopped.Kind is McpAgentRunStopKind.Requested or McpAgentRunStopKind.AlreadyRequested)
                    {
                        _cancellations.Signal(active.RequestId, active.ClaimToken);
                    }

                    if (stopped.Kind == McpAgentRunStopKind.Requested)
                    {
                        await _metrics.RefreshAsync(store, CancellationToken.None).ConfigureAwait(false);
                    }

                    _metrics.RecordStop(metricReason, McpAgentRunText.ToLowercaseInvariant(stopped.Kind));
                    return;
                }

                var current = await store.GetAsync(active.RequestId, cancellationToken).ConfigureAwait(false);
                if (current is null || current.ClaimToken != active.ClaimToken)
                {
                    return;
                }

                if (current.StopReason != McpAgentRunStopReason.None)
                {
                    _cancellations.Signal(active.RequestId, active.ClaimToken);
                    _metrics.RecordStop(metricReason, "marker_already_present");
                    return;
                }

                expectedVersion = current.Version;
            }

            _logger.LogWarning("Could not persist a durable MCP agent run {Reason} marker after a version race; execution was not signalled.",
                metricReason);
        }
        catch (Exception exception)
        {
            NodeSqliteContention.Record("raw", exception, _logger);
            _logger.LogError(exception,
                "Could not persist a durable MCP agent run {Reason} marker; execution was not signalled.",
                metricReason);
        }
    }

    private async Task FinalizeRegistrationFailureAsync(McpAgentRunRecord claimed, Guid claimToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMcpAgentRunStore>();
            var finalized = await store.TryFinalizeAsync(new McpAgentRunFinalization(claimed.RequestId,
                    claimed.Version,
                    claimToken,
                    McpAgentRunStatus.Failed,
                    McpAgentRunStopReason.None,
                    InternalFailureCode,
                    Result: null,
                    DisplayMessage: "The run could not be started.",
                    CompletedAtUtc: _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()),
                CancellationToken.None).ConfigureAwait(false);
            if (finalized)
            {
                await _metrics.RefreshAsync(store, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            NodeSqliteContention.Record("raw", exception, _logger);
            _logger.LogError(exception, "Could not reject a duplicate process-local durable MCP run claim.");
        }
    }

    private async Task FinalizeClaimDuringShutdownAsync(McpAgentRunRecord claimed, Guid claimToken)
    {
        try
        {
            var active = new McpAgentRunCancellationHandle(claimed.RequestId, claimToken, claimed.Version);
            await PersistStopThenSignalAsync(active,
                McpAgentRunStopReason.HostShutdown,
                "host",
                CancellationToken.None).ConfigureAwait(false);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMcpAgentRunStore>();
            var current = await store.GetAsync(claimed.RequestId, CancellationToken.None).ConfigureAwait(false);
            if (current is not null && current.ClaimToken == claimToken && current.StopReason != McpAgentRunStopReason.None)
            {
                await FinalizeMarkerWinnerAsync(store, current, claimToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            NodeSqliteContention.Record("raw", exception, _logger);
            _logger.LogError(exception, "Could not terminalize a durable MCP agent run claimed during host shutdown.");
        }
        finally
        {
            _cancellations.Remove(claimed.RequestId, claimToken);
        }
    }

    private async Task FinalizeMarkerWinnerAsync(IMcpAgentRunStore store, McpAgentRunRecord current, Guid claimToken)
    {
        var (status, failureCode, displayMessage) = current.StopReason switch
        {
            McpAgentRunStopReason.UserCancellation => (McpAgentRunStatus.Cancelled, McpExecutionFailureCodes.Cancelled, "Cancelled."),
            McpAgentRunStopReason.WatchdogExpired => (McpAgentRunStatus.Failed, WatchdogExpiredCode, "The run exceeded its execution deadline."),
            McpAgentRunStopReason.HostShutdown => (McpAgentRunStatus.Interrupted, InterruptedCode, "Interrupted by application shutdown."),
            _ => (McpAgentRunStatus.Interrupted, InterruptedCode, "The run was interrupted.")
        };

        var finalized = await store.TryFinalizeAsync(new McpAgentRunFinalization(current.RequestId,
                current.Version,
                claimToken,
                status,
                current.StopReason,
                failureCode,
                Result: null,
                DisplayMessage: displayMessage,
                CompletedAtUtc: _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()),
            CancellationToken.None).ConfigureAwait(false);
        if (finalized)
        {
            await _metrics.RefreshAsync(store, CancellationToken.None).ConfigureAwait(false);
            _metrics.RecordLifecycle(McpAgentRunText.ToLowercaseInvariant(status));
        }
    }

    private McpAgentRunFinalization CreateNormalFinalization(McpAgentRunRecord current,
        Guid claimToken,
        SpawnOutcome outcome)
    {
        var succeeded = outcome.Kind == SpawnOutcomeKind.Success;
        return new McpAgentRunFinalization(current.RequestId,
            current.Version,
            claimToken,
            succeeded ? McpAgentRunStatus.Succeeded : McpAgentRunStatus.Failed,
            McpAgentRunStopReason.None,
            succeeded ? null : outcome.FailureCode ?? InternalFailureCode,
            succeeded ? Truncate(outcome.Content, _options.MaxResultCharacters) : null,
            // At most 512 UTF-16 code units can encode to the store's 2 KiB UTF-8 display bound.
            Truncate(outcome.DisplayMessage, 512),
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private async Task DelayAfterFailureAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds),
                _timeProvider,
                stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
