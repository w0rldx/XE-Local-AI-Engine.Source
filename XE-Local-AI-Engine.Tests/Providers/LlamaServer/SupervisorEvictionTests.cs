namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies that the shared idle-TTL evicts an unused process and a new distinct model is admitted by evicting the
///     idle LRU; when the cap is full of <em>in-use</em> chat processes a new distinct model is rejected at start. An
///     in-window but unleased POOLED (embedding/reranker) process, by contrast, yields its slot — otherwise the default
///     cap (3 = the number of roles) plus background indexing hard-fails every chat model switch for a full TTL window.
/// </summary>
[Category(TestCategories.Unit)]
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

        // Push the process far past the TTL while its lease is held: LastUsedUtc is stamped at request start and release (not
        // per token), so a generation outrunning the idle window LOOKS idle — the reaper must skip it, not kill it mid-flight.
        time.Advance(TimeSpan.FromMinutes(10));

        // real-timer: the reaper's cadence timer resolves through AdvanceableTimeProvider.CreateTimer, which falls
        // through to the real provider, so only wall clock produces a reaper pass. Making this deterministic needs a
        // fake timer in the shared AdvanceableTimeProvider (Providers/LlamaServer/SupervisorTestDoubles.cs) or a
        // pass counter on LlamaServerIdleReaper; neither exists, and both live outside this file.
        await Task.Delay(SeveralReaperPasses);
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "The reaper must never kill a leased process, even past the TTL.");
        AssertEx.Equal(expected: 1, supervisor.CountRunningProcesses());

        // Released and then idle for one TTL, it is a normal victim: the next reaper pass evicts it, which also proves the
        // reaper was live the whole time (the skip above wasn't a stalled loop).
        lease!.Dispose();
        time.Advance(TimeSpan.FromSeconds(2));
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

        // Idle time runs from the release, so one full TTL later the same admission finds its idle LRU victim.
        lease!.Dispose();
        time.Advance(ttl);
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task BackgroundSpawn_CapFullOfAChatProcessThatWouldYield_EvictsNothing_AndIsRefused(bool transient)
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 1, ttl), timeProvider: time);
        await FillWithYieldingChatProcessAsync(supervisor, time, ttl, transient);

        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, ModelResidencyIntent.Background, CancellationToken.None));

        AssertEx.Contains(ex.Message, "maximum number of local models", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(expected: 1, launcher.LaunchCount, "the refused background load never launched");
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "work no user waits on never unloads a chat model at the cap");
        AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InteractiveSpawn_CapFullOfAChatProcessThatWouldYield_EvictsIt(bool transient)
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 1, ttl), timeProvider: time);
        await FillWithYieldingChatProcessAsync(supervisor, time, ttl, transient);

        await supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, ModelResidencyIntent.Interactive, CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(launcher.Handles.OrderBy(h => h.ProcessId).First().WasTreeKilled, "a user load takes the yielding chat process as before");
    }

    [Test]
    [Arguments(ModelResidencyIntent.Background, 1)]
    [Arguments(ModelResidencyIntent.Interactive, 0)]
    public async Task CapFullOfIdleChatAndInWindowPooled_OnlyABackgroundSpawnSparesTheChatProcess(ModelResidencyIntent intent, int victimIndex)
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 2, ttl), timeProvider: time);

        // chat-a idles past its TTL (rank 0); the embedding process stays in-window (rank 1).
        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(ttl + TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None);

        await supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, intent, CancellationToken.None);

        AssertEx.Equal(expected: 3, launcher.LaunchCount);
        var handles = launcher.Handles.OrderBy(h => h.ProcessId).ToList();
        AssertEx.True(handles[victimIndex].WasTreeKilled, $"a {intent} spawn evicts process {victimIndex}");
        AssertEx.False(handles[1 - victimIndex].WasTreeKilled, $"a {intent} spawn evicts exactly one process");
    }

    // Chat model-a fills the only slot as a victim a user load would take: idle past its TTL, or in-window but transient.
    private static async Task FillWithYieldingChatProcessAsync(LlamaServerProcessSupervisor supervisor, AdvanceableTimeProvider time, TimeSpan ttl, bool transient)
    {
        if (transient)
        {
            await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);
            return;
        }

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(ttl + TimeSpan.FromMinutes(1));
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

    [Test]
    public async Task ListRunningProcesses_ReportsLiveProcessesWithLastUse_AndDropsAnExitedOne()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 3, TimeSpan.FromHours(1)), timeProvider: time);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("model-b", ModelRole.Embedding, CancellationToken.None);

        var running = supervisor.ListRunningProcesses().OrderBy(static process => process.LastUsedUtc).ToArray();

        AssertEx.Equal(2, running.Length);
        AssertEx.Equal("model-a", running[0].ModelName);
        AssertEx.Equal(ModelRole.Embedding, running[1].Role);
        AssertEx.True(running[1].LastUsedUtc > running[0].LastUsedUtc, "The later ensure must carry the later last-use stamp.");

        launcher.Handles.OrderBy(static handle => handle.ProcessId).First().SimulateExit();

        AssertEx.Equal("model-b", supervisor.ListRunningProcesses().Single().ModelName);
    }

    [Test]
    public async Task MemoryEviction_TakesTheLeastRecentlyUsedIdleChat_EvenInsideItsIdleWindow()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var admission = new EvictingChatAdmission("chat-c");
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: CapOf(cap: 5, TimeSpan.FromMinutes(15)),
            timeProvider: time,
            pooledLaunchAdmission: admission);

        await ServeOneTurnAsync(supervisor, "chat-a");
        time.Advance(TimeSpan.FromMinutes(1));
        await ServeOneTurnAsync(supervisor, "chat-b");
        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));

        // Every resident is well inside the 15 min window, so neither the idle reaper nor cap eviction would take any of them.
        await supervisor.EnsureRunningAsync("chat-c", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(new IdleChatEvictionResult("'chat-a' (Chat)", BusyModel: null), admission.Results.Single());
        var handles = launcher.Handles.OrderBy(static handle => handle.ProcessId).ToList();
        AssertEx.True(handles[0].WasTreeKilled, "The least recently used idle chat process is unloaded for a chat load that does not fit.");
        AssertEx.False(handles[1].WasTreeKilled, "Only one process is unloaded per eviction call.");
        AssertEx.False(handles[2].WasTreeKilled, "The node's embedder is never unloaded for memory.");
        AssertEx.Null(supervisor.GetRegisteredProcess("chat-a", ModelRole.Chat));
        AssertEx.Equal(expected: 4, launcher.LaunchCount);
    }

    [Test]
    public async Task MemoryEviction_NeverTakesALeasedProtectedOrPooledProcess()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var admission = new EvictingChatAdmission("chat-c")
        {
            Protected = ["KEEP-WARM"]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: CapOf(cap: 6, TimeSpan.FromMinutes(15)),
            timeProvider: time,
            pooledLaunchAdmission: admission);

        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);
        using var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease("chat-a", ModelRole.Chat).Lease);
        await ServeOneTurnAsync(supervisor, "keep-warm");
        await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None);
        await supervisor.EnsureRunningAsync("rerank-model", ModelRole.Reranker, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));

        await supervisor.EnsureRunningAsync("chat-c", ModelRole.Chat, CancellationToken.None);

        // Only the leased chat is reported busy, so the hook can wait for it; the protected and pooled processes are not candidates at all.
        AssertEx.Equal(new IdleChatEvictionResult(EvictedModel: null, "'chat-a' (Chat)"), admission.Results.Single());
        AssertEx.Empty(launcher.Handles.Where(static handle => handle.WasTreeKilled));
    }

    [Test]
    public async Task MemoryEviction_NeverTakesAProfilingProcess()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var admission = new EvictingChatAdmission("model-b");
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: CapOf(cap: 3, TimeSpan.FromMinutes(15)),
            timeProvider: time,
            pooledLaunchAdmission: admission);

        var profilingHandleKilledDuringBody = true;
        await supervisor.RunExclusiveProfilingAsync("model-a",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            async (_, _) =>
            {
                time.Advance(TimeSpan.FromMinutes(1));
                await supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None);
                profilingHandleKilledDuringBody = launcher.Handles.OrderBy(static handle => handle.ProcessId).First().WasTreeKilled;
                return true;
            },
            CancellationToken.None);

        AssertEx.Equal(IdleChatEvictionResult.NothingEligible, admission.Results.Single(), "The process a benchmark is measuring is neither unloaded nor waited for.");
        AssertEx.False(profilingHandleKilledDuringBody);
    }

    [Test]
    public async Task MemoryEviction_SparesANeverLeasedChat_OnlyForTheGracePeriod()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var admission = new EvictingChatAdmission("chat-c", "chat-d");
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: CapOf(cap: 5, TimeSpan.FromMinutes(15)),
            timeProvider: time,
            pooledLaunchAdmission: admission);

        // chat-b is ready, but the conversation that cold-started it has not taken its lease yet.
        await supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));

        await supervisor.EnsureRunningAsync("chat-c", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(IdleChatEvictionResult.NothingEligible, admission.Results.Single(), "A load must neither take nor wait for a model another conversation is about to use.");
        var chatB = launcher.Handles.OrderBy(static handle => handle.ProcessId).First();
        AssertEx.False(chatB.WasTreeKilled);

        // Never leased past the grace period (a warm-only load, say from the model picker): an ordinary idle victim.
        time.Advance(TimeSpan.FromSeconds(30));
        await supervisor.EnsureRunningAsync("chat-d", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal("'chat-b' (Chat)", admission.Results[1].EvictedModel);
        AssertEx.True(chatB.WasTreeKilled, "The grace period bounds the protection; it must not last until the idle TTL.");
    }

    /// <summary>Loads a chat model and serves one request on it, the way a turn does: ensure, lease, release.</summary>
    private static async Task ServeOneTurnAsync(LlamaServerProcessSupervisor supervisor, string modelName)
    {
        await supervisor.EnsureRunningAsync(modelName, ModelRole.Chat, CancellationToken.None);
        AssertEx.NotNull(supervisor.TryAcquireInferenceLease(modelName, ModelRole.Chat).Lease).Dispose();
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

    /// <summary>A chat admission that, for one model, asks the supervisor to unload one idle chat process and records what it took.</summary>
    private sealed class EvictingChatAdmission : ILlamaServerPooledLaunchAdmission
    {
        private readonly HashSet<string> _evictFor;

        public EvictingChatAdmission(params string[] evictFor)
        {
            _evictFor = new HashSet<string>(evictFor, StringComparer.Ordinal);
        }

        public IReadOnlyCollection<string> Protected { get; init; } = [];

        public List<IdleChatEvictionResult> Results { get; } = [];

        public Task<IDisposable?> AdmitAsync(string modelName, ModelRole role, CancellationToken ct) =>
            Task.FromResult<IDisposable?>(null);

        public async Task<IDisposable?> AdmitChatAsync(string modelName,
            bool mayUnloadIdleModels,
            Func<IReadOnlyCollection<string>, CancellationToken, Task<IdleChatEvictionResult>> evictIdleModel,
            CancellationToken ct)
        {
            if (_evictFor.Contains(modelName))
            {
                Results.Add(await evictIdleModel(Protected, ct));
            }

            return null;
        }
    }
}
