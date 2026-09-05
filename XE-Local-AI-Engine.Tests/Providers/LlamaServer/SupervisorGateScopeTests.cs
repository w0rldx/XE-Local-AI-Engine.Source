namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Guards how far each supervisor-wide gate reaches, which is what makes concurrent roles independent.
/// </summary>
/// <remarks>
///     <para>
///         The RUNTIME gate is shared/exclusive: ordinary ensures take it shared, so one role's stalled reuse liveness
///         probe (up to <see cref="LlamaServerSupervisorOptions.ReuseLivenessProbeTimeout" />) no longer head-of-line
///         blocks another role's ensure — the workload that makes this matter is background knowledge-base indexing
///         alongside chat. Both exclusion directions against an operator runtime mutation are preserved: a mutation
///         waits for in-flight ensures (covered here) and ensures wait for a held mutation lease (covered by
///         <c>SupervisorRaceTests.RuntimeMutationLease_WhileHeld_BlocksEnsureUntilDisposed</c>).
///     </para>
///     <para>
///         The ADMISSION gate covers the cap decision and the port set only. An evicted victim's tree-kill — seconds
///         for a multi-GB model — runs after the gate is released, so it cannot serialize an unrelated model's port
///         allocation or release.
///     </para>
///     <para>
///         The <c>-lv 4</c> startup-capture sink is detached at readiness (same latch as the log-demotion window), so
///         serving-time lines stop paying a lock and a string copy into a buffer nothing reads again.
///     </para>
/// </remarks>
public sealed class SupervisorGateScopeTests
{
    [Test]
    public async Task EnsureRunning_StalledReuseProbe_DoesNotBlockAnotherRolesEnsure()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new GatedLivenessProbe();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, probe, options: LongProbeTimeoutOptions(), timeProvider: time);
        await supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, CancellationToken.None);

        // Past the rate-limit interval, so the next reuse claims a liveness probe — and parks inside it.
        time.Advance(TimeSpan.FromSeconds(10));
        var stalledReuse = supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => probe.Probing == 1, TimeSpan.FromSeconds(3), "The reuse liveness probe never started.");

        // Background indexing's embedding ensure: a different role and a different process, so it must not queue behind
        // the stalled chat probe.
        await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(3));

        probe.Release();
        await stalledReuse.WaitAsync(TimeSpan.FromSeconds(3));
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
    }

    [Test]
    public async Task RuntimeMutationLease_WaitsForAnEnsureAlreadyInsideTheGate()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new GatedLivenessProbe();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, probe, options: LongProbeTimeoutOptions(), timeProvider: time);
        await supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(10));
        var stalledReuse = supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => probe.Probing == 1, TimeSpan.FromSeconds(3), "The reuse liveness probe never started.");

        var mutation = supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(mutation,
            "A runtime mutation must not even decide until every ensure already inside the gate has left it.");

        probe.Release();
        await stalledReuse.WaitAsync(TimeSpan.FromSeconds(3));

        // Refused, because a process is running — the assertion above is that the decision waited, not its outcome.
        AssertEx.Null(await mutation.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Test]
    public async Task Admission_SlowEvictionTreeKill_DoesNotHoldTheAdmissionGate()
    {
        using var victimKill = new KillLatch();
        var launcher = new SwitchableLauncher(victimKill);
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromHours(1);
        await using var supervisor = CreateSupervisor(launcher,
            new LlamaServerSupervisorOptions
            {
                IdleTimeToLive = ttl,
                MaxLoadedProcesses = 2,
                MaxRestartAttempts = 3
            },
            time);

        // model-a becomes the idle-past-TTL victim; model-b fills the cap and stays in-window.
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(ttl + TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None);

        // model-c is admitted by evicting model-a, whose tree-kill then blocks for as long as this test wants.
        var admitting = supervisor.EnsureRunningAsync("model-c", ModelRole.Chat, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => victimKill.Entered, TimeSpan.FromSeconds(3), "The victim's tree-kill never started.");

        // An unrelated model's teardown takes the same admission gate. Held across the tree-kill (as it was), this
        // would block for the whole multi-GB kill; it must not.
        await supervisor.EvictAsync("model-b", ModelRole.Chat, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        victimKill.Release();
        await admitting.WaitAsync(TimeSpan.FromSeconds(3));
        AssertEx.True(launcher.Handles.First(handle => handle.ProcessId == 1).WasTreeKilled, "The evicted victim must still be torn down.");
    }

    [Test]
    public async Task EnsureRunning_OnceServing_ClosesTheAutomaticStartupCaptureWindow()
    {
        var report = new RecordingLayerPlacementReport();
        var launcher = new FakeProcessLauncher
        {
            StartupLines = ["load_tensors: offloaded 25/25 layers to GPU"]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            layerPlacementReport: report);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        // The window was open for the whole load: the placement banner reached the sniffer.
        AssertEx.Equal(expected: 1, report.RecordCount);

        var spec = launcher.Launches.Single();
        AssertEx.True(spec.ShouldDemoteForwardedLines!(),
            "Readiness must latch the serving window — the one latch that both demotes forwarded lines and detaches the automatic capture.");

        // The launcher captured the sink delegate at process start, so serving-time lines still reach it; the sink
        // itself must now drop them rather than copy each into a buffer nothing reads again. Both buffers behind the
        // sink are write-only after readiness (which is exactly why detaching is safe), so this asserts the reachable
        // half: the sink is still callable and nothing downstream moves.
        spec.StartupCapture!("load_tensors: offloaded 1/25 layers to GPU");
        AssertEx.Equal(expected: 1, report.RecordCount);
    }

    private static LlamaServerSupervisorOptions LongProbeTimeoutOptions()
    {
        return new LlamaServerSupervisorOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 3,
            MaxRestartAttempts = 3,

            // Long enough that the parked probe below stays parked until the test releases it, rather than falling out
            // through its own timeout and quietly turning the test green for the wrong reason.
            ReuseLivenessProbeTimeout = TimeSpan.FromSeconds(30)
        };
    }

    private static LlamaServerProcessSupervisor CreateSupervisor(ILlamaServerProcessLauncher launcher,
        LlamaServerSupervisorOptions options,
        TimeProvider timeProvider)
    {
        // Built directly rather than through SupervisorFactory: this file's launcher hands back a handle whose
        // tree-kill blocks, which the shared FakeProcessLauncher cannot produce.
        return new LlamaServerProcessSupervisor(new FakeBinaryManager(),
            new FakeVariantSelector(),
            new FakeModelStore(),
            launcher,
            new FakeHealthProbe(),
            new FakeLlamaServerCapabilityManifestProbe(),
            options,
            new FakeInferenceProfileResolver(),
            new LlamaServerLaunchPolicy(new LlamaServerLaunchPolicyOptions(), new FakeLaunchFallbackStore()),
            timeProvider: timeProvider);
    }

    /// <summary>Health probe whose REUSE liveness probe parks on a gate the test releases; readiness is immediate.</summary>
    private sealed class GatedLivenessProbe : ILlamaServerHealthProbe
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _probing;

        /// <summary>Number of reuse liveness probes currently parked.</summary>
        public int Probing => Volatile.Read(ref _probing);

        public Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
        {
            return Task.FromResult(true);
        }

        public async Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct)
        {
            Interlocked.Increment(ref _probing);
            try
            {
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _probing);
            }
        }

        public Task<int?> TryReadEffectiveContextTokensAsync(Uri baseAddress, CancellationToken ct)
        {
            return Task.FromResult<int?>(null);
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    /// <summary>A tree-kill the test can hold open, standing in for the seconds a multi-GB model takes to die.</summary>
    private sealed class KillLatch : IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(initialState: false);
        private int _entered;

        public bool Entered => Volatile.Read(ref _entered) != 0;

        public void Wait()
        {
            Interlocked.Exchange(ref _entered, value: 1);
            _gate.Wait(TimeSpan.FromSeconds(10));
        }

        public void Release()
        {
            _gate.Set();
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }

    /// <summary>Hands the FIRST launch a handle whose tree-kill blocks on <paramref name="firstKill" />; the rest are ordinary.</summary>
    private sealed class SwitchableLauncher(KillLatch firstKill) : ILlamaServerProcessLauncher
    {
        private int _nextPid;

        public ConcurrentBag<LatchedProcessHandle> Handles { get; } = new();

        public ILlamaServerProcessHandle Launch(LlamaServerLaunchSpec spec)
        {
            var pid = Interlocked.Increment(ref _nextPid);
#pragma warning disable CA2000 // Ownership of the handle transfers to the supervisor under test, which disposes it on teardown.
            var handle = new LatchedProcessHandle(pid, pid == 1 ? firstKill : null);
#pragma warning restore CA2000
            Handles.Add(handle);
            return handle;
        }
    }

    private sealed class LatchedProcessHandle(int pid, KillLatch? killLatch) : ILlamaServerProcessHandle
    {
        private readonly TaskCompletionSource _exitSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _exited;
        private int _killed;

        public bool WasTreeKilled => Volatile.Read(ref _killed) != 0;

        public int ProcessId { get; } = pid;

        public bool HasExited => Volatile.Read(ref _exited) != 0;

        public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                await _exitSignal.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public void TreeKill()
        {
            killLatch?.Wait();
            Interlocked.Exchange(ref _killed, value: 1);
            Interlocked.Exchange(ref _exited, value: 1);
            _exitSignal.TrySetResult();
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Counts placement records so a test can tell whether a forwarded line still reached the sniffer.</summary>
    private sealed class RecordingLayerPlacementReport : ILlamaLayerPlacementReport
    {
        private int _recordCount;

        public int RecordCount => Volatile.Read(ref _recordCount);

        public LlamaLayerPlacement? Current { get; private set; }

        public void Record(ModelRole role, GpuVariant variant, string modelName, int offloadedLayers, int totalLayers)
        {
            Interlocked.Increment(ref _recordCount);
            Current = new LlamaLayerPlacement(modelName, role, offloadedLayers, totalLayers);
        }

        public void Remove(ModelRole role, string modelName)
        {
            Current = null;
        }
    }
}
