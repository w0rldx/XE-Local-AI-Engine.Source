namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies that the shared idle-TTL evicts an unused process and a new distinct model is admitted by evicting the
///     idle LRU; when the cap is full of <em>in-use</em> chat processes a new distinct model is rejected at start. An
///     in-window but unleased POOLED (embedding/reranker) process, by contrast, yields its slot — otherwise the default
///     cap (3 = the number of roles) plus background indexing hard-fails every chat model switch for a full TTL window.
/// </summary>
public sealed class SupervisorEvictionTests
{
    /// <summary>Enough wall clock for several passes of the reaper's ~1 s cadence, which runs on the real clock.</summary>
    private static readonly TimeSpan SeveralReaperPasses = TimeSpan.FromSeconds(3);

    [Test]
    public async Task EnsureRunning_CapFullOfActiveProcesses_NewDistinctModel_Rejects()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: CapOf(cap: 2, TimeSpan.FromHours(1)),
            timeProvider: time);

        // Fill the cap with two fresh (non-idle) chat processes.
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None);

        // A third distinct model has no idle victim to evict → reject at start.
        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("model-c", ModelRole.Chat, CancellationToken.None));

        AssertEx.Contains(ex.Message, "maximum number of local models", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(expected: 2, launcher.LaunchCount); // model-c never launched.
    }

    [Test]
    public async Task EnsureRunning_ConcurrentDistinctModels_NeverExceedCap()
    {
        // Regression for the cap-overrun race: distinct (model, role) take distinct ensure-gates, so without a
        // reservation under the admission gate two concurrent spawns could both pass the cap check before either
        // registers. The readiness probe parks every spawn in-flight so all admissions race simultaneously.
        const int cap = 3;
        const int distinctModels = 8;
        var launcher = new FakeProcessLauncher();
        var probe = new GatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher,
            probe,
            options: CapOf(cap, TimeSpan.FromHours(1)));

        var calls = Enumerable.Range(start: 0, distinctModels)
                              .Select(i => Task.Run(async () =>
                              {
                                  try
                                  {
                                      await supervisor.EnsureRunningAsync($"model-{i}", ModelRole.Chat, CancellationToken.None);
                                      return true; // admitted
                                  }
                                  catch (LlamaRuntimeException)
                                  {
                                      return false; // cap-rejected at admit
                                  }
                              }))
                              .ToArray();

        // Settle: exactly `cap` spawns park in readiness and the remaining (distinctModels - cap) are cap-rejected.
        await AssertEx.EventuallyAsync(() => probe.Waiting == cap && RejectedCount(calls) == distinctModels - cap,
            TimeSpan.FromSeconds(5),
            "Admissions did not settle at the cap.");

        AssertEx.Equal(cap, probe.Waiting); // never more than `cap` concurrently admitted.
        AssertEx.True(launcher.LaunchCount <= cap, $"Launched {launcher.LaunchCount} processes, cap is {cap}.");

        probe.Release();
        var results = await Task.WhenAll(calls);

        AssertEx.Equal(cap, results.Count(admitted => admitted));
        AssertEx.Equal(distinctModels - cap, results.Count(admitted => !admitted));
        AssertEx.Equal(cap, launcher.LaunchCount); // the cap was never exceeded by the race.
    }

    private static int RejectedCount(IEnumerable<Task<bool>> calls)
    {
        return calls.Count(t => t.IsCompletedSuccessfully && !t.Result);
    }

    [Test]
    public async Task EnsureRunning_CapFull_ButLruIsIdlePastTtl_EvictsLru_AndAdmitsNewModel()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: CapOf(cap: 2, ttl),
            timeProvider: time);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None); // becomes LRU
        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None);

        // Push both past the idle TTL so the LRU (model-a) is an eligible eviction victim.
        time.Advance(ttl + TimeSpan.FromMinutes(1));

        await supervisor.EnsureRunningAsync("model-c", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 3, launcher.LaunchCount); // model-c spawned after evicting an idle victim.

        // The evicted least-recently-used process (model-a's, the first launched) was tree-killed.
        var firstHandle = launcher.Handles.OrderBy(h => h.ProcessId).First();
        AssertEx.True(firstHandle.WasTreeKilled, "Idle LRU victim should have been tree-killed.");
    }

    [Test]
    public async Task EnsureRunning_ReusesRunningProcess_NoSecondSpawn()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        var first = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        var second = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.Equal(first.BaseAddress.AbsoluteUri, second.BaseAddress.AbsoluteUri);
    }

    [Test]
    public async Task EnsureRunning_ReusedBeforeIdleTtl_RefreshesLastUsedAndPreventsEviction()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 1, ttl), timeProvider: time);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(14));

        // This is the exact reuse touch the keep-warm service performs. It must refresh LastUsed without another spawn.
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(2));

        // Sixteen minutes elapsed since launch, but only two since the keep-warm touch: model-a is not an idle victim.
        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None));
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.False(launcher.Handles.Single().WasTreeKilled);

        // Once the refreshed TTL really elapses, normal LRU admission can evict it.
        time.Advance(TimeSpan.FromMinutes(14));
        await supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None);
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(launcher.Handles.OrderBy(handle => handle.ProcessId).First().WasTreeKilled);
    }

    [Test]
    public async Task Reaper_LeasedProcessPastIdleTtl_IsNotReaped_UntilLeaseReleases()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        // A short TTL makes the background reaper re-check about every second of REAL time (its cadence is a quarter
        // of the TTL, floored at one second), while the injected clock drives the idle comparison itself.
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 3, TimeSpan.FromSeconds(2)), timeProvider: time);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        var lease = supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).Lease;
        AssertEx.NotNull(lease);

        // Push the process far past the TTL while its lease is held: LastUsedUtc is stamped per ensure/reuse (not per
        // token), so a generation outrunning the idle window LOOKS idle — the reaper must skip it, not kill it mid-flight.
        time.Advance(TimeSpan.FromMinutes(10));

        // real-timer: the reaper's cadence timer resolves through AdvanceableTimeProvider.CreateTimer, which falls
        // through to the real provider, so only wall clock produces a reaper pass. Making this deterministic needs a
        // fake timer in the shared AdvanceableTimeProvider (Providers/LlamaServer/SupervisorTestDoubles.cs) or a
        // pass counter on LlamaServerIdleReaper; neither exists, and both live outside this file.
        await Task.Delay(SeveralReaperPasses);
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "The reaper must never kill a leased process, even past the TTL.");
        AssertEx.Equal(expected: 1, supervisor.CountRunningProcesses());

        // Releasing the lease makes it a normal idle victim — the next reaper pass evicts it, which also proves the
        // reaper was live the whole time (the skip above wasn't a stalled loop).
        lease!.Dispose();
        await AssertEx.EventuallyAsync(() => launcher.Handles.Single().WasTreeKilled, TimeSpan.FromSeconds(5),
            "Once the lease released, the idle process should be reaped.");
    }

    [Test]
    public async Task EnsureRunning_CapFull_LruPastTtlButLeased_IsNotEvicted_NewModelRejects()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 1, ttl), timeProvider: time);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        var lease = supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).Lease;
        AssertEx.NotNull(lease);

        // Past the TTL the process looks idle to the LRU scan, but the held lease means a generation is mid-flight —
        // capacity admission must reject the newcomer rather than tree-kill the busy process to make room.
        time.Advance(ttl + TimeSpan.FromMinutes(1));

        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None));
        AssertEx.Contains(ex.Message, "maximum number of local models", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "A leased process must never be a capacity-eviction victim.");

        // Once the lease releases, the same admission finds its idle LRU victim and the new model loads normally.
        lease!.Dispose();
        await supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None);
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(launcher.Handles.OrderBy(h => h.ProcessId).First().WasTreeKilled, "After release the idle LRU is evicted as usual.");
    }

    [Test]
    public async Task EnsureRunning_CapFullOfInWindowRoles_ChatSpawnEvictsLruPooled_NotChat()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 3, ttl), timeProvider: time);

        // The default node shape: chat + embedding + reranker, all touched within the TTL window (background indexing
        // keeps refreshing the pooled pair). Before pooled processes could yield, this made every chat model switch
        // hard-fail with the cap error for up to a full TTL window.
        await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None); // pooled LRU
        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("rerank-model", ModelRole.Reranker, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));

        // A chat model switch succeeds by evicting the least-recently-used pooled process.
        await supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 4, launcher.LaunchCount);
        var handles = launcher.Handles.OrderBy(h => h.ProcessId).ToList();
        AssertEx.True(handles[0].WasTreeKilled, "The LRU pooled (embedding) process yields its slot.");
        AssertEx.False(handles[1].WasTreeKilled, "The newer pooled (reranker) process survives — LRU within the pooled rank.");
        AssertEx.False(handles[2].WasTreeKilled, "An in-window chat process is never a capacity-eviction victim.");
    }

    [Test]
    public async Task EnsureRunning_CapFull_InWindowPooledButLeased_IsNotEvicted_UntilLeaseReleases()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: CapOf(cap: 1, TimeSpan.FromMinutes(15)),
            timeProvider: time);

        await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None);
        var lease = supervisor.TryAcquireInferenceLease("embed-model", ModelRole.Embedding).Lease;
        AssertEx.NotNull(lease);

        // A leased pooled process is mid-forward-pass — it must never be torn down to admit a newcomer.
        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None));
        AssertEx.Contains(ex.Message, "maximum number of local models", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "A leased pooled process must never be a capacity-eviction victim.");

        // Released but still in-window: the pooled process now yields (this is the new behavior under the role rank).
        lease!.Dispose();
        await supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None);
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(launcher.Handles.OrderBy(h => h.ProcessId).First().WasTreeKilled, "The unleased in-window pooled process yields once its lease releases.");
    }

    [Test]
    public async Task EnsureRunning_CapFullButOneProcessExited_PrunesExited_AndAdmitsNewModel()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: CapOf(cap: 2, TimeSpan.FromHours(1)),
            timeProvider: time);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None);

        // model-a's child dies on its own. Both survivors are well inside the TTL, so neither is an idle victim — only
        // the exited-process prune can reclaim the slot the dead entry still occupies.
        var deadHandle = launcher.Handles.OrderBy(h => h.ProcessId).First();
        deadHandle.SimulateExit();

        await supervisor.EnsureRunningAsync("model-c", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 3, launcher.LaunchCount);
        AssertEx.True(deadHandle.WasDisposed, "The pruned exited process should have been torn down, not leaked.");
    }

    private static LlamaServerSupervisorOptions CapOf(int cap, TimeSpan ttl)
    {
        return new LlamaServerSupervisorOptions
        {
            MaxLoadedProcesses = cap,
            IdleTimeToLive = ttl,
            MaxRestartAttempts = 3
        };
    }
}
