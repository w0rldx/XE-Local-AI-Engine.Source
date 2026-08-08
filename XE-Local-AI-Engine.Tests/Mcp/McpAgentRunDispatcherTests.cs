namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel]
public sealed class McpAgentRunDispatcherTests
{
    [Test]
    public async Task StopAsync_WhileQueueReadIsInFlight_DoesNotClaimOrInterruptQueuedRun()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var registry = new McpAgentRunCancellationRegistry();
        var store = Substitute.For<IMcpAgentRunStore>();
        var executor = Substitute.For<IMcpAgentRunExecutor>();
        var queued = CreateRun(McpAgentRunStatus.Queued, version: 0, claimToken: null, McpAgentRunStopReason.None);
        var listStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ListAsync(Arg.Any<int>(), McpAgentRunStatus.Queued, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            listStarted.TrySetResult();
            await releaseList.Task.ConfigureAwait(false);
            return [queued];
        });
        await using var provider = CreateProvider(store, executor);
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            TimeProvider.System);
        await dispatcher.StartAsync(timeout.Token).ConfigureAwait(false);
        await listStarted.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

        var stop = dispatcher.StopAsync(timeout.Token);
        AssertEx.False(stop.IsCompleted, "Shutdown must wait for the admitted queue read to leave the claim gate.");
        releaseList.TrySetResult();
        await stop.WaitAsync(timeout.Token).ConfigureAwait(false);

        await store.DidNotReceiveWithAnyArgs().TryClaimAsync(Guid.Empty, default, default, default);
        await store.DidNotReceiveWithAnyArgs().RequestStopAsync(Guid.Empty, default, default, default, default);
        await store.DidNotReceiveWithAnyArgs().TryFinalizeAsync(default!, default);
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Test]
    [Arguments(McpAgentRunStopReason.UserCancellation, McpAgentRunStatus.Cancelled, "cancelled")]
    [Arguments(McpAgentRunStopReason.WatchdogExpired, McpAgentRunStatus.Failed, "watchdog_expired")]
    [Arguments(McpAgentRunStopReason.HostShutdown, McpAgentRunStatus.Interrupted, "interrupted")]
    public async Task ExecuteAsync_WhenMarkerCommitsBetweenClaimAndReload_MarkerWinsWithoutInference(McpAgentRunStopReason stopReason,
        McpAgentRunStatus expectedStatus,
        string expectedFailureCode)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var registry = new McpAgentRunCancellationRegistry();
        var store = Substitute.For<IMcpAgentRunStore>();
        var executor = Substitute.For<IMcpAgentRunExecutor>();
        var queued = CreateRun(McpAgentRunStatus.Queued, version: 0, claimToken: null, McpAgentRunStopReason.None);
        var claimToken = Guid.NewGuid();
        var claimed = queued with
        {
            Status = McpAgentRunStatus.Running,
            Version = 1,
            ClaimToken = claimToken,
            ClaimedAtUtc = 2
        };
        var marker = claimed with
        {
            Version = 2,
            StopReason = stopReason,
            StopRequestedAtUtc = 3
        };
        McpAgentRunFinalization? finalization = null;
        store.ListAsync(Arg.Any<int>(), McpAgentRunStatus.Queued, Arg.Any<CancellationToken>())
             .Returns([queued]);
        store.TryClaimAsync(Arg.Is<Guid>(requestId => requestId == queued.RequestId),
                 Arg.Is<long>(version => version == queued.Version),
                 Arg.Any<long>(),
                 Arg.Any<CancellationToken>())
             .Returns(new McpAgentRunClaimResult(McpAgentRunClaimKind.Claimed, claimed));
        store.GetAsync(claimed.RequestId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            AssertEx.True(registry.Signal(claimed.RequestId, claimToken),
                "The process-local CTS must exist before the post-claim durable marker reload.");
            return marker;
        });
        store.TryFinalizeAsync(Arg.Any<McpAgentRunFinalization>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            finalization = callInfo.Arg<McpAgentRunFinalization>();
            stop.Cancel();
            return true;
        });
        await using var provider = CreateProvider(store, executor);
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            TimeProvider.System);

        await BackgroundServiceTestHelper.RunExecuteAsync(dispatcher, stop.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        AssertEx.Equal(expectedStatus, finalization!.Status);
        AssertEx.Equal(stopReason, finalization.ExpectedStopReason);
        AssertEx.Equal(expectedFailureCode, finalization.FailureCode!);
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await store.Received(2).GetLedgerSnapshotAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WhenNormalCompletionCommitsFirst_PersistsSuccessfulOutcome()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var registry = new McpAgentRunCancellationRegistry();
        var store = Substitute.For<IMcpAgentRunStore>();
        var executor = Substitute.For<IMcpAgentRunExecutor>();
        var queued = CreateRun(McpAgentRunStatus.Queued, version: 0, claimToken: null, McpAgentRunStopReason.None);
        var claimed = queued with
        {
            Status = McpAgentRunStatus.Running,
            Version = 1,
            ClaimToken = Guid.NewGuid(),
            ClaimedAtUtc = 2
        };
        McpAgentRunFinalization? finalization = null;
        store.ListAsync(Arg.Any<int>(), McpAgentRunStatus.Queued, Arg.Any<CancellationToken>()).Returns([queued]);
        store.TryClaimAsync(Arg.Is<Guid>(requestId => requestId == queued.RequestId),
                 Arg.Is<long>(version => version == queued.Version),
                 Arg.Any<long>(),
                 Arg.Any<CancellationToken>())
             .Returns(new McpAgentRunClaimResult(McpAgentRunClaimKind.Claimed, claimed));
        store.GetAsync(claimed.RequestId, Arg.Any<CancellationToken>()).Returns(claimed);
        executor.ExecuteAsync(claimed, Arg.Any<CancellationToken>()).Returns(SpawnOutcome.Success("completed first"));
        store.TryFinalizeAsync(Arg.Any<McpAgentRunFinalization>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            finalization = callInfo.Arg<McpAgentRunFinalization>();
            stop.Cancel();
            return true;
        });
        await using var provider = CreateProvider(store, executor);
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            TimeProvider.System);

        await BackgroundServiceTestHelper.RunExecuteAsync(dispatcher, stop.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunStatus.Succeeded, finalization!.Status);
        AssertEx.Equal(McpAgentRunStopReason.None, finalization.ExpectedStopReason);
        AssertEx.Equal("completed first", finalization.Result!);
        await store.Received(2).GetLedgerSnapshotAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    [SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before disposal",
        Justification = "The dispatcher task is explicitly awaited before the dispatcher using-scope exits.")]
    public async Task ExecuteAsync_AfterThirtyMinuteWatchdog_PersistsMarkerBeforeCancellingExecution()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new McpAgentRunCancellationRegistry();
        var store = Substitute.For<IMcpAgentRunStore>();
        var executor = Substitute.For<IMcpAgentRunExecutor>();
        var queued = CreateRun(McpAgentRunStatus.Queued, version: 0, claimToken: null, McpAgentRunStopReason.None);
        var claimed = queued with
        {
            Status = McpAgentRunStatus.Running,
            Version = 1,
            ClaimToken = Guid.NewGuid(),
            ClaimedAtUtc = 2
        };
        var current = claimed;
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        McpAgentRunFinalization? finalization = null;
        store.ListAsync(Arg.Any<int>(), McpAgentRunStatus.Queued, Arg.Any<CancellationToken>()).Returns([queued]);
        store.TryClaimAsync(Arg.Is<Guid>(requestId => requestId == queued.RequestId),
                 Arg.Is<long>(version => version == queued.Version),
                 Arg.Any<long>(),
                 Arg.Any<CancellationToken>())
             .Returns(new McpAgentRunClaimResult(McpAgentRunClaimKind.Claimed, claimed));
        store.GetAsync(claimed.RequestId, Arg.Any<CancellationToken>()).Returns(_ => current);
        store.RequestStopAsync(Arg.Is<Guid>(requestId => requestId == claimed.RequestId),
                 Arg.Any<long>(),
                 Arg.Is<McpAgentRunStopReason>(reason => reason == McpAgentRunStopReason.WatchdogExpired),
                 Arg.Any<long>(),
                 Arg.Any<CancellationToken>())
             .Returns(_ =>
             {
                 current = current with
                 {
                     Version = current.Version + 1,
                     StopReason = McpAgentRunStopReason.WatchdogExpired,
                     StopRequestedAtUtc = 30
                 };
                 return new McpAgentRunStopResult(McpAgentRunStopKind.Requested, current);
             });
        executor.ExecuteAsync(Arg.Any<McpAgentRunRecord>(), Arg.Any<CancellationToken>()).Returns(async callInfo =>
        {
            executionStarted.TrySetResult();
            var token = callInfo.Arg<CancellationToken>();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return SpawnOutcome.Success("unreachable");
        });
        store.TryFinalizeAsync(Arg.Any<McpAgentRunFinalization>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            finalization = callInfo.Arg<McpAgentRunFinalization>();
            stop.Cancel();
            return true;
        });
        await using var provider = CreateProvider(store, executor);
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            clock);
        var dispatch = BackgroundServiceTestHelper.RunExecuteAsync(dispatcher, stop.Token);
        await executionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        clock.Advance(TimeSpan.FromMinutes(30));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        AssertEx.Equal(TimeSpan.FromMinutes(30), clock.FirstDueTime);
        AssertEx.Equal(McpAgentRunStatus.Failed, finalization!.Status);
        AssertEx.Equal(McpAgentRunStopReason.WatchdogExpired, finalization.ExpectedStopReason);
        AssertEx.Equal("watchdog_expired", finalization.FailureCode!);
        await store.Received().RequestStopAsync(Arg.Is<Guid>(requestId => requestId == claimed.RequestId),
            Arg.Any<long>(),
            Arg.Is<McpAgentRunStopReason>(reason => reason == McpAgentRunStopReason.WatchdogExpired),
            Arg.Any<long>(),
            Arg.Any<CancellationToken>());
        await store.Received(3).GetLedgerSnapshotAsync(Arg.Any<CancellationToken>());
    }

    private static ServiceProvider CreateProvider(IMcpAgentRunStore store, IMcpAgentRunExecutor executor)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(executor);
        services.AddSingleton<McpAgentRunMetrics>();
        store.GetLedgerSnapshotAsync(Arg.Any<CancellationToken>()).Returns(EmptySnapshot());
        return services.BuildServiceProvider();
    }

    private static McpAgentRunDispatcher CreateDispatcher(IServiceScopeFactory scopeFactory,
        McpAgentRunCancellationRegistry registry,
        McpAgentRunMetrics metrics,
        TimeProvider timeProvider) =>
        new(scopeFactory,
            registry,
            metrics,
            Options.Create(new McpAgentRunOptions
            {
                MaxConcurrentWorkers = 1,
                PollIntervalMilliseconds = 50,
                WatchdogMinutes = 30
            }),
            timeProvider,
            NullLogger<McpAgentRunDispatcher>.Instance);

    private static McpAgentRunRecord CreateRun(McpAgentRunStatus status,
        long version,
        Guid? claimToken,
        McpAgentRunStopReason stopReason) =>
        new(Guid.Parse("4f42e874-a781-4f2a-a4d2-b6d5bd6f00cc"),
            SHA256.HashData("request"u8),
            status,
            version,
            claimToken,
            stopReason,
            StopRequestedAtUtc: null,
            AgentDefinitionId: null,
            AgentDefinitionVersion: null,
            ModelId: "local-model",
            ModelOverrideId: null,
            WorkspaceId: null,
            BindingFingerprint: SHA256.HashData("binding"u8),
            Task: "task",
            Instructions: "read only",
            Result: null,
            DisplayMessage: null,
            FailureCode: null,
            CreatedAtUtc: 1,
            ClaimedAtUtc: null,
            CompletedAtUtc: null,
            PayloadExpiresAtUtc: 86_400_001,
            CompactedAtUtc: null,
            PayloadExpired: false);

    private static McpAgentRunLedgerSnapshot EmptySnapshot() =>
        new(QueueDepth: 0,
            RunningCount: 0,
            new McpAgentRunLedgerCounters(AccountingVersion: 1,
                NonterminalRunCount: 0,
                QueuedRunCount: 0,
                RunningRunCount: 0,
                IdentityCount: 0,
                ActivePayloadBytes: 0,
                TombstoneLogicalBytes: 0,
                UpdatedAtUtc: 0));

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = utcNow;

        public TimeSpan? FirstDueTime { get; private set; }

        public override DateTimeOffset GetUtcNow() =>
            _utcNow;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_gate)
            {
                FirstDueTime ??= dueTime;
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] timers;
            lock (_gate)
            {
                _utcNow += elapsed;
                timers = _timers.Where(timer => timer.Advance(elapsed)).ToArray();
            }

            foreach (var timer in timers)
            {
                timer.Fire();
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private TimeSpan _remaining = dueTime;
            private TimeSpan _period = period;
            private bool _disposed;

            public bool Advance(TimeSpan elapsed)
            {
                if (_disposed || _remaining == Timeout.InfiniteTimeSpan)
                {
                    return false;
                }

                _remaining -= elapsed;
                return _remaining <= TimeSpan.Zero;
            }

            public void Fire()
            {
                if (_disposed)
                {
                    return;
                }

                callback(state);
                _remaining = _period;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                {
                    return false;
                }

                _remaining = dueTime;
                _period = period;
                return true;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    owner.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
