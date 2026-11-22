namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using NSubstitute;
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
    public async Task LoweredChatCap_WithAHeldChatSurvivor_TheNextReapUnloadsTheRegisteredChat()
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
        var options = new LlamaServerSupervisorOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 2,
            MaxRestartAttempts = 3
        };
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe: probe, options: options);
        supervisor.VetoedKillExitWait = TimeSpan.Zero;
        await EjectAParkedBackgroundLoadAsync(supervisor, probe);
        await supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None);
        AssertEx.NotNull(supervisor.TryAcquireInferenceLease("chat-b", ModelRole.Chat).Lease).Dispose();

        // One registered chat, but two reserved chat slots: the held survivor still occupies one.
        options.MaxLoadedProcesses = 1;
        await supervisor.ReapIdleOnceAsync();

        var handles = launcher.Handles.OrderBy(static handle => handle.ProcessId).ToList();
        AssertEx.False(handles[0].HasExited, "the survivor is still held");
        AssertEx.True(handles[1].WasTreeKilled, "the registered chat yields, so the reserved slots meet the lowered cap");
        AssertEx.Equal(expected: 0, supervisor.CountRunningProcesses());
    }

    [Test]
    public async Task LoweredChatCap_AHeldSurvivorExitingDuringTheKeepWarmRead_FreesItsSlot_AndTheLiveChatStays()
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
        var options = new LlamaServerSupervisorOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 2,
            MaxRestartAttempts = 3
        };

        // The survivor exits while the reconciliation reads the keep-warm names, outside the gate, after its early over-cap check.
        var admission = Substitute.For<ILlamaServerPooledLaunchAdmission>();
        admission.GetProtectedChatModelsAsync(Arg.Any<CancellationToken>())
                 .Returns(_ =>
                 {
                     launcher.Handles.OrderBy(static handle => handle.ProcessId).First().SimulateExit();
                     return Task.FromResult<IReadOnlyCollection<string>>([]);
                 });
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe: probe, options: options, pooledLaunchAdmission: admission);
        supervisor.VetoedKillExitWait = TimeSpan.Zero;
        await EjectAParkedBackgroundLoadAsync(supervisor, probe);
        await supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None);
        AssertEx.NotNull(supervisor.TryAcquireInferenceLease("chat-b", ModelRole.Chat).Lease).Dispose();

        options.MaxLoadedProcesses = 1;
        await supervisor.ReapIdleOnceAsync();

        var handles = launcher.Handles.OrderBy(static handle => handle.ProcessId).ToList();
        AssertEx.True(handles[0].HasExited && handles[0].WasDisposed, "the exited survivor is reclaimed and its handle disposed");
        AssertEx.False(handles[1].WasTreeKilled, "with the survivor's slot freed the node meets the cap, so the live chat stays loaded");
        AssertEx.Equal(expected: 1, supervisor.CountRunningProcesses());
    }

    [Test]
    public async Task CompanionAtItsCap_WithAHeldChatSurvivor_ReportsThePlainCapMessage()
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
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: probe,
            options: new LlamaServerSupervisorOptions
            {
                IdleTimeToLive = TimeSpan.FromHours(1),
                MaxLoadedProcesses = 1,
                MaxLoadedCompanionProcesses = 1,
                MaxRestartAttempts = 3
            });
        supervisor.VetoedKillExitWait = TimeSpan.Zero;
        await EjectAParkedBackgroundLoadAsync(supervisor, probe);
        await AssertCappedAsync(supervisor, "The held chat survivor fills the chat slot, and the chat cap message names it.");

        // The only companion slot holds a leased embedder, so the next companion is refused at the companion cap.
        await supervisor.EnsureRunningAsync("embed-a", ModelRole.Embedding, CancellationToken.None);
        using var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease("embed-a", ModelRole.Embedding).Lease);
        var capped = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("embed-b", ModelRole.Embedding, CancellationToken.None));

        AssertEx.Contains(capped.Message, "maximum number of local models");
        AssertEx.False(capped.Message.Contains("still shutting down", StringComparison.Ordinal), "A chat survivor holds no companion slot, so the companion cap message must not blame it.");
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

    [Test]
    public async Task VetoedSafeRetry_StillRecordsItsVerdict_OnceForItsBackendAndKvType()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new SafeRetryPublicationGatedHealthProbe();
        var fallbackStore = new FakeLaunchFallbackStore();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: probe,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: fallbackStore,
            loadTelemetry: telemetry);
        var background = EnsureBackgroundAsync(supervisor);
        await probe.Entered;

        await OperatorEjectAsync(supervisor);
        probe.Release();

        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => background);
        AssertEx.Equal(expected: 2, launcher.LaunchCount, "The optimized attempt failed and the safe retry reached readiness.");
        var (variant, kvCacheType) = fallbackStore.Disabled.Single();
        AssertEx.Equal(GpuVariant.Cuda, variant);
        AssertEx.Equal(LlamaServerKvCacheTypes.Q8_0, kvCacheType);
        AssertEx.Equal("Primary:Failed,SafeRetry:Cancelled",
            string.Join(",", telemetry.Observations.Select(static observation => $"{observation.AttemptKind}:{observation.Outcome}")),
            "A vetoed safe retry publishes no Ready observation.");
    }

    [Test]
    public async Task VetoedExpertOffloadSafeRetry_RecordsNothing()
    {
        var probe = new SafeRetryPublicationGatedHealthProbe();
        var fallbackStore = new FakeLaunchFallbackStore();
        await using var supervisor = SupervisorFactory.Create(new FakeProcessLauncher(),
            healthProbe: probe,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: fallbackStore,
            allocationResolver: ExpertOffloadAllocationResolver());
        var background = EnsureBackgroundAsync(supervisor);
        await probe.Entered;

        await OperatorEjectAsync(supervisor);
        probe.Release();

        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => background);
        AssertEx.Empty(fallbackStore.Disabled, "An expert-offload safe retry is inconclusive about KV, vetoed or not.");
    }

    [Test]
    public async Task VetoedSafeRetry_WhoseVerdictStoreThrows_IsStillTornDown_AndTheWaiterGetsTheVeto()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new SafeRetryPublicationGatedHealthProbe();
        var logger = new RecordingLogger<LlamaServerProcessSupervisor>();
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: probe,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: new HeldOrThrowingFallbackStore(hold: false),
            logger: logger);
        var background = EnsureBackgroundAsync(supervisor);
        await probe.Entered;

        await OperatorEjectAsync(supervisor);
        probe.Release();

        var refusal = await AssertEx.ThrowsAsync<OperatorEjectedException>(() => background);
        AssertEx.Null(refusal.InnerException, "The waiter gets the original veto, never the store's fault.");
        var safeChild = launcher.Handles.Last();
        AssertEx.True(safeChild.WasTreeKilled && safeChild.WasDisposed, "The vetoed safe retry must be killed and disposed.");
        AssertEx.Equal(expected: 0, supervisor.CountRunningProcesses());
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "safe-retry verdict for model"), "A lost verdict is said out loud.");
    }

    [Test]
    public async Task VetoedSafeRetry_WhoseVerdictStoreHangs_SettlesItsWaitersAndTicketWithinTheBudget()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new SafeRetryPublicationGatedHealthProbe();
        var store = new HeldOrThrowingFallbackStore(hold: true);
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: probe,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: store);
        supervisor.VetoedSafeRetryVerdictBudget = TimeSpan.FromMilliseconds(100);
        try
        {
            var background = EnsureBackgroundAsync(supervisor);
            await probe.Entered;
            await OperatorEjectAsync(supervisor);
            probe.Release();

            await store.Entered.WaitAsync(TestBudgets.Contended);
            AssertEx.True(launcher.Handles.Last().WasTreeKilled, "The teardown runs before the verdict write.");
            await AssertEx.ThrowsAsync<OperatorEjectedException>(() => background.WaitAsync(TestBudgets.Contended),
                "A store that never returns must not hold the waiters past the budget.");
            AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns(), "The settled spawn released its record and ticket.");

            AssertEx.NotNull(await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None));
            AssertEx.Equal(expected: 3, launcher.LaunchCount, "A fresh Interactive ensure launches without a ticket conflict.");
        }
        finally
        {
            store.Release();
        }
    }

    [Test]
    public async Task VetoedSafeRetry_WhoseVerdictStoreBlocksSynchronously_SettlesItsWaitersAndTicketWhileTheStoreIsStillBlocked()
    {
        using var storeGate = new ManualResetEventSlim(initialState: false);
        var launcher = new FakeProcessLauncher();
        var probe = new SafeRetryPublicationGatedHealthProbe();
        var store = new SynchronouslyBlockingFallbackStore(storeGate);
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: probe,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: store);
        supervisor.VetoedSafeRetryVerdictBudget = TimeSpan.FromMilliseconds(100);
        try
        {
            var background = EnsureBackgroundAsync(supervisor);
            await probe.Entered;
            await OperatorEjectAsync(supervisor);
            probe.Release();

            await store.Entered.WaitAsync(TestBudgets.Contended);
            await AssertEx.ThrowsAsync<OperatorEjectedException>(() => background.WaitAsync(TestBudgets.Contended),
                "A store that blocks before returning its task must not hold the waiters past the budget.");
            AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns(), "The settled spawn released its record and ticket.");
            AssertEx.False(store.Returned, "The waiters settled while the store was still blocked.");
        }
        finally
        {
            storeGate.Set();
        }
    }

    [Test]
    public async Task VetoedSafeRetry_WhoseVerdictIsCutShortByShutdown_LogsAtDebug_NotWarning()
    {
        var probe = new SafeRetryPublicationGatedHealthProbe();
        var store = new HeldOrThrowingFallbackStore(hold: true);
        var logger = new RecordingLogger<LlamaServerProcessSupervisor>();
        await using var supervisor = SupervisorFactory.Create(new FakeProcessLauncher(),
            healthProbe: probe,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: store,
            logger: logger);
        supervisor.VetoedSafeRetryVerdictBudget = TestBudgets.Contended;
        try
        {
            var background = EnsureBackgroundAsync(supervisor);
            await probe.Entered;
            await OperatorEjectAsync(supervisor);
            probe.Release();
            await store.Entered.WaitAsync(TestBudgets.Contended);

            await supervisor.DisposeAsync().AsTask().WaitAsync(TestBudgets.Contended);

            await AssertEx.ThrowsAsync<OperatorEjectedException>(() => background);
            AssertEx.True(logger.HasEntry(LogLevel.Debug, "the supervisor is shutting down"), "A shutdown that cuts the verdict short is logged at Debug.");
            AssertEx.False(logger.HasEntry(LogLevel.Warning, "safe-retry verdict for model"), "A shutdown is not a lost-verdict Warning.");
        }
        finally
        {
            store.Release();
        }
    }

    [Test]
    public async Task FailedSpawn_StillHoldingItsTicket_HandsALateEnsureTheSameFailure_ThenAFreshEnsureLaunches()
    {
        using var ticketGate = new ManualResetEventSlim(initialState: false);
        var ticketDisposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeProcessLauncher();
        var probe = new FakeHealthProbe(ready: false);
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: probe,
            options: OneAttempt(),
            launchAdmissions: new FirstTicketHeldRegistry(ticketGate, ticketDisposing));
        try
        {
            var initiator = supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
            await ticketDisposing.Task;
            AssertEx.Equal(expected: 1, supervisor.CountInflightSpawns(), "A failed record stays until its ticket is released.");

            var late = supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
            await AssertEx.StaysIncompleteAsync(late, "The late ensure must join the failed spawn, not conflict with its held ticket.");
            ticketGate.Set();

            var first = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => initiator);
            var second = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => late);
            AssertEx.True(ReferenceEquals(first, second), "The late caller gets the same failure as the initiator.");
            var launchesAfterFailure = launcher.LaunchCount;

            probe.Ready = true;
            AssertEx.NotNull(await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None));
            AssertEx.Equal(launchesAfterFailure + 1, launcher.LaunchCount, "Once released, a fresh ensure launches.");
        }
        finally
        {
            ticketGate.Set();
        }
    }

    [Test]
    public async Task FailedSpawn_WhoseTicketDisposalThrows_StillReleasesItsReservation_AndTheWaiterGetsTheOriginalFailure()
    {
        using var ticketGate = new ManualResetEventSlim(initialState: true);
        var reservation = Substitute.For<IDisposable>();
        var hook = Substitute.For<ILlamaServerPooledLaunchAdmission>();
        hook.AdmitChatAsync(Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<Func<IReadOnlyCollection<string>, CancellationToken, Task<IdleChatEvictionResult>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDisposable?>(reservation));
        var logger = new RecordingLogger<LlamaServerProcessSupervisor>();
        await using var supervisor = SupervisorFactory.Create(healthProbe: new FakeHealthProbe(ready: false),
            options: OneAttempt(),
            launchAdmissions: new FirstTicketHeldRegistry(ticketGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), throwOnDispose: true),
            pooledLaunchAdmission: hook,
            logger: logger);

        var failure = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None).WaitAsync(TestBudgets.Contended));

        AssertEx.False(failure.Message.Contains("disposal failed", StringComparison.Ordinal), "A cleanup fault never replaces the spawn's failure.");
        reservation.Received(1).Dispose();
        AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns());
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "Releasing the launch admission"), "The cleanup fault is logged on its own.");
    }

    [Test]
    public async Task Dispose_WithAFailedSpawnStillHoldingItsTicket_Completes()
    {
        using var ticketGate = new ManualResetEventSlim(initialState: false);
        var ticketDisposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = SupervisorFactory.Create(healthProbe: new FakeHealthProbe(ready: false),
            options: OneAttempt(),
            launchAdmissions: new FirstTicketHeldRegistry(ticketGate, ticketDisposing));
        try
        {
            var initiator = supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
            await ticketDisposing.Task;

            var dispose = supervisor.DisposeAsync().AsTask();
            await AssertEx.StaysIncompleteAsync(dispose, "Disposal waits out the failed spawn's held ticket.");
            ticketGate.Set();

            await dispose.WaitAsync(TestBudgets.Contended);
            await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => initiator);
        }
        finally
        {
            ticketGate.Set();
        }
    }

    /// <summary>Disposal makes one last kill of a held survivor: a child that still lives keeps its handle (and receipt), one that exits is reclaimed.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Dispose_WithAHeldVetoedSurvivor_KillsItOnceMore(bool exitsOnTheFinalKill)
    {
        var kills = 0;
        FakeProcessTreeHandle? child = null;
        var launcher = new FakeProcessLauncher(_ =>
        {
#pragma warning disable CA2000 // Ownership transfers to the supervisor through the launcher fake.
            child = new FakeProcessTreeHandle(7001,
                exitOnTreeKill: false,
                onTreeKill: () =>
                {
                    if (Interlocked.Increment(ref kills) > 1 && exitsOnTheFinalKill)
                    {
                        child!.SimulateExit();
                    }
                });
#pragma warning restore CA2000
            return child;
        });
        var probe = new PublicationGatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe: probe, options: OneSlot());
        supervisor.VetoedKillExitWait = TimeSpan.Zero;
        var background = EnsureBackgroundAsync(supervisor);
        await probe.Entered;
        await OperatorEjectAsync(supervisor);
        probe.Release();
        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => background);
        var survivor = AssertEx.NotNull(child);
        AssertEx.False(survivor.HasExited || survivor.WasDisposed, "The vetoed child is a held survivor before disposal.");
        var killsBeforeDispose = Volatile.Read(ref kills);

        await supervisor.DisposeAsync();

        AssertEx.Equal(killsBeforeDispose + 1, Volatile.Read(ref kills), "Disposal retries the kill exactly once.");
        AssertEx.Equal(exitsOnTheFinalKill, survivor.WasDisposed,
            exitsOnTheFinalKill ? "A survivor that exits on the final kill is reclaimed." : "A survivor that still lives keeps its handle, and so its receipt.");
    }

    private static LlamaServerSupervisorOptions OneAttempt() =>
        new()
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 3,
            MaxRestartAttempts = 1
        };

    /// <summary>An allocation resolver that always places the experts in system RAM.</summary>
    private static IProcessContextAllocationResolver ExpertOffloadAllocationResolver()
    {
        var allocation = new ProcessContextAllocation
        {
            ProcessContextTokens = 8192,
            ModelTrainContextTokens = null,
            Source = ProcessContextAllocationSource.HardwareTier,
            Placement = ProcessPlacementMode.ExpertOffload,
            Footprint = ResourceFootprint.Zero,
            ContentIdentity = $"{Model}:0",
            CacheKey = "moe-cache"
        };
        var resolver = Substitute.For<IProcessContextAllocationResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<GpuVariant>(), Arg.Any<ResolvedLaunchArguments>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<ProcessContextAllocation?>(allocation));
        return resolver;
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

    /// <summary>
    ///     Parks a background load of <see cref="Model" /> at publication, ejects it and lets the veto land, leaving its child held when
    ///     the launcher's kill does not end it. A load that ends before it parks fails here with its own fault, never as a hang.
    /// </summary>
    private static async Task EjectAParkedBackgroundLoadAsync(LlamaServerProcessSupervisor supervisor, PublicationGatedHealthProbe probe)
    {
        var background = EnsureBackgroundAsync(supervisor);
        try
        {
            var first = await Task.WhenAny(probe.Entered, background).WaitAsync(TestBudgets.Contended);
            if (ReferenceEquals(first, background))
            {
                await background; // A fault surfaces here as itself.
            }

            AssertEx.False(background.IsCompleted, "The background load completed before it parked at publication.");

            await OperatorEjectAsync(supervisor);
        }
        finally
        {
            probe.Release();
        }

        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => background.WaitAsync(TestBudgets.Contended));
    }

    private static Task<LlamaServerEjectOutcome> OperatorEjectAsync(LlamaServerProcessSupervisor supervisor) =>
        supervisor.EjectAsync(Model, ModelRole.Chat, force: false, CancellationToken.None, operatorRequested: true);

    private static Task<LlamaServerEndpoint> EnsureBackgroundAsync(LlamaServerProcessSupervisor supervisor) =>
        supervisor.EnsureRunningAsync(Model, ModelRole.Chat, ModelResidencyIntent.Background, CancellationToken.None);

    /// <summary>
    ///     The real registry, except that the FIRST launch ticket signals, then holds its disposal on a gate the test
    ///     releases, and with <c>throwOnDispose</c> throws once it has released the real ticket.
    /// </summary>
    private sealed class FirstTicketHeldRegistry : IProcessLaunchAdmissionRegistry
    {
        private readonly TaskCompletionSource _disposing;
        private readonly ManualResetEventSlim _gate;
        private readonly ProcessLaunchAdmissionRegistry _inner = new();
        private readonly bool _throwOnDispose;
        private int _tickets;

        public FirstTicketHeldRegistry(ManualResetEventSlim gate, TaskCompletionSource disposing, bool throwOnDispose = false)
        {
            _gate = gate;
            _disposing = disposing;
            _throwOnDispose = throwOnDispose;
        }

        public ProcessLaunchAdmissionSnapshot Snapshot(string modelName, ModelRole role) =>
            _inner.Snapshot(modelName, role);

        public IProcessLaunchAdmissionLease? Acquire(ProcessLaunchAdmission admission) =>
            _inner.Acquire(admission);

        public bool TryAcquire(ProcessLaunchAdmission admission, [NotNullWhen(true)] out IProcessLaunchAdmissionLease? lease) =>
            _inner.TryAcquire(admission, out lease);

        public bool TryBeginLaunch(string modelName, ModelRole role, out ProcessLaunchAdmission? admission, [NotNullWhen(true)] out IProcessLaunchTicket? ticket)
        {
            if (!_inner.TryBeginLaunch(modelName, role, out admission, out var inner))
            {
                ticket = null;
                return false;
            }

            ticket = Interlocked.Increment(ref _tickets) == 1 ? new HeldTicket(inner, _gate, _disposing, _throwOnDispose) : inner;
            return true;
        }

        private sealed class HeldTicket : IProcessLaunchTicket
        {
            private readonly TaskCompletionSource _disposing;
            private readonly ManualResetEventSlim _gate;
            private readonly IProcessLaunchTicket _inner;
            private readonly bool _throwOnDispose;

            public HeldTicket(IProcessLaunchTicket inner, ManualResetEventSlim gate, TaskCompletionSource disposing, bool throwOnDispose)
            {
                _inner = inner;
                _gate = gate;
                _disposing = disposing;
                _throwOnDispose = throwOnDispose;
            }

            public void Dispose()
            {
                _disposing.TrySetResult();

                // A test that never releases the gate fails on its own assertions after the budget.
                _ = _gate.Wait(TestBudgets.Contended);
                _inner.Dispose();
                if (_throwOnDispose)
                {
#pragma warning disable S3877 // A throwing disposal is the fault under test.
                    throw new InvalidOperationException("The launch ticket's disposal failed.");
#pragma warning restore S3877
                }
            }
        }
    }

    /// <summary>Fails the first (optimized) readiness wait, then parks the ready safe retry at its publication point until released.</summary>
    private sealed class SafeRetryPublicationGatedHealthProbe : ILlamaServerHealthProbe
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readinessCalls;

        public Task Entered => _entered.Task;

        public Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct) =>
            Task.FromResult(Interlocked.Increment(ref _readinessCalls) > 1);

        public Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct) =>
            Task.FromResult(true);

        public async Task<int?> ReadEffectiveContextTokensOrNullAsync(Uri baseAddress, CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return null;
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    /// <summary>Reads as never-disabled; the verdict write either throws a fault the policy does not absorb, or holds, ignoring its token, until released.</summary>
    private sealed class HeldOrThrowingFallbackStore : ILlamaServerLaunchFallbackStore
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _hold;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HeldOrThrowingFallbackStore(bool hold)
        {
            _hold = hold;
        }

        public Task Entered => _entered.Task;

        public Task<bool> IsOptimizedConfigDisabledAsync(GpuVariant variant, string kvCacheType, CancellationToken ct) =>
            Task.FromResult(false);

        public async Task DisableOptimizedConfigAsync(GpuVariant variant, string kvCacheType, CancellationToken ct)
        {
            _entered.TrySetResult();
            if (!_hold)
            {
                throw new InvalidOperationException("The launch-fallback store is broken.");
            }

            await _release.Task;
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    /// <summary>Reads as never-disabled; the verdict write blocks its calling thread on a gate BEFORE it returns a task, as stalled synchronous file I/O does.</summary>
    private sealed class SynchronouslyBlockingFallbackStore : ILlamaServerLaunchFallbackStore
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _gate;
        private int _returned;

        public SynchronouslyBlockingFallbackStore(ManualResetEventSlim gate)
        {
            _gate = gate;
        }

        public Task Entered => _entered.Task;

        public bool Returned => Volatile.Read(ref _returned) != 0;

        public Task<bool> IsOptimizedConfigDisabledAsync(GpuVariant variant, string kvCacheType, CancellationToken ct) =>
            Task.FromResult(false);

        public Task DisableOptimizedConfigAsync(GpuVariant variant, string kvCacheType, CancellationToken ct)
        {
            _entered.TrySetResult();
            // Ignores its token, as stalled synchronous file I/O does.
            _ = _gate.Wait(TestBudgets.Contended, CancellationToken.None);
            Volatile.Write(ref _returned, value: 1);
            return Task.CompletedTask;
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
