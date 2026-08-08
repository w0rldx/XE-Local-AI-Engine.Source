namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the image supervisor's loaded cap + idle-LRU eviction (a new distinct model is rejected when the cap is
///     full of in-use daemons, admitted by evicting an idle LRU when one is past the TTL) and its reuse-path wedged-daemon
///     guard (a still-alive daemon that fails the rate-limited liveness probe N consecutive times is torn down and
///     respawned instead of handed out forever), plus how far the admission gate reaches: it covers the cap decision
///     and the port set only, never an evicted victim's tree-kill.
/// </summary>
public sealed class ImageServerSupervisorTests
{
    [Test]
    public async Task EnsureRunning_ReusesRunningDaemon_NoSecondSpawn()
    {
        var launcher = new FakeImageProcessLauncher();
        await using var supervisor = ImageSupervisorFactory.Create(launcher);

        var first = await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);
        var second = await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.Equal(first.BaseAddress.AbsoluteUri, second.BaseAddress.AbsoluteUri);
    }

    [Test]
    public async Task EnsureRunning_ReuseWithinProbeInterval_DoesNotProbe()
    {
        // A reuse inside the liveness-probe interval is handed out immediately with no HTTP probe (cheap hot path).
        var launcher = new FakeImageProcessLauncher();
        var probe = new FakeImageReadinessProbe();
        await using var supervisor = ImageSupervisorFactory.Create(launcher, probe);

        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None); // spawn
        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None); // reuse, still within interval

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.Equal(expected: 0, probe.ResponsiveChecks);
    }

    [Test]
    public async Task EnsureRunning_WedgedDaemon_AfterConsecutiveProbeFailures_TearsDownAndRespawns()
    {
        var launcher = new FakeImageProcessLauncher();
        var probe = new FakeImageReadinessProbe();
        var time = new AdvanceableClock();
        var options = new StableDiffusionRuntimeOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 2,
            ReuseLivenessProbeInterval = TimeSpan.FromSeconds(5),
            MaxReuseLivenessFailures = 3
        };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, probe, options: options, timeProvider: time);

        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None); // spawn 1
        var firstHandle = launcher.Handles.Single();

        // The daemon wedges: alive, but no longer answers the liveness probe.
        probe.Responsive = false;

        // Each reuse past the probe interval issues one liveness probe. The first two failures stay under the threshold
        // (the daemon is still handed out); the third trips the wedged guard and respawns.
        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(6)); // clear the rate-limit window so this reuse probes
            await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);
        }

        AssertEx.Equal(expected: 3, probe.ResponsiveChecks);
        AssertEx.Equal(expected: 2, launcher.LaunchCount); // wedged daemon respawned exactly once.
        AssertEx.True(firstHandle.WasTreeKilled, "The wedged daemon should have been torn down.");
    }

    [Test]
    public async Task EnsureRunning_TransientProbeFailure_ThenRecovers_DoesNotRespawn()
    {
        var launcher = new FakeImageProcessLauncher();
        var probe = new FakeImageReadinessProbe();
        var time = new AdvanceableClock();
        var options = new StableDiffusionRuntimeOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 2,
            ReuseLivenessProbeInterval = TimeSpan.FromSeconds(5),
            MaxReuseLivenessFailures = 3
        };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, probe, options: options, timeProvider: time);

        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None); // spawn

        // Two consecutive failures (under the threshold) then a success resets the counter — no respawn.
        probe.Responsive = false;
        time.Advance(TimeSpan.FromSeconds(6));
        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(6));
        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);

        probe.Responsive = true; // recovered
        time.Advance(TimeSpan.FromSeconds(6));
        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);

        AssertEx.Equal(expected: 1, launcher.LaunchCount); // never respawned.
    }

    [Test]
    public async Task EnsureRunning_CapFullOfUnleasedDaemon_NewDistinctModel_EvictsAndAdmits()
    {
        // Switching image models must work immediately. With MaxLoadedProcesses = 1 the cap is reached the moment ANY
        // daemon is resident, so gating admission on the idle TTL would turn that TTL into a fifteen-minute window in
        // which a second model simply cannot be loaded — and the app exposes no unload path out of it. The resident
        // daemon here is fresh and well inside the TTL, but it holds no job lease, so nothing is in flight and it is
        // evicted to admit the new model.
        var launcher = new FakeImageProcessLauncher();
        var time = new AdvanceableClock();
        var options = new StableDiffusionRuntimeOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 1
        };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, options: options, timeProvider: time);

        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None); // fills the cap of 1 (fresh, unleased)
        var firstHandle = launcher.Handles.Single();

        await supervisor.EnsureRunningAsync("flux", CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(firstHandle.WasTreeKilled, "an unleased in-window daemon should be evicted to admit a switch.");
    }

    [Test]
    public async Task EnsureRunning_CapFull_PrefersThePastTtlVictimOverAnInWindowOne()
    {
        // With room for two daemons and both unleased, the one past its idle TTL is the victim — the in-window fallback
        // is a last resort, not the first choice, so a warm daemon is only sacrificed when there is nothing colder.
        var launcher = new FakeImageProcessLauncher();
        var time = new AdvanceableClock();
        var ttl = TimeSpan.FromMinutes(15);
        var options = new StableDiffusionRuntimeOptions
        {
            IdleTimeToLive = ttl,
            MaxLoadedProcesses = 2
        };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, options: options, timeProvider: time);

        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);
        var coldHandle = launcher.Handles.Single();

        // sd15 ages past the TTL; sdxl is then loaded fresh, so it is the more-recently-used but in-window daemon.
        time.Advance(ttl + TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("sdxl", CancellationToken.None);
        var warmHandle = launcher.Handles.Single(handle => !ReferenceEquals(handle, coldHandle));

        await supervisor.EnsureRunningAsync("flux", CancellationToken.None);

        AssertEx.Equal(expected: 3, launcher.LaunchCount);
        AssertEx.True(coldHandle.WasTreeKilled, "the past-TTL daemon is the preferred cap-eviction victim.");
        AssertEx.False(warmHandle.WasTreeKilled, "an in-window daemon must not be evicted while a colder one exists.");
    }

    [Test]
    public async Task EnsureRunning_CapFull_ButLruIsIdlePastTtl_EvictsLru_AndAdmitsNewModel()
    {
        var launcher = new FakeImageProcessLauncher();
        var time = new AdvanceableClock();
        var ttl = TimeSpan.FromMinutes(15);
        var options = new StableDiffusionRuntimeOptions
        {
            IdleTimeToLive = ttl,
            MaxLoadedProcesses = 1
        };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, options: options, timeProvider: time);

        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);
        var firstHandle = launcher.Handles.Single();

        // Push the resident daemon past the idle TTL so it is an eligible eviction victim.
        time.Advance(ttl + TimeSpan.FromMinutes(1));

        await supervisor.EnsureRunningAsync("flux", CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount); // flux spawned after evicting the idle victim.
        AssertEx.True(firstHandle.WasTreeKilled, "Idle LRU victim should have been tree-killed.");
    }

    [Test]
    public async Task Restart_EvictsOutgoingDaemon_AndRespawns_EvenAtCapOne()
    {
        // Restart evicts the outgoing daemon before allocating, so it never trips its own cap.
        var launcher = new FakeImageProcessLauncher();
        var options = new StableDiffusionRuntimeOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 1
        };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, options: options);

        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);
        var firstHandle = launcher.Handles.Single();

        await supervisor.RestartAsync("sd15", CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(firstHandle.WasTreeKilled, "The outgoing daemon should have been torn down on restart.");
    }

    [Test]
    public async Task LeasedDaemon_PastTtl_IsNotEvicted_UntilLeaseReleased()
    {
        // A daemon with an active job lease (an in-flight generation) is never evicted, even past the idle
        // TTL — LastUsedUtc is stamped per ensure/reuse, not per generation step, so a long job looks idle. At cap, a new
        // model is rejected rather than killing the leased daemon; once the lease is released the LRU evictor reclaims it.
        var launcher = new FakeImageProcessLauncher();
        var time = new AdvanceableClock();
        var ttl = TimeSpan.FromMinutes(15);
        var options = new StableDiffusionRuntimeOptions
        {
            IdleTimeToLive = ttl,
            MaxLoadedProcesses = 1
        };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, options: options, timeProvider: time);

        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);
        var firstHandle = launcher.Handles.Single();
        var lease = supervisor.TryAcquireJobLease("sd15");
        AssertEx.NotNull(lease);

        // Past the TTL, but the daemon is leased (mid-generation): a new distinct model finds no evictable victim → reject.
        time.Advance(ttl + TimeSpan.FromMinutes(1));
        var ex = await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() =>
            supervisor.EnsureRunningAsync("flux", CancellationToken.None));
        AssertEx.Contains(ex.Message, "maximum number of local image models", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(firstHandle.WasTreeKilled, "a leased daemon must never be evicted mid-generation, even past the TTL.");
        AssertEx.Equal(expected: 1, launcher.LaunchCount);

        // Release the lease → the now-idle past-TTL daemon becomes an eligible LRU victim → the new model is admitted.
        lease!.Dispose();
        await supervisor.EnsureRunningAsync("flux", CancellationToken.None);
        AssertEx.True(firstHandle.WasTreeKilled, "once the lease is released the idle past-TTL daemon should be evicted.");
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
    }

    [Test]
    public async Task TryAcquireJobLease_AfterDaemonEvicted_ReturnsNullLeaseless()
    {
        // Once a daemon has been evicted (torn down), a lease attempt must refuse leaselessly rather than
        // hand back a lease over a dead/removed daemon. Lease acquisition and eviction share an atomic latch, so a lease
        // can never be granted over a daemon that eviction has claimed or removed.
        var launcher = new FakeImageProcessLauncher();
        var time = new AdvanceableClock();
        var ttl = TimeSpan.FromMinutes(15);
        var options = new StableDiffusionRuntimeOptions
        {
            IdleTimeToLive = ttl,
            MaxLoadedProcesses = 1
        };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, options: options, timeProvider: time);

        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);
        var firstHandle = launcher.Handles.Single();

        // Idle past the TTL, then admit a distinct model at cap 1 → the idle LRU daemon is atomically latched and evicted.
        time.Advance(ttl + TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("flux", CancellationToken.None);
        AssertEx.True(firstHandle.WasTreeKilled, "the idle past-TTL daemon should have been evicted to admit the new model.");

        var lease = supervisor.TryAcquireJobLease("sd15");
        AssertEx.Null(lease);
    }

    [Test]
    public async Task JobLease_TouchRefreshesIdleClock_KeepingDaemonAlivePastOriginalTtl()
    {
        // A long generation Touch()es its lease each poll; the refreshed LastUsedUtc keeps the daemon inside the idle
        // window so even a released-but-recently-touched daemon is not immediately reclaimed at cap.
        var launcher = new FakeImageProcessLauncher();
        var time = new AdvanceableClock();
        var ttl = TimeSpan.FromMinutes(15);
        var options = new StableDiffusionRuntimeOptions
        {
            IdleTimeToLive = ttl,
            MaxLoadedProcesses = 1
        };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, options: options, timeProvider: time);

        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);
        var firstHandle = launcher.Handles.Single();
        using var lease = supervisor.TryAcquireJobLease("sd15");

        // Advance most of the TTL, then Touch — the daemon is now recently-used again.
        time.Advance(ttl - TimeSpan.FromMinutes(1));
        lease!.Touch();
        time.Advance(TimeSpan.FromMinutes(2)); // 1 min past the ORIGINAL window, but only 2 min since the Touch.

        // A new model at cap finds the touched daemon still in-window (and leased) → reject, daemon survives.
        await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() =>
            supervisor.EnsureRunningAsync("flux", CancellationToken.None));
        AssertEx.False(firstHandle.WasTreeKilled);
    }

    [Test]
    public async Task Dispose_DuringBlockedReadiness_TreeKillsSpawnedDaemon_NoOrphan()
    {
        // A spawn registers into _processes only after readiness, so a DisposeAsync that races the spawn tears
        // down only the daemons in its snapshot — a process launched-but-not-yet-registered would leak. Linking readiness
        // to the shutdown token makes dispose cancel the readiness wait, and the spawn's catch tree-kills the handle.
        var launcher = new FakeImageProcessLauncher();
        var probe = new GatedImageReadinessProbe();
        var supervisor = new ImageServerProcessSupervisor(new FakeImageModelStore(),
            new FakeSdBackendSelector(),
            new FakeSdBinaryManager(),
            launcher,
            probe,
            new StableDiffusionRuntimeOptions
            {
                IdleTimeToLive = TimeSpan.FromHours(1),
                MaxLoadedProcesses = 2
            });

        var ensure = supervisor.EnsureRunningAsync("sd15", CancellationToken.None);
        await probe.ReadinessEntered; // the handle is launched and the spawn is blocked in its readiness wait.

        await supervisor.DisposeAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => ensure);
        var handle = launcher.Handles.Single();
        AssertEx.True(handle.WasTreeKilled, "a daemon launched but not yet registered must be tree-killed on dispose (no orphan).");
    }

    [Test]
    public async Task Admission_SlowEvictionTreeKill_DoesNotHoldTheAdmissionGate()
    {
        // The admission gate covers the cap decision and the port set only. An evicted victim's tree-kill — seconds for
        // a multi-GB image model — runs after the gate is released, so it cannot serialize an unrelated model's port
        // allocation or release behind it.
        using var victimKill = new ImageKillLatch();
        var launcher = new LatchingImageLauncher(victimKill);
        var time = new AdvanceableClock();
        var ttl = TimeSpan.FromHours(1);
        await using var supervisor = ImageSupervisorFactory.Create(launcher,
            options: new StableDiffusionRuntimeOptions
            {
                IdleTimeToLive = ttl,
                MaxLoadedProcesses = 2
            },
            timeProvider: time);

        // sd15 becomes the idle-past-TTL victim; sdxl fills the cap and stays in-window.
        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);
        var victim = launcher.Victim!;
        time.Advance(ttl + TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("sdxl", CancellationToken.None);

        // flux is admitted by evicting sd15, whose tree-kill then blocks for as long as this test wants.
        var admitting = supervisor.EnsureRunningAsync("flux", CancellationToken.None);
        await AssertEx.EventuallyAsync(() => victimKill.Entered, TimeSpan.FromSeconds(3), "The victim's tree-kill never started.");

        // An unrelated model's teardown takes the same admission gate. Held across the tree-kill (as it was), this would
        // block for the whole multi-GB kill; it must not.
        await supervisor.EvictAsync("sdxl", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        victimKill.Release();
        await admitting.WaitAsync(TimeSpan.FromSeconds(3));
        AssertEx.True(victim.WasTreeKilled, "The evicted victim must still be torn down.");
    }

    /// <summary>Readiness probe that signals when its wait is entered, then blocks until its (shutdown-linked) token cancels.</summary>
    private sealed class GatedImageReadinessProbe : IImageServerReadinessProbe
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadinessEntered => _entered.Task;

        public async Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return true;
        }

        public Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct)
        {
            return Task.FromResult(true);
        }
    }

    /// <summary>A tree-kill the test can hold open, standing in for the seconds a multi-GB image model takes to die.</summary>
    private sealed class ImageKillLatch : IDisposable
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

    /// <summary>
    ///     Hands the FIRST launch a handle whose tree-kill blocks on <paramref name="firstKill" />; the rest are
    ///     ordinary. The shared <see cref="FakeImageProcessHandle" /> kills instantly and so cannot express a slow kill.
    /// </summary>
    private sealed class LatchingImageLauncher(ImageKillLatch firstKill) : IImageServerProcessLauncher
    {
        private int _nextPid;

        /// <summary>The first launched daemon — the one this file's admission test evicts.</summary>
        public LatchedImageProcessHandle? Victim { get; private set; }

        public IImageServerProcessHandle Launch(ImageServerLaunchSpec spec)
        {
            var pid = Interlocked.Increment(ref _nextPid);
#pragma warning disable CA2000 // Ownership of the handle transfers to the supervisor under test, which disposes it on teardown.
            var handle = new LatchedImageProcessHandle(pid, pid == 1 ? firstKill : null);
#pragma warning restore CA2000
            Victim ??= handle;
            return handle;
        }
    }

    private sealed class LatchedImageProcessHandle(int pid, ImageKillLatch? killLatch) : IImageServerProcessHandle
    {
        private int _exited;
        private int _killed;

        public bool WasTreeKilled => Volatile.Read(ref _killed) != 0;

        public int ProcessId { get; } = pid;

        public bool HasExited => Volatile.Read(ref _exited) != 0;

        public void TreeKill()
        {
            killLatch?.Wait();
            Interlocked.Exchange(ref _killed, value: 1);
            Interlocked.Exchange(ref _exited, value: 1);
        }

        public void Dispose()
        {
            // No unmanaged resources in the double.
        }
    }
}
