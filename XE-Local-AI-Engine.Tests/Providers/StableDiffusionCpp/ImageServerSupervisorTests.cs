namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
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
}
