namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The operator-eject races, each held on a barrier: a teardown still running, a spawn that is ready but not yet
///     published when the eject lands, and the joins on either side of it.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SupervisorOperatorEjectVetoTests
{
    private const string Model = "model-a";
    private const string PartialOffloadLine = "0.00.539.550 I load_tensors: offloaded 38/49 layers to GPU";

    [Test]
    public async Task BackgroundEnsure_WhileTheEjectTeardownIsStillKilling_IsRefused()
    {
        using var killGate = new ManualResetEventSlim(initialState: false);
        var killEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = HoldingFirstKill(killGate, killEntered);
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);

        // Run off the test thread: the held kill blocks the thread that runs the teardown.
        var eject = Task.Run(() => OperatorEjectAsync(supervisor));
        await killEntered.Task;
        AssertEx.Equal(expected: 0, supervisor.CountRunningProcesses(), "The teardown detaches the key before it kills the child.");

        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => EnsureBackgroundAsync(supervisor));
        killGate.Set();

        AssertEx.Equal(LlamaServerEjectOutcome.Ejected, await eject);
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }

    [Test]
    public async Task BackgroundSpawn_ReadyWhenTheEjectLands_IsNeverPublished_AndEveryWaiterIsRefused()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new PublicationGatedHealthProbe();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe: probe, loadTelemetry: telemetry);
        var initiator = EnsureBackgroundAsync(supervisor);
        await probe.Entered;
        var joiner = EnsureBackgroundAsync(supervisor);
        await AssertEx.StaysIncompleteAsync(joiner, "The second Background caller must join the in-flight spawn.");

        // The spawn is ready and parked before publication, so the eject finds nothing registered yet.
        AssertEx.Equal(LlamaServerEjectOutcome.NotRunning, await OperatorEjectAsync(supervisor));
        AssertEx.Null(supervisor.TryAcquireInferenceLease(Model, ModelRole.Chat).Lease);
        probe.Release();

        var first = await AssertEx.ThrowsAsync<OperatorEjectedException>(() => initiator);
        var second = await AssertEx.ThrowsAsync<OperatorEjectedException>(() => joiner);
        AssertEx.True(ReferenceEquals(first, second), "Every waiter must see the one refusal.");
        var after = supervisor.TryAcquireInferenceLease(Model, ModelRole.Chat);
        AssertEx.Null(after.Lease, "A vetoed process must never be leasable.");
        AssertEx.False(after.ProcessEvicting);
        AssertEx.Equal(expected: 0, supervisor.CountRunningProcesses());
        var child = launcher.Handles.Single();
        AssertEx.True(child.WasTreeKilled && child.WasDisposed, "The vetoed child must be killed and disposed.");
        AssertEx.Equal(LlamaServerReadinessOutcome.Cancelled, telemetry.Observations.Single().Outcome, "A vetoed spawn records no Ready observation.");

        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
        AssertEx.Equal(expected: 2, launcher.LaunchCount, "A later Interactive request starts a fresh spawn.");
    }

    [Test]
    public async Task InteractiveJoin_BeforeTheEject_IsRefused_AndTheKeyStaysMarkedAndCold()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new PublicationGatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe: probe);
        var background = EnsureBackgroundAsync(supervisor);
        await probe.Entered;
        AssertEx.Null(supervisor.GetInflightQualifiedAtGeneration(Model, ModelRole.Chat), "A Background initiator never qualifies its spawn.");
        var interactive = supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(interactive, "The Interactive caller must join the in-flight spawn.");
        AssertEx.Equal<long?>(expected: 0, supervisor.GetInflightQualifiedAtGeneration(Model, ModelRole.Chat), "The join qualified the spawn at the generation before the eject.");

        await OperatorEjectAsync(supervisor);
        probe.Release();

        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => background);
        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => interactive);
        AssertEx.Equal(expected: 0, supervisor.CountRunningProcesses());
        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => EnsureBackgroundAsync(supervisor));
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }

    [Test]
    public async Task InteractiveJoin_AfterTheEject_IsPublished_AndClearsTheMark()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new PublicationGatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe: probe);
        var background = EnsureBackgroundAsync(supervisor);
        await probe.Entered;
        await OperatorEjectAsync(supervisor);
        AssertEx.Null(supervisor.GetInflightQualifiedAtGeneration(Model, ModelRole.Chat), "A Background initiator never qualifies its spawn.");

        var interactive = supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(interactive, "The Interactive caller must join the in-flight spawn.");
        AssertEx.Equal<long?>(expected: 1, supervisor.GetInflightQualifiedAtGeneration(Model, ModelRole.Chat), "The join qualified the spawn at the eject's generation.");
        probe.Release();

        await Task.WhenAll(background, interactive);
        AssertEx.Equal(expected: 1, supervisor.CountRunningProcesses());
        await supervisor.EvictAsync(Model, ModelRole.Chat, CancellationToken.None);
        await EnsureBackgroundAsync(supervisor);
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
    }

    [Test]
    public async Task InteractiveEnsure_DuringARefusedSpawnsCleanup_WaitsForIt_ThenLaunchesFresh()
    {
        using var killGate = new ManualResetEventSlim(initialState: false);
        var killEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = HoldingFirstKill(killGate, killEntered);
        var probe = new PublicationGatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe: probe);
        var background = EnsureBackgroundAsync(supervisor);
        await probe.Entered;
        await OperatorEjectAsync(supervisor);
        probe.Release();
        await killEntered.Task;

        var interactive = supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(interactive, "The ensure must wait out the refused spawn's cleanup, not join it.");
        killGate.Set();

        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => background);
        var endpoint = await interactive;
        AssertEx.NotNull(endpoint);
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
    }

    [Test]
    public async Task InteractiveEnsure_WhileARefusedSpawnStillHoldsItsLaunchTicket_WaitsForIt_ThenLaunchesFresh()
    {
        using var ticketGate = new ManualResetEventSlim(initialState: false);
        var ticketDisposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new PublicationGatedHealthProbe();
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: probe,
            launchAdmissions: new FirstTicketHeldRegistry(ticketGate, ticketDisposing));
        try
        {
            var background = EnsureBackgroundAsync(supervisor);
            await probe.Entered;
            await OperatorEjectAsync(supervisor);
            probe.Release();
            await ticketDisposing.Task;
            AssertEx.Equal(expected: 1, supervisor.CountInflightSpawns(), "A refused record stays until its ticket is released.");

            var interactive = supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
            await AssertEx.StaysIncompleteAsync(interactive, "The ensure must wait for the refused spawn to release its launch ticket, not conflict with it.");
            ticketGate.Set();

            await AssertEx.ThrowsAsync<OperatorEjectedException>(() => background);
            AssertEx.NotNull(await interactive);
            AssertEx.Equal(expected: 2, launcher.LaunchCount);
        }
        finally
        {
            // Released before the supervisor's disposal waits out the spawn, so a red run fails at once.
            ticketGate.Set();
        }
    }

    [Test]
    public async Task OperatorEject_LeavesALoadAUserQualifiedAfterTheAttemptOpened_Running()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new PublicationGatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe: probe);
        var background = EnsureBackgroundAsync(supervisor);
        await probe.Entered;
        Task<LlamaServerEndpoint>? interactive = null;
        supervisor.OperatorEjectAttemptOpenedForTesting = async () =>
        {
            // The attempt is open: this join qualifies the parked spawn at the eject's generation, so it publishes before the eject goes on.
            interactive = supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
            probe.Release();
            await interactive.WaitAsync(TestBudgets.Contended);
        };

        AssertEx.Equal(LlamaServerEjectOutcome.NotRunning, await OperatorEjectAsync(supervisor), "The eject read the table before that load published.");
        AssertEx.NotNull(await interactive!);
        AssertEx.NotNull(await background);
        AssertEx.Equal(expected: 1, supervisor.CountRunningProcesses());
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "The eject must not tear down a load published after its attempt opened.");
        using var lease = supervisor.TryAcquireInferenceLease(Model, ModelRole.Chat).Lease;
        AssertEx.NotNull(lease, "The user-waited load stays leasable.");
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }

    [Test]
    public async Task VetoedSpawn_WhoseKillThrows_StaysRefused_AndKeepsItsSlotUntilAReapKillsIt()
    {
        var launches = 0;
        var kills = 0;
        var launcher = new FakeProcessLauncher(_ =>
        {
            var launch = Interlocked.Increment(ref launches);
            Action? onTreeKill = launch == 1
                ? () =>
                {
                    if (Interlocked.Increment(ref kills) == 1)
                    {
                        throw new InvalidOperationException("The kill failed.");
                    }
                }
                : null;
#pragma warning disable CA2000 // Ownership transfers to the supervisor through the launcher fake.
            return new FakeProcessTreeHandle(7000 + launch, onTreeKill: onTreeKill);
#pragma warning restore CA2000
        })
        {
            StartupLines = [PartialOffloadLine]
        };
        var probe = new PublicationGatedHealthProbe();
        var report = new LlamaLayerPlacementReport();
        var logger = new RecordingLogger<LlamaServerProcessSupervisor>();
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: probe,
            options: OneSlot(),
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            layerPlacementReport: report,
            logger: logger);
        supervisor.VetoedKillExitWait = TimeSpan.Zero;
        var initiator = EnsureBackgroundAsync(supervisor);
        await probe.Entered;
        var joiner = EnsureBackgroundAsync(supervisor);
        await AssertEx.StaysIncompleteAsync(joiner, "The second Background caller must join the in-flight spawn.");

        await OperatorEjectAsync(supervisor);
        probe.Release();

        var first = await AssertEx.ThrowsAsync<OperatorEjectedException>(() => initiator);
        var second = await AssertEx.ThrowsAsync<OperatorEjectedException>(() => joiner);
        AssertEx.True(ReferenceEquals(first, second), "Every waiter must see the original refusal.");
        AssertEx.Equal(expected: 1, launcher.LaunchCount, "A failed kill must not turn the veto into a restart.");
        AssertEx.Null(report.Current, "The vetoed spawn's layer placement must be retired.");
        AssertEx.Empty(logger.Entries.Where(static entry => entry.Level >= LogLevel.Error), "An expected veto must not log a failed start.");
        var child = launcher.Handles.Single();
        AssertEx.False(child.WasDisposed, "A child that may still be alive stays owned.");

        await AssertCappedAsync(supervisor, "The surviving child still holds the one slot.");
        await using (var blocked = await supervisor.AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None, raisesDemand: false))
        {
            // Disposed even on a red run, so a wrongly granted lease cannot hold the gate the supervisor's disposal waits on.
            AssertEx.Null(blocked, "A runtime swap must not run while a held child may still have the binary open.");
        }

        await supervisor.ReapIdleOnceAsync();
        AssertEx.True(child.HasExited && child.WasDisposed, "The reap must retry the kill and reclaim the exited child.");
        var mutation = await supervisor.AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None, raisesDemand: false);
        AssertEx.NotNull(mutation, "Once the held child is reclaimed, a runtime swap may run.");
        await mutation!.DisposeAsync();
        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
        AssertEx.Equal(expected: 2, launcher.LaunchCount, "The reclaimed slot admits a fresh launch.");
    }

    [Test]
    public async Task VetoedSpawn_WhoseKillReturnsWhileTheChildLives_KeepsItsSlotUntilTheChildExits()
    {
        var launches = 0;
        var launcher = new FakeProcessLauncher(_ =>
        {
            var launch = Interlocked.Increment(ref launches);
#pragma warning disable CA2000 // Ownership transfers to the supervisor through the launcher fake.
            return new FakeProcessTreeHandle(7000 + launch, exitOnTreeKill: launch != 1);
#pragma warning restore CA2000
        });
        var probe = new PublicationGatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe: probe, options: OneSlot());
        supervisor.VetoedKillExitWait = TimeSpan.Zero;
        var background = EnsureBackgroundAsync(supervisor);
        await probe.Entered;
        await OperatorEjectAsync(supervisor);
        probe.Release();

        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => background);
        var child = launcher.Handles.Single();
        AssertEx.True(child.WasTreeKilled && !child.HasExited, "The kill returned, but the child lives on.");
        AssertEx.False(child.WasDisposed, "A child not seen to exit stays owned.");

        await AssertCappedAsync(supervisor, "The surviving child still holds the one slot.");
        child.SimulateExit();
        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
        AssertEx.Equal(expected: 2, launcher.LaunchCount, "Once the child exits, the next admission reclaims its slot.");
        AssertEx.True(child.WasDisposed, "The reclaimed child's handle is disposed.");
    }

    [Test]
    public async Task FreshEnsure_AfterAnOperatorEjectsAPublishedSpawnStillReleasingItsTicket_DecidesAfresh()
    {
        using var ticketGate = new ManualResetEventSlim(initialState: false);
        var ticketDisposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher, launchAdmissions: new FirstTicketHeldRegistry(ticketGate, ticketDisposing));
        try
        {
            var initiator = supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
            await ticketDisposing.Task;
            AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns(), "A published record leaves before its ticket is released.");
            AssertEx.Equal(LlamaServerEjectOutcome.Ejected, await OperatorEjectAsync(supervisor));

            await AssertEx.ThrowsAsync<OperatorEjectedException>(() => EnsureBackgroundAsync(supervisor).WaitAsync(TestBudgets.Contended),
                "A Background caller must meet the mark, never join the settling spawn whose process was ejected.");
            ticketGate.Set();
            await initiator;

            await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
            AssertEx.Equal(expected: 2, launcher.LaunchCount, "An Interactive caller gets a newly launched process.");
            AssertEx.Equal(expected: 1, supervisor.CountRunningProcesses());
        }
        finally
        {
            // Released before the supervisor's disposal waits out the spawn, so a red run fails at once.
            ticketGate.Set();
        }
    }

    [Test]
    public async Task WaitForRefusedSpawn_ForACancelledCaller_ThrowsItsCancellation_NotTheRefusal()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var refused = Task.FromException(new OperatorEjectedException("Refused."));

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => LlamaServerProcessSupervisor.WaitForRefusedSpawnAsync(refused, cts.Token));
    }

    /// <summary>The veto must not reach the safe-candidate fallback, the restart loop or the failed-start Error log.</summary>
    [Test]
    public async Task VetoedOptimizedSpawn_WithASafeFallbackAndRestartsLeft_LaunchesOnce()
    {
        var launcher = new FakeProcessLauncher
        {
            StartupLines = [PartialOffloadLine]
        };
        var probe = new PublicationGatedHealthProbe();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        var report = new LlamaLayerPlacementReport();
        var logger = new RecordingLogger<LlamaServerProcessSupervisor>();
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: probe,
            options: new LlamaServerSupervisorOptions
            {
                IdleTimeToLive = TimeSpan.FromHours(1),
                MaxLoadedProcesses = 3,
                MaxRestartAttempts = 3
            },
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            layerPlacementReport: report,
            loadTelemetry: telemetry,
            logger: logger);
        var initiator = EnsureBackgroundAsync(supervisor);
        await probe.Entered;
        var joiner = EnsureBackgroundAsync(supervisor);
        await AssertEx.StaysIncompleteAsync(joiner, "The second Background caller must join the in-flight spawn.");
        AssertEx.NotNull(report.Current, "The held spawn has already recorded its layer placement.");

        await OperatorEjectAsync(supervisor);
        probe.Release();

        var first = await AssertEx.ThrowsAsync<OperatorEjectedException>(() => initiator);
        var second = await AssertEx.ThrowsAsync<OperatorEjectedException>(() => joiner);
        AssertEx.True(ReferenceEquals(first, second), "Every waiter must see the original refusal, never a wrapper.");
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.Equal(LlamaServerReadinessOutcome.Cancelled, telemetry.Observations.Single().Outcome);
        AssertEx.Null(report.Current, "The vetoed spawn's layer placement must be retired.");
        AssertEx.Empty(logger.Entries.Where(static entry => entry.Level >= LogLevel.Error), "An expected veto must not log a failed start.");
    }

    /// <summary>One chat slot, so a port reservation that was never released refuses the next cold launch at the cap.</summary>
    private static LlamaServerSupervisorOptions OneSlot() =>
        new()
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 1,
            MaxRestartAttempts = 3
        };

    private static async Task AssertCappedAsync(LlamaServerProcessSupervisor supervisor, string message)
    {
        var capped = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None), message);
        AssertEx.True(capped.Message.Contains("maximum number", StringComparison.Ordinal), message);
        AssertEx.True(capped.Message.Contains("still shutting down", StringComparison.Ordinal), "The cap message must explain the held slot.");
    }

    private static Task<LlamaServerEjectOutcome> OperatorEjectAsync(LlamaServerProcessSupervisor supervisor) =>
        supervisor.EjectAsync(Model, ModelRole.Chat, force: false, CancellationToken.None, operatorRequested: true);

    private static Task<LlamaServerEndpoint> EnsureBackgroundAsync(LlamaServerProcessSupervisor supervisor) =>
        supervisor.EnsureRunningAsync(Model, ModelRole.Chat, ModelResidencyIntent.Background, CancellationToken.None);

    /// <summary>The real registry, except that the FIRST launch ticket signals, then holds its disposal on a gate the test releases.</summary>
    private sealed class FirstTicketHeldRegistry : IProcessLaunchAdmissionRegistry
    {
        private readonly TaskCompletionSource _disposing;
        private readonly ManualResetEventSlim _gate;
        private readonly ProcessLaunchAdmissionRegistry _inner = new();
        private int _tickets;

        public FirstTicketHeldRegistry(ManualResetEventSlim gate, TaskCompletionSource disposing)
        {
            _gate = gate;
            _disposing = disposing;
        }

        public ProcessLaunchAdmissionSnapshot Snapshot(string modelName, ModelRole role) => _inner.Snapshot(modelName, role);

        public IProcessLaunchAdmissionLease? Acquire(ProcessLaunchAdmission admission) => _inner.Acquire(admission);

        public bool TryAcquire(ProcessLaunchAdmission admission, [NotNullWhen(true)] out IProcessLaunchAdmissionLease? lease) => _inner.TryAcquire(admission, out lease);

        public bool TryBeginLaunch(string modelName, ModelRole role, out ProcessLaunchAdmission? admission, [NotNullWhen(true)] out IProcessLaunchTicket? ticket)
        {
            if (!_inner.TryBeginLaunch(modelName, role, out admission, out var inner))
            {
                ticket = null;
                return false;
            }

            ticket = Interlocked.Increment(ref _tickets) == 1 ? new HeldTicket(inner, _gate, _disposing) : inner;
            return true;
        }

        private sealed class HeldTicket : IProcessLaunchTicket
        {
            private readonly TaskCompletionSource _disposing;
            private readonly ManualResetEventSlim _gate;
            private readonly IProcessLaunchTicket _inner;

            public HeldTicket(IProcessLaunchTicket inner, ManualResetEventSlim gate, TaskCompletionSource disposing)
            {
                _inner = inner;
                _gate = gate;
                _disposing = disposing;
            }

            public void Dispose()
            {
                _disposing.TrySetResult();

                // A dispose must not throw; a test that never releases the gate fails on its own assertions after the budget.
                _ = _gate.Wait(TestBudgets.Contended);
                _inner.Dispose();
            }
        }
    }

    /// <summary>A launcher whose FIRST child holds its tree-kill on <paramref name="killGate" />, after signalling <paramref name="killEntered" />.</summary>
    private static FakeProcessLauncher HoldingFirstKill(ManualResetEventSlim killGate, TaskCompletionSource killEntered)
    {
        var launches = 0;
        return new FakeProcessLauncher(_ =>
        {
            var pid = 7000 + Interlocked.Increment(ref launches);
            Action? onTreeKill = pid == 7001
                ? () =>
                {
                    killEntered.TrySetResult();
                    if (!killGate.Wait(TestBudgets.Contended))
                    {
                        throw new TimeoutException("The test never released the held tree-kill.");
                    }
                }
                : null;
#pragma warning disable CA2000 // Ownership transfers to the supervisor through the launcher fake.
            return new FakeProcessTreeHandle(pid, onTreeKill: onTreeKill);
#pragma warning restore CA2000
        });
    }
}
