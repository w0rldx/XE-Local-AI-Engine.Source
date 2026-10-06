namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Collections.Concurrent;
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
using SharedClock = XE_Local_AI_Engine.Client.Testing.Fakes.ManualTimeProvider;

[Category(TestCategories.Unit)]
public sealed class McpAgentRunDispatcherTests
{
    private const string DispatchAwaited = "ScriptedQueue.StopAsync awaits the dispatcher task before the using-scopes exit.";

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
            await releaseList.Task;
            return [queued];
        });
        await using var provider = CreateProvider(store, executor);
        using var wake = NewWake();
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            wake,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            TimeProvider.System);
        await dispatcher.StartAsync(timeout.Token);
        await listStarted.Task.WaitAsync(timeout.Token);

        var stop = dispatcher.StopAsync(timeout.Token);
        AssertEx.False(stop.IsCompleted, "Shutdown must wait for the admitted queue read to leave the claim gate.");
        releaseList.TrySetResult();
        await stop.WaitAsync(timeout.Token);

        await store.DidNotReceiveWithAnyArgs().TryClaimAsync(Guid.Empty, default, default, default);
        await store.DidNotReceiveWithAnyArgs().RequestStopAsync(Guid.Empty, default, default, default, default);
        await store.DidNotReceiveWithAnyArgs().TryFinalizeAsync(default!, default);
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Test]
    public async Task StopAsync_WhenTokenAlreadyCancelledWhileGateHeld_CompletesWithoutThrowing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var cancelledShutdown = new CancellationTokenSource();
        await cancelledShutdown.CancelAsync();
        var registry = new McpAgentRunCancellationRegistry();
        var store = Substitute.For<IMcpAgentRunStore>();
        var executor = Substitute.For<IMcpAgentRunExecutor>();
        var queued = CreateRun(McpAgentRunStatus.Queued, version: 0, claimToken: null, McpAgentRunStopReason.None);
        var listStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ListAsync(Arg.Any<int>(), McpAgentRunStatus.Queued, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            listStarted.TrySetResult();
            await releaseList.Task;
            return [queued];
        });
        await using var provider = CreateProvider(store, executor);
        using var wake = NewWake();
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            wake,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            TimeProvider.System);
        await dispatcher.StartAsync(timeout.Token);
        await listStarted.Task.WaitAsync(timeout.Token);

        // A host shutdown token that has already tripped must not abort the durable stop work nor escape as an exception.
        var stop = dispatcher.StopAsync(cancelledShutdown.Token);
        AssertEx.False(stop.IsCompleted, "Shutdown must still wait for the admitted queue read to leave the claim gate.");
        releaseList.TrySetResult();
        await stop.WaitAsync(timeout.Token);
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
             .Returns(new McpAgentRunClaimResult
             {
                 Kind = McpAgentRunClaimKind.Claimed,
                 Run = claimed
             });
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
        using var wake = NewWake();
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            wake,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            TimeProvider.System);

        await BackgroundServiceTestHelper.RunExecuteAsync(dispatcher, stop.Token).WaitAsync(TimeSpan.FromSeconds(5));

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
             .Returns(new McpAgentRunClaimResult
             {
                 Kind = McpAgentRunClaimKind.Claimed,
                 Run = claimed
             });
        store.GetAsync(claimed.RequestId, Arg.Any<CancellationToken>()).Returns(claimed);
        executor.ExecuteAsync(claimed, Arg.Any<CancellationToken>()).Returns(SpawnOutcome.Success("completed first"));
        store.TryFinalizeAsync(Arg.Any<McpAgentRunFinalization>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            finalization = callInfo.Arg<McpAgentRunFinalization>();
            stop.Cancel();
            return true;
        });
        await using var provider = CreateProvider(store, executor);
        using var wake = NewWake();
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            wake,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            TimeProvider.System);

        await BackgroundServiceTestHelper.RunExecuteAsync(dispatcher, stop.Token).WaitAsync(TimeSpan.FromSeconds(5));

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
             .Returns(new McpAgentRunClaimResult
             {
                 Kind = McpAgentRunClaimKind.Claimed,
                 Run = claimed
             });
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
                 return new McpAgentRunStopResult
                 {
                     Kind = McpAgentRunStopKind.Requested,
                     Run = current
                 };
             });
        executor.ExecuteAsync(Arg.Any<McpAgentRunRecord>(), Arg.Any<CancellationToken>()).Returns(async callInfo =>
        {
            executionStarted.TrySetResult();
            var token = callInfo.Arg<CancellationToken>();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return SpawnOutcome.Success("unreachable");
        });
        store.TryFinalizeAsync(Arg.Any<McpAgentRunFinalization>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            finalization = callInfo.Arg<McpAgentRunFinalization>();
            stop.Cancel();
            return true;
        });
        await using var provider = CreateProvider(store, executor);
        using var wake = NewWake();
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            wake,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            clock);
        var dispatch = BackgroundServiceTestHelper.RunExecuteAsync(dispatcher, stop.Token);
        await executionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromMinutes(30));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

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

    [Test]
    [SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before disposal", Justification = DispatchAwaited)]
    public async Task AnIdleWorker_ClaimsAnAdmissionOnItsWake_WithoutTheClockMoving()
    {
        using var stop = new CancellationTokenSource();
        var clock = new SharedClock();
        var queue = new ScriptedQueue();
        await using var provider = CreateProvider(queue.Store, queue.Executor);
        using var wake = NewWake();
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            new McpAgentRunCancellationRegistry(),
            wake,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            clock,
            pollIntervalMilliseconds: 5000);
        var dispatch = BackgroundServiceTestHelper.RunExecuteAsync(dispatcher, stop.Token);
        await ParkedAsync(clock, timers: 1);

        var run = queue.Enqueue();
        wake.Raise();

        await queue.Claimed(run.RequestId).Task.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(expected: 2, queue.ListCalls, "one empty read before the wait, one after the wake.");
        await queue.StopAsync(stop, dispatch);
    }

    [Test]
    [SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before disposal", Justification = DispatchAwaited)]
    public async Task WithoutAWake_AnIdleWorkerReadsNothingUntilTheFallback_ThenStillClaims()
    {
        using var stop = new CancellationTokenSource();
        var clock = new SharedClock();
        var queue = new ScriptedQueue();
        await using var provider = CreateProvider(queue.Store, queue.Executor);
        using var wake = NewWake();
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            new McpAgentRunCancellationRegistry(),
            wake,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            clock,
            pollIntervalMilliseconds: 5000);
        var dispatch = BackgroundServiceTestHelper.RunExecuteAsync(dispatcher, stop.Token);
        await ParkedAsync(clock, timers: 1);
        var run = queue.Enqueue();

        // The worker is parked on its fallback timer and nothing else can move it, so short of the interval it reads nothing.
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        AssertEx.Equal(expected: 1, queue.ListCalls, "an idle worker must not read the queue before its fallback.");
        AssertEx.False(queue.Claimed(run.RequestId).Task.IsCompleted, "nothing woke the worker yet.");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await queue.Claimed(run.RequestId).Task.WaitAsync(TestBudgets.Contended);
        await queue.StopAsync(stop, dispatch);
    }

    [Test]
    [SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before disposal", Justification = DispatchAwaited)]
    public async Task AWakeRaisedBetweenTheEmptyReadAndTheWait_IsNotLost()
    {
        using var stop = new CancellationTokenSource();
        var clock = new SharedClock();
        var queue = new ScriptedQueue();
        await using var provider = CreateProvider(queue.Store, queue.Executor);
        using var wake = NewWake();
        McpAgentRunRecord? run = null;

        // The read has already produced its empty answer when the admission commits and wakes: the exact lost-wakeup window.
        queue.AfterFirstRead = () =>
        {
            run = queue.Enqueue();
            wake.Raise();
        };
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            new McpAgentRunCancellationRegistry(),
            wake,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            clock,
            pollIntervalMilliseconds: 5000);
        var dispatch = BackgroundServiceTestHelper.RunExecuteAsync(dispatcher, stop.Token);

        await queue.FirstRead.WaitAsync(TestBudgets.Contended);
        await queue.Claimed(run!.RequestId).Task.WaitAsync(TestBudgets.Contended);
        await queue.StopAsync(stop, dispatch);
    }

    [Test]
    [SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before disposal", Justification = DispatchAwaited)]
    public async Task TwoIdleWorkers_ClaimTwoAdmissions_WithoutTheClockMoving()
    {
        using var stop = new CancellationTokenSource();
        var clock = new SharedClock();
        var queue = new ScriptedQueue();
        await using var provider = CreateProvider(queue.Store, queue.Executor);
        using var wake = NewWake(workers: 2);
        using var dispatcher = CreateDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            new McpAgentRunCancellationRegistry(),
            wake,
            provider.GetRequiredService<McpAgentRunMetrics>(),
            clock,
            workers: 2,
            pollIntervalMilliseconds: 5000);
        var dispatch = BackgroundServiceTestHelper.RunExecuteAsync(dispatcher, stop.Token);
        await ParkedAsync(clock, timers: 2);

        // Executions stay parked, so neither worker can loop back and take the second run itself: each claim needs its own wake.
        var first = queue.Enqueue();
        wake.Raise();
        var second = queue.Enqueue();
        wake.Raise();

        await queue.Claimed(first.RequestId).Task.WaitAsync(TestBudgets.Contended);
        await queue.Claimed(second.RequestId).Task.WaitAsync(TestBudgets.Contended);
        await queue.StopAsync(stop, dispatch);
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

    /// <summary>Completes once the idle workers have armed their fallback timers, so the clock is never moved past a wait nobody holds.</summary>
    private static Task ParkedAsync(SharedClock clock, int timers) =>
        AssertEx.EventuallyAsync(() => clock.ArmedTimerCount >= timers, TestBudgets.Contended, "the worker never parked on its fallback timer.");

    private static McpAgentRunWakeSignal NewWake(int workers = 1) =>
        new(Options.Create(new McpAgentRunOptions
        {
            MaxConcurrentWorkers = workers
        }));

    private static McpAgentRunDispatcher CreateDispatcher(IServiceScopeFactory scopeFactory,
        McpAgentRunCancellationRegistry registry,
        McpAgentRunWakeSignal wake,
        McpAgentRunMetrics metrics,
        TimeProvider timeProvider,
        int workers = 1,
        int pollIntervalMilliseconds = 50) =>
        new(scopeFactory,
            registry,
            wake,
            metrics,
            Options.Create(new McpAgentRunOptions
            {
                MaxConcurrentWorkers = workers,
                PollIntervalMilliseconds = pollIntervalMilliseconds,
                WatchdogMinutes = 30
            }),
            timeProvider,
            NullLogger<McpAgentRunDispatcher>.Instance);

    private static McpAgentRunRecord CreateRun(McpAgentRunStatus status,
        long version,
        Guid? claimToken,
        McpAgentRunStopReason stopReason) =>
        new()
        {
            RequestId = Guid.Parse("4f42e874-a781-4f2a-a4d2-b6d5bd6f00cc"),
            RequestFingerprint = SHA256.HashData("request"u8),
            Status = status,
            Version = version,
            ClaimToken = claimToken,
            StopReason = stopReason,
            StopRequestedAtUtc = null,
            AgentDefinitionId = null,
            AgentDefinitionVersion = null,
            ModelId = "local-model",
            ModelOverrideId = null,
            WorkspaceId = null,
            BindingFingerprint = SHA256.HashData("binding"u8),
            Task = "task",
            Instructions = "read only",
            Result = null,
            DisplayMessage = null,
            FailureCode = null,
            CreatedAtUtc = 1,
            ClaimedAtUtc = null,
            CompletedAtUtc = null,
            PayloadExpiresAtUtc = 86_400_001,
            CompactedAtUtc = null,
            PayloadExpired = false
        };

    private static McpAgentRunLedgerSnapshot EmptySnapshot() =>
        new()
        {
            QueueDepth = 0,
            RunningCount = 0,
            Counters = new McpAgentRunLedgerCounters
            {
                AccountingVersion = 1,
                NonterminalRunCount = 0,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                IdentityCount = 0,
                ActivePayloadBytes = 0,
                TombstoneLogicalBytes = 0,
                UpdatedAtUtc = 0
            }
        };

    /// <summary>A store whose queue the test fills, and an executor that parks every claim until the test releases it.</summary>
    private sealed class ScriptedQueue
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _claimed = new();
        private readonly ConcurrentDictionary<Guid, McpAgentRunRecord> _running = new();
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private readonly List<McpAgentRunRecord> _queued = [];
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _listCalls;

        public ScriptedQueue()
        {
            Store.ListAsync(Arg.Any<int>(), McpAgentRunStatus.Queued, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                McpAgentRunRecord[] snapshot;
                lock (_gate)
                {
                    snapshot = [.. _queued];
                }

                if (Interlocked.Increment(ref _listCalls) == 1)
                {
                    AfterFirstRead?.Invoke();
                    _firstRead.TrySetResult();
                }

                return snapshot;
            });
            Store.TryClaimAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
            {
                var requestId = callInfo.ArgAt<Guid>(0);
                McpAgentRunRecord? queued;
                lock (_gate)
                {
                    queued = _queued.Find(run => run.RequestId == requestId);
                    if (queued is not null)
                    {
                        _ = _queued.Remove(queued);
                    }
                }

                if (queued is null)
                {
                    return new McpAgentRunClaimResult
                    {
                        Kind = McpAgentRunClaimKind.NotQueued,
                        Run = null
                    };
                }

                var claimed = queued with
                {
                    Status = McpAgentRunStatus.Running,
                    Version = 1,
                    ClaimToken = Guid.NewGuid(),
                    ClaimedAtUtc = 2
                };
                _running[requestId] = claimed;
                Claimed(requestId).TrySetResult();
                return new McpAgentRunClaimResult
                {
                    Kind = McpAgentRunClaimKind.Claimed,
                    Run = claimed
                };
            });
            Store.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                 .Returns(callInfo => _running.GetValueOrDefault(callInfo.ArgAt<Guid>(0)));
            Store.TryFinalizeAsync(Arg.Any<McpAgentRunFinalization>(), Arg.Any<CancellationToken>()).Returns(true);
            Executor.ExecuteAsync(Arg.Any<McpAgentRunRecord>(), Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                await _release.Task;
                return SpawnOutcome.Success("done");
            });
        }

        public IMcpAgentRunStore Store { get; } = Substitute.For<IMcpAgentRunStore>();

        public IMcpAgentRunExecutor Executor { get; } = Substitute.For<IMcpAgentRunExecutor>();

        public int ListCalls => Volatile.Read(ref _listCalls);

        public Task FirstRead => _firstRead.Task;

        /// <summary>Runs inside the first queue read, after its answer was taken and before the worker sees it.</summary>
        public Action? AfterFirstRead { get; set; }

        public McpAgentRunRecord Enqueue()
        {
            var run = CreateRun(McpAgentRunStatus.Queued, version: 0, claimToken: null, McpAgentRunStopReason.None) with
            {
                RequestId = Guid.NewGuid()
            };
            lock (_gate)
            {
                _queued.Add(run);
            }

            return run;
        }

        public TaskCompletionSource Claimed(Guid requestId) =>
            _claimed.GetOrAdd(requestId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public async Task StopAsync(CancellationTokenSource stop, Task dispatch)
        {
            _release.TrySetResult();
            await stop.CancelAsync();
            await dispatch.WaitAsync(TestBudgets.Contended, CancellationToken.None);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

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

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _remaining;
            private TimeSpan _period;
            private bool _disposed;

            public ManualTimer(ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                _remaining = dueTime;
                _period = period;
            }

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

                _callback(_state);
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
                    _owner.Remove(this);
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
