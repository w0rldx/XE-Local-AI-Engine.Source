namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies that the shared idle-TTL evicts an unused process and a new distinct model is admitted by evicting the
///     idle LRU; when the cap is full of <em>in-use</em> chat processes a new distinct model is rejected at start. Chat
///     models and the companions (embedding/reranker) hold separate slots: each evicts only within its own class.
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
    public async Task TwoResidentChatModels_ConsecutiveSearches_NeverEvictEitherCompanion()
    {
        // A 27B and a 9B chat model resident on a cap of 3 would leave one slot for the embedder and the reranker,
        // so every knowledge search's embed and warm would evict the other companion; neither is ever evicted.
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 3, TimeSpan.FromMinutes(15)), timeProvider: time);
        await supervisor.EnsureRunningAsync("chat-27b", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("chat-9b", ModelRole.Chat, CancellationToken.None);

        // Each search embeds the query, then the background warm ensures the reranker.
        for (var search = 0; search < 2; search++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None);
            await supervisor.EnsureRunningAsync("rerank-model", ModelRole.Reranker, CancellationToken.None);
        }

        AssertEx.Equal(expected: 4, launcher.LaunchCount, "both companions launched once and were reused by the second search");
        AssertEx.False(launcher.Handles.Any(handle => handle.WasTreeKilled), "no chat model or companion was evicted");
    }

    [Test]
    public async Task ADecisionModel_IsACompanion_AndTheDefaultCompanionCapHoldsAllThree()
    {
        // The default cap gives the embedder, the reranker and one decision model a slot each, and none of them takes a chat slot.
        var launcher = new FakeProcessLauncher();
        var defaults = new LlamaServerSupervisorOptions();
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: CapOf(cap: 1, TimeSpan.FromMinutes(15), companionCap: defaults.MaxLoadedCompanionProcesses));
        await supervisor.EnsureRunningAsync("chat-9b", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None);
        await supervisor.EnsureRunningAsync("rerank-model", ModelRole.Reranker, CancellationToken.None);
        await supervisor.EnsureRunningAsync("ggml-org/Julia-1-GGUF:Q8_0", ModelRole.Decision, CancellationToken.None);

        AssertEx.Equal(expected: 4, launcher.LaunchCount);
        AssertEx.False(launcher.Handles.Any(handle => handle.WasTreeKilled), "a decision model neither frees a chat slot nor evicts a companion under the default cap.");
    }

    [Test]
    public async Task ChatSpawnAtTheChatCap_NeverEvictsACompanion()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 1, TimeSpan.FromMinutes(15)), timeProvider: time);
        await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None);
        await supervisor.EnsureRunningAsync("rerank-model", ModelRole.Reranker, CancellationToken.None);
        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));

        // The only chat slot holds an in-window interactive chat process, which is never a victim; a companion frees no chat slot.
        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None));

        AssertEx.Contains(ex.Message, "maximum number of local models", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(expected: 3, launcher.LaunchCount);
        AssertEx.False(launcher.Handles.Any(handle => handle.WasTreeKilled), "a chat load never evicts a companion");
    }

    [Test]
    public async Task CompanionSpawnAtTheCompanionCap_EvictsTheLruCompanion_NeverAChatModel()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: CapOf(cap: 1, TimeSpan.FromMinutes(15), companionCap: 2),
            timeProvider: time);
        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None); // companion LRU
        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("rerank-model", ModelRole.Reranker, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));

        await supervisor.EnsureRunningAsync("embed-model-2", ModelRole.Embedding, CancellationToken.None);

        var handles = launcher.Handles.OrderBy(h => h.ProcessId).ToList();
        AssertEx.Equal(expected: 4, handles.Count);
        AssertEx.False(handles[0].WasTreeKilled, "the chat model holds its own slot");
        AssertEx.True(handles[1].WasTreeKilled, "the least-recently-used companion yields");
        AssertEx.False(handles[2].WasTreeKilled, "the newer companion survives");
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
    [Arguments(ModelResidencyIntent.Background, false)]
    [Arguments(ModelResidencyIntent.Interactive, true)]
    public async Task ChatCapFullOfAnIdleChat_OnlyAnInteractiveSpawnTakesIt_AndNeverTheEmbedder(ModelResidencyIntent intent, bool admitted)
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 1, ttl), timeProvider: time);

        // chat-a idles past its TTL; the embedding process stays in-window in its own companion slot.
        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(ttl + TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None);

        var spawn = supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, intent, CancellationToken.None);
        if (admitted)
        {
            await spawn;
        }
        else
        {
            _ = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => spawn);
        }

        var handles = launcher.Handles.OrderBy(h => h.ProcessId).ToList();
        AssertEx.Equal(admitted, handles[0].WasTreeKilled, $"a {intent} spawn takes the idle chat model only when it is interactive");
        AssertEx.False(handles[1].WasTreeKilled, "the embedder is never a chat spawn's victim");
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
    public async Task EnsureRunning_CompanionCapFull_InWindowCompanionButLeased_IsNotEvicted_UntilLeaseReleases()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: CapOf(cap: 1, TimeSpan.FromMinutes(15), companionCap: 1),
            timeProvider: time);

        await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None);
        var lease = supervisor.TryAcquireInferenceLease("embed-model", ModelRole.Embedding).Lease;
        AssertEx.NotNull(lease);

        // A leased companion is mid-forward-pass — it must never be torn down to admit another companion.
        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("rerank-model", ModelRole.Reranker, CancellationToken.None));
        AssertEx.Contains(ex.Message, "maximum number of local models", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "A leased companion must never be a capacity-eviction victim.");

        // Released but still in-window: the companion now yields to the other companion.
        lease!.Dispose();
        await supervisor.EnsureRunningAsync("rerank-model", ModelRole.Reranker, CancellationToken.None);
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(launcher.Handles.OrderBy(h => h.ProcessId).First().WasTreeKilled, "The unleased in-window companion yields once its lease releases.");
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

    [Test]
    public async Task Residency_TicksOnRegistrationLeaseAcquireReleaseAndEject()
    {
        var notifier = new RecordingResidencyChangeNotifier();
        await using var supervisor = SupervisorFactory.Create(residencyNotifier: notifier);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        AssertEx.True(notifier.Count >= 1, "Registration must tick: the process is now a running-list row.");

        // IsBusy and LastUsedUtc both move on a lease, so the acquire and the release each tick exactly once.
        var beforeLease = notifier.Count;
        var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).Lease);
        AssertEx.Equal(beforeLease + 1, notifier.Count);
        lease.Dispose();
        AssertEx.Equal(beforeLease + 2, notifier.Count);

        var beforeEject = notifier.Count;
        AssertEx.Equal(LlamaServerEjectOutcome.Ejected, await supervisor.EjectAsync("model-a", ModelRole.Chat, force: false, CancellationToken.None));
        AssertEx.True(notifier.Count > beforeEject, "An eject removes the row and must tick.");
        AssertEx.Equal(expected: 0, (await supervisor.CheckHealthAsync(CancellationToken.None)).Count);
    }

    [Test]
    public async Task Residency_TicksOnIdleReap()
    {
        var notifier = new RecordingResidencyChangeNotifier();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        await using var supervisor = SupervisorFactory.Create(options: CapOf(cap: 2, ttl), timeProvider: time, residencyNotifier: notifier);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        var beforeReap = notifier.Count;

        time.Advance(ttl + TimeSpan.FromMinutes(1));
        await supervisor.ReapIdleOnceAsync();

        AssertEx.Equal(expected: 0, (await supervisor.CheckHealthAsync(CancellationToken.None)).Count);
        AssertEx.True(notifier.Count > beforeReap, "An idle reap removes the row and must tick.");
    }

    [Test]
    public async Task IdleReap_WhenOneTeardownThrows_StillReapsTheOthers()
    {
        // A throwing TreeKill (e.g. a Win32Exception from a plain handle) escaping ReapIdleOnceAsync would fault the
        // reaper loop for the rest of the session and skip every other idle process in that pass.
        var nextPid = 5000;
        var launcher = new FakeProcessLauncher(spec =>
        {
            Action? onTreeKill = string.Equals(spec.ModelName, "model-a", StringComparison.Ordinal)
                ? static () => throw new InvalidOperationException("teardown failed")
                : null;
#pragma warning disable CA2000 // Ownership transfers to the supervisor through the launcher fake.
            return new FakeProcessTreeHandle(Interlocked.Increment(ref nextPid), onTreeKill: onTreeKill);
#pragma warning restore CA2000
        });
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 2, ttl), timeProvider: time);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None);

        time.Advance(ttl + TimeSpan.FromMinutes(1));
        await supervisor.ReapIdleOnceAsync();

        AssertEx.Equal(expected: 0, (await supervisor.CheckHealthAsync(CancellationToken.None)).Count);
        AssertEx.True(launcher.Handles.All(static handle => handle.WasTreeKilled || handle.WasDisposed),
            "every idle process must be torn down even when one teardown throws.");
    }

    [Test]
    public async Task Residency_TicksWhenAProcessDiesOnItsOwn()
    {
        var launcher = new FakeProcessLauncher();
        var notifier = new RecordingResidencyChangeNotifier();
        await using var supervisor = SupervisorFactory.Create(launcher, residencyNotifier: notifier);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        var beforeExit = notifier.Count;

        launcher.Handles.Single().SimulateExit(exitCode: 137);

        // The exit watch resumes on the thread pool, so the tick lands shortly after the exit, not inside SimulateExit.
        await AssertEx.EventuallyAsync(() => notifier.Count > beforeExit, TestBudgets.Contended,
            "A crash must reach the header without waiting for the idle reaper's next pass.");
    }

    [Test]
    public async Task UnloadTelemetry_NamesTheIdleReap_ThenTheCapEviction()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        var ttl = TimeSpan.FromMinutes(15);
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 2, ttl), timeProvider: time, loadTelemetry: telemetry);

        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(ttl + TimeSpan.FromMinutes(1));
        await supervisor.ReapIdleOnceAsync();

        await supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("chat-c", ModelRole.Chat, CancellationToken.None);
        time.Advance(ttl + TimeSpan.FromMinutes(1));
        // Cap 2 is full and chat-b is the idle LRU, so admitting chat-d evicts it for the cap.
        await supervisor.EnsureRunningAsync("chat-d", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal("(chat-a, Chat, IdleTimeout), (chat-b, Chat, LoadedModelCap)", string.Join(", ", telemetry.Unloads));
    }

    [Test]
    public async Task UnloadTelemetry_NamesTheMemoryEviction()
    {
        var time = new AdvanceableTimeProvider();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        await using var supervisor = SupervisorFactory.Create(options: CapOf(cap: 5, TimeSpan.FromMinutes(15)), timeProvider: time, loadTelemetry: telemetry,
            pooledLaunchAdmission: new EvictingChatAdmission("chat-b"));

        await ServeOneTurnAsync(supervisor, "chat-a");
        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal("(chat-a, Chat, MemoryForAnotherModel)", string.Join(", ", telemetry.Unloads));
    }

    [Test]
    public async Task UnloadTelemetry_NamesShutdownForEveryLiveProcess_AndNothingForAnExitedOne()
    {
        var launcher = new FakeProcessLauncher();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        var supervisor = SupervisorFactory.Create(launcher, loadTelemetry: telemetry);
        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("embed-a", ModelRole.Embedding, CancellationToken.None);
        // The embedder, launched second, dies on its own: the node did not unload it.
        launcher.Handles.OrderBy(static handle => handle.ProcessId).Last().SimulateExit(exitCode: 1);

        await supervisor.DisposeAsync();

        AssertEx.Equal("(chat-a, Chat, Shutdown)", string.Join(", ", telemetry.Unloads));
    }

    [Test]
    public async Task UnloadTelemetry_WhenTheSinkThrows_TheIdleReapStillTearsTheProcessDown()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 2, ttl), timeProvider: time,
            loadTelemetry: new ThrowingLlamaServerLoadTelemetry());
        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);

        time.Advance(ttl + TimeSpan.FromMinutes(1));
        await supervisor.ReapIdleOnceAsync();

        AssertEx.True(launcher.Handles.Single().WasTreeKilled, "Unload telemetry is report-only and never blocks an eviction.");
    }

    [Test]
    public async Task LoweredChatCap_TheNextReapUnloadsLruIdleChats_DownToTheCap()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        var options = CapOf(cap: 4, TimeSpan.FromMinutes(15));
        await using var supervisor = SupervisorFactory.Create(launcher, options: options, timeProvider: time, loadTelemetry: telemetry);
        foreach (var model in new[] { "chat-a", "chat-b", "chat-c", "chat-d" })
        {
            await ServeOneTurnAsync(supervisor, model);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        // A node-settings save lowers the cap. Every resident is an in-window interactive chat, which no admission would take.
        options.MaxLoadedProcesses = 2;
        await supervisor.ReapIdleOnceAsync();

        var handles = ByLaunchOrder(launcher);
        AssertEx.True(handles[0].WasTreeKilled && handles[1].WasTreeKilled, "the two least recently used chats are unloaded");
        AssertEx.False(handles[2].WasTreeKilled || handles[3].WasTreeKilled, "the two most recently used stay loaded at the new cap");
        AssertEx.Equal(expected: 2, supervisor.CountRunningProcesses());
        AssertEx.Equal("(chat-a, Chat, LoadedModelCap), (chat-b, Chat, LoadedModelCap)", string.Join(", ", telemetry.Unloads));
    }

    [Test]
    public async Task LoweredChatCap_SparesALeasedChat_AndUnloadsTheNextLeastRecentlyUsed()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var options = CapOf(cap: 3, TimeSpan.FromMinutes(15));
        await using var supervisor = SupervisorFactory.Create(launcher, options: options, timeProvider: time);
        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);
        using var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease("chat-a", ModelRole.Chat).Lease);
        time.Advance(TimeSpan.FromMinutes(1));
        await ServeOneTurnAsync(supervisor, "chat-b");
        time.Advance(TimeSpan.FromMinutes(1));
        await ServeOneTurnAsync(supervisor, "chat-c");

        options.MaxLoadedProcesses = 2;
        await supervisor.ReapIdleOnceAsync();

        var handles = ByLaunchOrder(launcher);
        AssertEx.False(handles[0].WasTreeKilled, "a chat mid-request is never unloaded, though it is the least recently used");
        AssertEx.True(handles[1].WasTreeKilled, "the least recently used unleased chat is unloaded instead");
        AssertEx.False(handles[2].WasTreeKilled);
    }

    [Test]
    public async Task LoweredChatCap_ALeasedExcessProcessIsUnloadedOnThePassAfterItsLeaseEnds()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var options = CapOf(cap: 2, TimeSpan.FromMinutes(15));
        await using var supervisor = SupervisorFactory.Create(launcher, options: options, timeProvider: time);
        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);
        var leaseA = AssertEx.NotNull(supervisor.TryAcquireInferenceLease("chat-a", ModelRole.Chat).Lease);
        await supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None);
        using var leaseB = AssertEx.NotNull(supervisor.TryAcquireInferenceLease("chat-b", ModelRole.Chat).Lease);

        options.MaxLoadedProcesses = 1;
        await supervisor.ReapIdleOnceAsync();
        AssertEx.False(launcher.Handles.Any(static handle => handle.WasTreeKilled), "both chats are mid-request");

        leaseA.Dispose();
        await supervisor.ReapIdleOnceAsync();

        var handles = ByLaunchOrder(launcher);
        AssertEx.True(handles[0].WasTreeKilled, "the excess chat is unloaded on the first pass after its lease ends");
        AssertEx.False(handles[1].WasTreeKilled);
    }

    [Test]
    public async Task LoweredChatCap_SparesAChatAwaitingItsFirstLease_AndUnloadsAnother()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var options = CapOf(cap: 2, TimeSpan.FromMinutes(15));
        await using var supervisor = SupervisorFactory.Create(launcher, options: options, timeProvider: time);

        // chat-a is ready, but the conversation that cold-started it has not taken its lease yet.
        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        await ServeOneTurnAsync(supervisor, "chat-b");

        options.MaxLoadedProcesses = 1;
        await supervisor.ReapIdleOnceAsync();

        var handles = ByLaunchOrder(launcher);
        AssertEx.False(handles[0].WasTreeKilled, "a chat within its first-lease grace is spared, though it is the least recently used");
        AssertEx.True(handles[1].WasTreeKilled, "the other chat is unloaded instead");
    }

    [Test]
    public async Task LoweredChatCap_SparesAProfilingProcess_AndUnloadsAnother()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var options = CapOf(cap: 2, TimeSpan.FromMinutes(15));
        await using var supervisor = SupervisorFactory.Create(launcher, options: options, timeProvider: time);

        var profilingKilled = true;
        var otherKilled = false;
        await supervisor.RunExclusiveProfilingAsync("model-a",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            async (_, _) =>
            {
                time.Advance(TimeSpan.FromMinutes(1));
                await ServeOneTurnAsync(supervisor, "model-b");
                options.MaxLoadedProcesses = 1;
                await supervisor.ReapIdleOnceAsync();
                var handles = ByLaunchOrder(launcher);
                profilingKilled = handles[0].WasTreeKilled;
                otherKilled = handles[1].WasTreeKilled;
                return true;
            },
            CancellationToken.None);

        AssertEx.False(profilingKilled, "the process a benchmark is measuring is never unloaded, though it is the least recently used");
        AssertEx.True(otherKilled, "the other chat is unloaded instead");
    }

    [Test]
    public async Task LoweredChatCap_SparesTheKeepWarmModel_AndUnloadsAnother()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var options = CapOf(cap: 2, TimeSpan.FromMinutes(15));
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: options,
            timeProvider: time,
            pooledLaunchAdmission: new EvictingChatAdmission
            {
                Protected = ["KEEP-WARM"]
            });
        await ServeOneTurnAsync(supervisor, "keep-warm");
        time.Advance(TimeSpan.FromMinutes(1));
        await ServeOneTurnAsync(supervisor, "chat-b");

        options.MaxLoadedProcesses = 1;
        await supervisor.ReapIdleOnceAsync();

        var handles = ByLaunchOrder(launcher);
        AssertEx.False(handles[0].WasTreeKilled, "the keep-warm model is never unloaded for the cap, though it is the least recently used");
        AssertEx.True(handles[1].WasTreeKilled, "the other chat is unloaded instead");
    }

    [Test]
    public async Task LoweredChatCap_NeverTouchesACompanion_AndUnloadsAChat()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var options = CapOf(cap: 2, TimeSpan.FromMinutes(15));
        await using var supervisor = SupervisorFactory.Create(launcher, options: options, timeProvider: time);
        await supervisor.EnsureRunningAsync("embed-model", ModelRole.Embedding, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await ServeOneTurnAsync(supervisor, "chat-a");
        time.Advance(TimeSpan.FromMinutes(1));
        await ServeOneTurnAsync(supervisor, "chat-b");

        options.MaxLoadedProcesses = 1;
        await supervisor.ReapIdleOnceAsync();

        var handles = ByLaunchOrder(launcher);
        AssertEx.False(handles[0].WasTreeKilled, "a companion holds its own slot and is never a chat-cap victim, though it is the least recently used");
        AssertEx.True(handles[1].WasTreeKilled, "the least recently used chat is unloaded");
        AssertEx.False(handles[2].WasTreeKilled);
    }

    [Test]
    public async Task LoweredChatCap_UnloadsANewerTransientChat_BeforeAnOlderInteractiveOne()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var options = CapOf(cap: 2, TimeSpan.FromMinutes(15));
        await using var supervisor = SupervisorFactory.Create(launcher, options: options, timeProvider: time);
        await ServeOneTurnAsync(supervisor, "chat-a");
        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.EnsureRunningAsync("draft-model", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);

        // Past the first-lease grace, still inside the transient lifetime: both chats are in-window.
        time.Advance(TimeSpan.FromSeconds(40));
        options.MaxLoadedProcesses = 1;
        await supervisor.ReapIdleOnceAsync();

        var handles = ByLaunchOrder(launcher);
        AssertEx.False(handles[0].WasTreeKilled, "an in-window interactive chat ranks after a transient one, even as the least recently used");
        AssertEx.True(handles[1].WasTreeKilled, "the transient chat is unloaded first");
    }

    [Test]
    public async Task LoweredChatCap_AnAdmissionEvictsEveryEligibleVictim()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        var options = CapOf(cap: 3, ttl);
        await using var supervisor = SupervisorFactory.Create(launcher, options: options, timeProvider: time);
        await ServeOneTurnAsync(supervisor, "chat-a");
        await ServeOneTurnAsync(supervisor, "chat-b");
        await ServeOneTurnAsync(supervisor, "chat-c");
        time.Advance(ttl + TimeSpan.FromMinutes(1));

        // Two over the new cap: one eviction alone would still leave no room.
        options.MaxLoadedProcesses = 1;
        await supervisor.EnsureRunningAsync("chat-d", ModelRole.Chat, CancellationToken.None);

        var handles = ByLaunchOrder(launcher);
        AssertEx.True(handles.Take(3).All(static handle => handle.WasTreeKilled), "every idle chat is evicted to fit the new cap");
        AssertEx.Equal(expected: 4, launcher.LaunchCount);
        AssertEx.Equal(expected: 1, supervisor.CountRunningProcesses());
    }

    [Test]
    public async Task LoweredChatCap_PartialEvictionThenRefusal_TearsDownTheDetached()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        var options = CapOf(cap: 3, ttl);
        await using var supervisor = SupervisorFactory.Create(launcher, options: options, timeProvider: time);
        await ServeOneTurnAsync(supervisor, "chat-a");
        await supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None);
        using var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease("chat-b", ModelRole.Chat).Lease);
        time.Advance(ttl + TimeSpan.FromMinutes(1));
        await ServeOneTurnAsync(supervisor, "chat-c");

        // chat-a is idle past its TTL; chat-b is mid-request and chat-c an in-window interactive chat, so neither is an admission victim.
        options.MaxLoadedProcesses = 1;
        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("chat-d", ModelRole.Chat, CancellationToken.None));

        AssertEx.Contains(ex.Message, "maximum number of local models", StringComparison.OrdinalIgnoreCase);
        var handles = ByLaunchOrder(launcher);
        AssertEx.True(handles[0].WasTreeKilled && handles[0].WasDisposed, "the victim detached before the refusal is still torn down");
        AssertEx.False(handles[1].WasTreeKilled || handles[2].WasTreeKilled);
        AssertEx.Equal(expected: 3, launcher.LaunchCount, "the refused model never launched");
        AssertEx.Equal(expected: 2, supervisor.CountRunningProcesses());
    }

    [Test]
    public async Task LoweredChatCap_AnAdmissionCancelledAfterTheFirstDetach_TearsItDown_AndStops()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(15);
        var options = CapOf(cap: 3, ttl);
        using var cts = new CancellationTokenSource();
        var notifier = new CallbackResidencyChangeNotifier();
        await using var supervisor = SupervisorFactory.Create(launcher, options: options, timeProvider: time, residencyNotifier: notifier);
        await ServeOneTurnAsync(supervisor, "chat-a");
        time.Advance(TimeSpan.FromSeconds(1));
        await ServeOneTurnAsync(supervisor, "chat-b");
        time.Advance(TimeSpan.FromSeconds(1));
        await ServeOneTurnAsync(supervisor, "chat-c");
        time.Advance(ttl + TimeSpan.FromMinutes(1));
        options.MaxLoadedProcesses = 1;

        // The first detach under the admission gate ticks residency; the caller is cancelled right there. Profiling hands the caller's
        // token to the admission itself, while an ensure admits on the detached spawn's own token.
        notifier.OnChange = cts.Cancel;
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => supervisor.RunExclusiveProfilingAsync("model-p",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            static (_, _) => Task.FromResult(true),
            cts.Token));
        notifier.OnChange = null;

        var handles = ByLaunchOrder(launcher);
        AssertEx.True(handles[0].WasTreeKilled && handles[0].WasDisposed, "the victim detached before the cancellation is torn down");
        AssertEx.False(handles[1].WasTreeKilled || handles[2].WasTreeKilled, "a cancelled admission evicts nothing more");
        AssertEx.Equal(expected: 3, launcher.LaunchCount, "the cancelled admission never launched");
    }

    [Test]
    public async Task LoweredChatCap_AnAdmissionEnteringAfterTheSave_SeesTheNewCap()
    {
        var options = CapOf(cap: 2, TimeSpan.FromMinutes(15));
        var notifier = new CallbackResidencyChangeNotifier();
        var processes = NewProcessTable();
        using var reaper = NewReaper(processes, options, TimeProvider.System, notifier);
        _ = await reaper.AdmitAndAllocatePortAsync(ModelRole.Chat, ModelResidencyIntent.Interactive, CancellationToken.None);

        // An unrelated teardown holds the admission gate: its detach blocks in the residency tick, under the gate, until the test releases it.
        using var gateHeld = new ManualResetEventSlim();
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notifier.OnChange = () =>
        {
            gateEntered.TrySetResult();
            gateHeld.Wait();
        };
        var other = RegisterProcess(processes, "other", DateTimeOffset.UtcNow);
        var teardown = Task.Run(() => reaper.RemoveProcessAsync(other.Key, other.Value));
        try
        {
            await gateEntered.Task.WaitAsync(TestBudgets.Contended);

            var admission = reaper.AdmitAndAllocatePortAsync(ModelRole.Chat, ModelResidencyIntent.Interactive, CancellationToken.None);
            AssertEx.False(admission.IsCompleted, "the admission waits at the gate the teardown holds");

            // The save returns while the admission waits; the admission decides on the cap it reads inside the gate.
            options.MaxLoadedProcesses = 1;
            notifier.OnChange = null;
            gateHeld.Set();
            await teardown.WaitAsync(TestBudgets.Contended);

            var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => admission.WaitAsync(TestBudgets.Contended));
            AssertEx.Contains(ex.Message, "maximum number of local models", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // Released and drained before the reaper and the event are disposed, whatever failed above.
            notifier.OnChange = null;
            gateHeld.Set();
            await teardown;
        }
    }

    [Test]
    public async Task Dispose_DuringAKeepWarmReadInTheBackgroundPass_CancelsTheRead_AndCompletes()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var admission = new BlockingKeepWarmAdmission();

        // A 2 s TTL paces the background pass at its 1 s floor on the real clock; the test clock stays put, so nothing is reaped for idleness.
        var options = CapOf(cap: 2, TimeSpan.FromSeconds(2));
        var supervisor = SupervisorFactory.Create(launcher, options: options, timeProvider: time, pooledLaunchAdmission: admission);
        Task? disposal = null;
        try
        {
            await ServeOneTurnAsync(supervisor, "chat-a");
            await ServeOneTurnAsync(supervisor, "chat-b");
            options.MaxLoadedProcesses = 1;

            var readToken = await admission.ReadStarted.Task.WaitAsync(TestBudgets.Contended);
            disposal = supervisor.DisposeAsync().AsTask();
            var completed = true;
            try
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                completed = false;
            }

            AssertEx.True(completed, "disposal must not wait on a keep-warm read that ignores shutdown");
            AssertEx.True(readToken.IsCancellationRequested, "the keep-warm read runs on the shutdown token");
        }
        finally
        {
            admission.Unblock();
            if (disposal is null)
            {
                await supervisor.DisposeAsync();
            }
            else
            {
                await disposal;
            }
        }
    }

    [Test]
    public async Task TtlReap_ReadsTheIdleClockOnlyWhileHoldingTheClaim()
    {
        // A request that starts and ends between an unclaimed expiry check and the claim would leave the reap to unload a process it just
        // used. With the claim held first, no lease can start, and one that ended before stamped the idle clock first.
        var ttl = TimeSpan.FromMinutes(15);
        var started = DateTimeOffset.UtcNow;
        var processes = NewProcessTable();
        var process = RegisterProcess(processes, "chat-a", started).Value;
        bool? claimedAtTheExpiryRead = null;
        var clock = new CallbackClock(started + ttl + TimeSpan.FromMinutes(1))
        {
            // Read 1 is the scan; read 2 is the expiry check under the gate.
            OnRead = read => claimedAtTheExpiryRead = read == 2 ? process.IsEvicting : claimedAtTheExpiryRead
        };
        using var reaper = NewReaper(processes, CapOf(cap: 2, ttl), clock);

        await reaper.ReapIdleOnceAsync();

        AssertEx.Equal(true, claimedAtTheExpiryRead, "the expiry check must run while the eviction claim is held");
        AssertEx.True(process.Handle is FakeProcessTreeHandle { WasTreeKilled: true }, "the expired process is reaped");
    }

    [Test]
    public async Task TtlReap_AProcessFoundFreshUnderTheClaim_GetsTheClaimBack_AndStaysLeasable()
    {
        var ttl = TimeSpan.FromMinutes(15);
        var started = DateTimeOffset.UtcNow;
        var now = started + ttl + TimeSpan.FromMinutes(1);
        var processes = NewProcessTable();
        var process = RegisterProcess(processes, "chat-a", started).Value;

        // A request ended just before the claim: its release stamped the idle clock at the very instant of the expiry check.
        var clock = new CallbackClock(now)
        {
            OnRead = read =>
            {
                if (read == 2)
                {
                    process.StampLastUsed(now);
                }
            }
        };
        using var reaper = NewReaper(processes, CapOf(cap: 2, ttl), clock);

        await reaper.ReapIdleOnceAsync();

        AssertEx.False(process.Handle is FakeProcessTreeHandle { WasTreeKilled: true }, "a process used since the scan is kept");
        AssertEx.False(process.IsEvicting, "the claim is given back, so new requests can lease the process");
        AssertEx.Equal(expected: 1, processes.Count);
    }

    private static ConcurrentDictionary<LlamaServerProcessSupervisor.ProcessKey, LlamaServerProcessSupervisor.RunningProcess> NewProcessTable() =>
        new();

    private static LlamaServerIdleReaper NewReaper(ConcurrentDictionary<LlamaServerProcessSupervisor.ProcessKey, LlamaServerProcessSupervisor.RunningProcess> processes,
        LlamaServerSupervisorOptions options,
        TimeProvider timeProvider,
        CallbackResidencyChangeNotifier? notifier = null) =>
        new(processes, new LlamaServerPortAllocator(options), new LlamaLayerPlacementReport(), options, timeProvider, NullLogger.Instance, notifier);

    /// <summary>Puts a ready, unleased chat process last used at <paramref name="lastUsed" /> straight into the reaper's table.</summary>
    private static KeyValuePair<LlamaServerProcessSupervisor.ProcessKey, LlamaServerProcessSupervisor.RunningProcess> RegisterProcess(
        ConcurrentDictionary<LlamaServerProcessSupervisor.ProcessKey, LlamaServerProcessSupervisor.RunningProcess> processes,
        string modelName,
        DateTimeOffset lastUsed)
    {
        var key = new LlamaServerProcessSupervisor.ProcessKey(modelName, ModelRole.Chat);
#pragma warning disable CA2000 // The reaper owns the handle: a teardown disposes it.
        var process = new LlamaServerProcessSupervisor.RunningProcess(new FakeProcessTreeHandle(4000 + processes.Count),
            new LlamaServerEndpoint
            {
                ModelName = modelName,
                Role = ModelRole.Chat,
                BaseAddress = new Uri("http://127.0.0.1:1/")
            },
            port: 0,
            lastUsed);
#pragma warning restore CA2000
        processes[key] = process;
        return new KeyValuePair<LlamaServerProcessSupervisor.ProcessKey, LlamaServerProcessSupervisor.RunningProcess>(key, process);
    }

    /// <summary>A frozen clock that reports each <see cref="GetUtcNow" /> read, numbered from 1, before it returns.</summary>
    private sealed class CallbackClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        private int _reads;

        public CallbackClock(DateTimeOffset now)
        {
            _now = now;
        }

        public Action<int>? OnRead { get; init; }

        public override DateTimeOffset GetUtcNow()
        {
            OnRead?.Invoke(Interlocked.Increment(ref _reads));
            return _now;
        }
    }

    [Test]
    public async Task TtlReap_ALeaseTakenBetweenSelectionAndDetach_KeepsTheProcess()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        var ttl = TimeSpan.FromMinutes(15);
        ILlamaServerInferenceLease? lease = null;
        LlamaServerProcessSupervisor? target = null;

        // The pass logs its idle decision after the scan and before it takes the gate: a request leases the process right there. The
        // lease stamps the idle clock, so the clock moves past the TTL again: only the claim's lease check can keep the process.
        var logger = new OnInformationLogger<LlamaServerProcessSupervisor>("Evicting idle",
            () =>
            {
                lease = target!.TryAcquireInferenceLease("chat-a", ModelRole.Chat).Lease;
                time.Advance(ttl + TimeSpan.FromMinutes(1));
            });
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 2, ttl), timeProvider: time, loadTelemetry: telemetry, logger: logger);
        target = supervisor;
        await ServeOneTurnAsync(supervisor, "chat-a");
        time.Advance(ttl + TimeSpan.FromMinutes(1));

        await supervisor.ReapIdleOnceAsync();

        using var held = AssertEx.NotNull(lease, "the request in the window was granted its lease");
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "a process leased after the scan is never torn down under the request");
        AssertEx.Equal(expected: 1, supervisor.CountRunningProcesses());
        AssertEx.Empty(telemetry.Unloads, "no unload is recorded for a reap that did not happen");
    }

    [Test]
    public async Task TtlReap_ALeaseReleasedInTheWindow_RestartsTheIdleClock()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        var ttl = TimeSpan.FromMinutes(15);
        LlamaServerProcessSupervisor? target = null;

        // A short request runs and ends between the pass's scan and its claim.
        var logger = new OnInformationLogger<LlamaServerProcessSupervisor>("Evicting idle",
            () => AssertEx.NotNull(target!.TryAcquireInferenceLease("chat-a", ModelRole.Chat).Lease).Dispose());
        await using var supervisor = SupervisorFactory.Create(launcher, options: CapOf(cap: 2, ttl), timeProvider: time, loadTelemetry: telemetry, logger: logger);
        target = supervisor;
        await ServeOneTurnAsync(supervisor, "chat-a");
        time.Advance(ttl + TimeSpan.FromMinutes(1));

        await supervisor.ReapIdleOnceAsync();

        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "the release restarted the idle clock, so the process is in its window again");
        AssertEx.Empty(telemetry.Unloads);

        // One full TTL after that release it is reaped as usual, with exactly one recorded unload.
        time.Advance(ttl);
        await supervisor.ReapIdleOnceAsync();
        AssertEx.True(launcher.Handles.Single().WasTreeKilled);
        AssertEx.Equal("(chat-a, Chat, IdleTimeout)", string.Join(", ", telemetry.Unloads));
    }

    private static List<FakeProcessTreeHandle> ByLaunchOrder(FakeProcessLauncher launcher) =>
        [.. launcher.Handles.OrderBy(static handle => handle.ProcessId)];

    private static LlamaServerSupervisorOptions CapOf(int cap, TimeSpan ttl, int companionCap = 2)
    {
        return new LlamaServerSupervisorOptions
        {
            MaxLoadedProcesses = cap,
            MaxLoadedCompanionProcesses = companionCap,
            IdleTimeToLive = ttl,
            MaxRestartAttempts = 3
        };
    }

    /// <summary>A pooled launch admission whose keep-warm read blocks until its token is cancelled or the test unblocks it.</summary>
    private sealed class BlockingKeepWarmAdmission : ILlamaServerPooledLaunchAdmission
    {
        private readonly TaskCompletionSource _unblocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CancellationToken> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Unblock() =>
            _unblocked.TrySetResult();

        public Task<IDisposable?> AdmitAsync(string modelName, ModelRole role, CancellationToken ct) =>
            Task.FromResult<IDisposable?>(null);

        public Task<IDisposable?> AdmitChatAsync(string modelName,
            bool mayUnloadIdleModels,
            Func<IReadOnlyCollection<string>, CancellationToken, Task<IdleChatEvictionResult>> evictIdleModel,
            CancellationToken ct) =>
            Task.FromResult<IDisposable?>(null);

        public async Task<IReadOnlyCollection<string>> GetProtectedChatModelsAsync(CancellationToken ct)
        {
            ReadStarted.TrySetResult(ct);
            await _unblocked.Task.WaitAsync(ct);
            return [];
        }
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

        public Task<IReadOnlyCollection<string>> GetProtectedChatModelsAsync(CancellationToken ct) =>
            Task.FromResult(Protected);

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
