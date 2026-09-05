namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Lifecycle behaviours (Audit-4) added on top of the base supervisor tests: the DETACHED model load
///     (caller cancellation abandons its wait but the load continues and warms the model for the next send), the
///     size-aware / limited-retry readiness-timeout classification, and the graceful/force operator eject with bounded
///     in-flight lease drain.
/// </summary>
public sealed class SupervisorLifecycleTests
{
    private static LlamaServerSupervisorOptions OptionsWithDrain(TimeSpan drainTimeout)
    {
        return new LlamaServerSupervisorOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 3,
            MaxRestartAttempts = 3,
            EjectDrainTimeout = drainTimeout
        };
    }

    [Test]
    public async Task EnsureRunning_CallerCancelledDuringReadiness_LoadContinuesDetached_SecondEnsureReuses()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new GatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher, probe);

        using var cts = new CancellationTokenSource();
        var first = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, cts.Token);

        // The spawn is parked in readiness (the load is in flight).
        await AssertEx.EventuallyAsync(() => probe.Waiting == 1, TimeSpan.FromSeconds(5), "The spawn never reached readiness.");

        // Cancel the caller's wait: the caller aborts, but the detached load must keep going.
        await cts.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => first);

        // The load is still parked (NOT aborted by the caller cancellation) — single-flight state is intact.
        AssertEx.Equal(expected: 1, probe.Waiting);
        AssertEx.Equal(expected: 1, launcher.LaunchCount);

        // Let the load finish; it registers a warm process.
        probe.Release();
        await AssertEx.EventuallyAsync(() => supervisor.CountRunningProcesses() == 1, TimeSpan.FromSeconds(5), "The detached load never completed.");

        // A second ensure (uncancelled) reuses the now-warm process: still exactly one spawn.
        var second = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        AssertEx.NotNull(second);
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }

    [Test]
    public async Task EnsureRunning_ConcurrentSameKeyWhileParked_SpawnsExactlyOnce()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new GatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher, probe);

        // Fire many concurrent ensures for the same key while the first spawn is held in readiness.
        var calls = Enumerable.Range(start: 0, count: 16)
                              .Select(_ => supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None))
                              .ToArray();

        await AssertEx.EventuallyAsync(() => probe.Waiting == 1, TimeSpan.FromSeconds(5), "The single-flight spawn never parked.");
        AssertEx.Equal(expected: 1, launcher.LaunchCount);

        probe.Release();
        var endpoints = await Task.WhenAll(calls);

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        var first = endpoints[0].BaseAddress.AbsoluteUri;
        AssertEx.True(endpoints.All(e => string.Equals(e.BaseAddress.AbsoluteUri, first, StringComparison.Ordinal)));
    }

    [Test]
    public async Task EnsureRunning_ReadinessTimeout_RetriesAtMostConfigured_IndependentOfRestartCap()
    {
        var launcher = new FakeProcessLauncher();
        // Never ready → every spawn attempt is a readiness timeout (not a process crash).
        await using var supervisor = SupervisorFactory.Create(launcher,
            new FakeHealthProbe(ready: false),
            options: new LlamaServerSupervisorOptions
            {
                IdleTimeToLive = TimeSpan.FromHours(1),
                MaxRestartAttempts = 5, // deliberately high
                MaxReadinessTimeoutRetries = 1 // but a readiness timeout retries at most once → 2 spawns total
            });

        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None));

        AssertEx.Equal(expected: 2, launcher.LaunchCount); // initial + one readiness retry, NOT the restart cap (5).
        AssertEx.Contains(ex.Message, "did not become ready", StringComparison.OrdinalIgnoreCase);
        AssertEx.True(launcher.Handles.All(h => h.WasTreeKilled), "Each timed-out spawn must be torn down.");
    }

    [Test]
    public async Task Eject_IdleProcess_TearsDownImmediately_Ejected()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        var outcome = await supervisor.EjectAsync("model-a", ModelRole.Chat, force: false, CancellationToken.None);

        AssertEx.Equal(LlamaServerEjectOutcome.Ejected, outcome);
        AssertEx.True(launcher.Handles.Single().WasTreeKilled, "An idle eject should tree-kill the process.");

        // The process is gone: a subsequent ensure spawns fresh.
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
    }

    [Test]
    public async Task Eject_NotRunning_ReturnsNotRunning()
    {
        await using var supervisor = SupervisorFactory.Create();

        var outcome = await supervisor.EjectAsync("ghost", ModelRole.Chat, force: false, CancellationToken.None);

        AssertEx.Equal(LlamaServerEjectOutcome.NotRunning, outcome);
    }

    [Test]
    public async Task Eject_ActiveLease_DrainsThenEjects()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher, options: OptionsWithDrain(TimeSpan.FromSeconds(5)));
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        var lease = supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).Lease;
        AssertEx.NotNull(lease);

        var ejectTask = supervisor.EjectAsync("model-a", ModelRole.Chat, force: false, CancellationToken.None);

        // While the lease is held the eject is still draining (not complete, process not yet killed).
        await AssertEx.StaysIncompleteAsync(ejectTask, "The eject should still be draining while a lease is held.");
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "The process must not be killed while draining.");

        lease!.Dispose(); // releasing the lease lets the drain complete.
        var outcome = await ejectTask;

        AssertEx.Equal(LlamaServerEjectOutcome.Ejected, outcome);
        AssertEx.True(launcher.Handles.Single().WasTreeKilled, "After the drain completes the process is torn down.");
    }

    [Test]
    public async Task Eject_ActiveLease_DrainTimesOut_NotForced_LeftRunning()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher, options: OptionsWithDrain(TimeSpan.FromMilliseconds(150)));
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        using var lease = supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).Lease;
        AssertEx.NotNull(lease);

        var outcome = await supervisor.EjectAsync("model-a", ModelRole.Chat, force: false, CancellationToken.None);

        AssertEx.Equal(LlamaServerEjectOutcome.TimedOutStillBusy, outcome);
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "A timed-out GRACEFUL eject must NOT silently kill the process.");

        // Still registered and usable: a subsequent ensure reuses it (no respawn).
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }

    [Test]
    public async Task Eject_Force_ActiveLease_TearsDown_AndMarksLeaseEjected()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher, options: OptionsWithDrain(TimeSpan.FromMilliseconds(150)));
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        using var lease = supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).Lease;
        AssertEx.NotNull(lease);

        var outcome = await supervisor.EjectAsync("model-a", ModelRole.Chat, force: true, CancellationToken.None);

        AssertEx.Equal(LlamaServerEjectOutcome.ForcedWhileBusy, outcome);
        AssertEx.True(launcher.Handles.Single().WasTreeKilled, "A force eject must tree-kill the process.");
        AssertEx.True(lease!.WasEjected, "The in-flight lease must report the process was operator-ejected.");
    }

    [Test]
    public async Task TryAcquireInferenceLease_NotRunning_RefusedAsAbsent()
    {
        await using var supervisor = SupervisorFactory.Create();

        var acquisition = supervisor.TryAcquireInferenceLease("ghost", ModelRole.Chat);

        AssertEx.True(acquisition.Lease is null, "No lease should be issued when no process backs the (model, role).");
        AssertEx.False(acquisition.ProcessEvicting, "An absent process is not an eject in progress — the caller may proceed leaseless.");
    }

    [Test]
    public async Task TryAcquireInferenceLease_WhileEjectDraining_RefusedAsEvicting_ThenAbsentAfterEject()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher, options: OptionsWithDrain(TimeSpan.FromSeconds(5)));
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        var held = supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).Lease;
        AssertEx.NotNull(held);

        // EjectAsync marks the process evicting synchronously (before its first await), so the refusal below is
        // deterministic while the drain waits on the held lease.
        var ejectTask = supervisor.EjectAsync("model-a", ModelRole.Chat, force: false, CancellationToken.None);

        var refused = supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat);
        AssertEx.True(refused.Lease is null, "No lease may be granted while an eject is draining.");
        AssertEx.True(refused.ProcessEvicting, "The refusal must be classified as eject-in-progress, not an absent process.");

        held!.Dispose();
        var outcome = await ejectTask;
        AssertEx.Equal(LlamaServerEjectOutcome.Ejected, outcome);

        // After the eject completes the process is genuinely absent: refused, but NOT as evicting.
        var afterEject = supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat);
        AssertEx.True(afterEject.Lease is null, "No lease exists for the torn-down process.");
        AssertEx.False(afterEject.ProcessEvicting, "A completed eject leaves no evicting refusal behind.");
    }

    [Test]
    public async Task Eject_CancelledMidDrain_ClearsEvicting_ProcessStaysUsable()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher, options: OptionsWithDrain(TimeSpan.FromSeconds(30)));
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        using var held = supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).Lease;
        AssertEx.NotNull(held);

        using var cts = new CancellationTokenSource();
        var ejectTask = supervisor.EjectAsync("model-a", ModelRole.Chat, force: false, cts.Token);
        AssertEx.True(supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).ProcessEvicting,
            "The eject must be draining (evicting) before the cancellation.");

        await cts.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => ejectTask);

        // The cancelled eject performed no teardown, so the evicting mark must be cleared: the process stays alive,
        // reusable, and grants leases again (the audited bug left it refusing every future lease forever).
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "A cancelled graceful eject must not kill the process.");
        var next = supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat);
        AssertEx.NotNull(next.Lease);
        AssertEx.False(next.ProcessEvicting, "The evicting mark must not survive a cancelled eject.");
        next.Lease!.Dispose();

        // With every lease released, a subsequent eject still works end-to-end (drains immediately and tears down).
        held!.Dispose();
        var outcome = await supervisor.EjectAsync("model-a", ModelRole.Chat, force: false, CancellationToken.None);
        AssertEx.Equal(LlamaServerEjectOutcome.Ejected, outcome);
    }
}
