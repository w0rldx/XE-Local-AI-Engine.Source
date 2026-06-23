namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies that the shared idle-TTL evicts an unused process and a new distinct model is admitted by evicting the
///     idle LRU; when the cap is full of <em>in-use</em> processes a new distinct model is rejected at start.
/// </summary>
public sealed class SupervisorEvictionTests
{
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
