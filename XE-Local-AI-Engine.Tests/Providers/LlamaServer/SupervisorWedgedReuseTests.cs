namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the reuse-path wedged-server guard: a still-alive <c>llama-server</c> that stops answering the liveness
///     probe (deadlocked, not exited) is torn down and respawned after
///     <see cref="LlamaServerSupervisorOptions.MaxReuseLivenessFailures" /> consecutive failed probes, instead of being
///     handed to every request forever (each reuse otherwise refreshes LastUsedUtc, so the idle reaper never sees it).
///     The probe is rate-limited, so a reuse inside the interval is handed out with no HTTP probe.
/// </summary>
public sealed class SupervisorWedgedReuseTests
{
    private static LlamaServerSupervisorOptions OptionsWith(TimeSpan interval, int maxFailures)
    {
        return new LlamaServerSupervisorOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 3,
            MaxRestartAttempts = 3,
            ReuseLivenessProbeInterval = interval,
            MaxReuseLivenessFailures = maxFailures
        };
    }

    [Test]
    public async Task EnsureRunning_ReuseWithinProbeInterval_ReusesWithoutProbe()
    {
        var launcher = new FakeProcessLauncher();
        // Even an unresponsive probe must NOT be consulted within the interval — the reuse is immediate.
        var probe = new FakeHealthProbe(ready: true, responsive: false);
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher,
            probe,
            options: OptionsWith(TimeSpan.FromSeconds(5), maxFailures: 3),
            timeProvider: time);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None); // spawn
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None); // reuse within interval

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }

    [Test]
    public async Task EnsureRunning_WedgedServer_AfterConsecutiveProbeFailures_TearsDownAndRespawns()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new FakeHealthProbe(ready: true, responsive: true);
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher,
            probe,
            options: OptionsWith(TimeSpan.FromSeconds(5), maxFailures: 3),
            timeProvider: time);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None); // spawn 1
        var firstHandle = launcher.Handles.OrderBy(h => h.ProcessId).First();

        // The server wedges: alive, but stops answering /health.
        probe.Responsive = false;

        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(6)); // clear the rate-limit window so this reuse probes
            await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        }

        AssertEx.Equal(expected: 2, launcher.LaunchCount); // wedged server respawned exactly once.
        AssertEx.True(firstHandle.WasTreeKilled, "The wedged server should have been torn down.");
    }

    [Test]
    public async Task EnsureRunning_TransientProbeFailure_ThenRecovers_DoesNotRespawn()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new FakeHealthProbe(ready: true, responsive: true);
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher,
            probe,
            options: OptionsWith(TimeSpan.FromSeconds(5), maxFailures: 3),
            timeProvider: time);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None); // spawn

        // Two failures (under threshold) then a success resets the counter — no respawn.
        probe.Responsive = false;
        time.Advance(TimeSpan.FromSeconds(6));
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(6));
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        probe.Responsive = true; // recovered
        time.Advance(TimeSpan.FromSeconds(6));
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 1, launcher.LaunchCount); // never respawned.
    }
}
