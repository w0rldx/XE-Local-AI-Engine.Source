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
///     respawned instead of handed out forever).
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
    public async Task EnsureRunning_CapFullOfActiveDaemons_NewDistinctModel_Rejects()
    {
        var launcher = new FakeImageProcessLauncher();
        var time = new AdvanceableClock();
        var options = new StableDiffusionRuntimeOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 1
        };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, options: options, timeProvider: time);

        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None); // fills the cap of 1 (fresh, in-use)

        // A second distinct model has no idle victim to evict → reject at start.
        var ex = await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() =>
            supervisor.EnsureRunningAsync("flux", CancellationToken.None));

        AssertEx.Contains(ex.Message, "maximum number of local image models", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(expected: 1, launcher.LaunchCount); // flux never launched.
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
        // GPTAUD-10a: a daemon with an active job lease (an in-flight generation) is never evicted, even past the idle
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
        // GPTAUD-10b: a spawn registers into _processes only after readiness, so a DisposeAsync that races the spawn tears
        // down only the daemons in its snapshot — a process launched-but-not-yet-registered would leak. Linking readiness
        // to the shutdown token makes dispose cancel the readiness wait, and the spawn's catch tree-kills the handle.
        var launcher = new FakeImageProcessLauncher();
        var probe = new GatedImageReadinessProbe();
        var supervisor = new ImageServerProcessSupervisor(
            new FakeImageModelStore(),
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
}
