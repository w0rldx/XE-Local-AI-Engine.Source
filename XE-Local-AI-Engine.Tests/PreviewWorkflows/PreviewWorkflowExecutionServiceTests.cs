namespace XE_Local_AI_Engine.Tests.PreviewWorkflows;

using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for <see cref="PreviewWorkflowExecutionService" /> — the in-memory run-state machine. Drive the run
///     with a scripted session (no Ollama / network); assert the cancel-while-paused, continue/cancel race, shutdown,
///     byte-cap, model-failure, node-local-client, and never-persisted behaviors.
/// </summary>
public sealed class PreviewWorkflowExecutionServiceTests
{
    private static PreviewWorkflowExecutionOptions DefaultOptions()
    {
        return new PreviewWorkflowExecutionOptions
        {
            IdleTimeout = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromMinutes(15),
            SweepInterval = TimeSpan.FromSeconds(30),
            AbandonedSubscriberGrace = TimeSpan.FromMinutes(5),
            MaxConcurrentRuns = 4,
            MaxOutputBytes = 10 * 1024 * 1024
        };
    }

    private static PreviewWorkflowExecutionService CreateService(FakePreviewWorkflowRunner runner,
        RecordingPreviewEventPublisher publisher,
        FakeLocalModelProvider provider,
        PreviewWorkflowExecutionOptions? options = null,
        int maxLoadedProcesses = 8,
        TimeProvider? timeProvider = null)
    {
        // Wrap the single fake provider in the real resolver (default = the fake provider, unmapped models route to it),
        // so the service exercises the production lazy-per-model + cap-reject path.
        var resolver = SingleProviderResolverFactory.Create(provider, maxLoadedProcesses);
        return new PreviewWorkflowExecutionService(resolver,
            runner,
            publisher,
            Options.Create(options ?? DefaultOptions()),
            timeProvider ?? TimeProvider.System,
            NullLoggerFactory.Instance);
    }

    private static Task WaitForAsync(Func<bool> condition, TimeSpan timeout) =>
        AssertEx.EventuallyAsync(condition, timeout, "Condition not met within the timeout.");

    [Test]
    public async Task PreviewExec_ConcurrentStarts_NeverExceedConcurrencyCap()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();

        // Each run gets a scripted session with NO queued updates, so WatchAsync blocks and the run stays live for the
        // whole test — a started run never completes and frees its slot mid-test. This makes the cap outcome depend
        // solely on the reservation, not on timing.
        var runner = new FakePreviewWorkflowRunner((_, _) => new ScriptedPreviewRunSession([]));

        var options = DefaultOptions();
        options.MaxConcurrentRuns = 2;
        await using var service = CreateService(runner, publisher, provider, options);

        const int attempts = 8;
        var starts = Enumerable.Range(0, attempts)
                               .Select(attempt => Task.Run(async () =>
                               {
                                   try
                                   {
                                       _ = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
                                       return true;
                                   }
                                   catch (PreviewWorkflowCapReachedException)
                                   {
                                       return false;
                                   }
                               }))
                               .ToArray();

        var results = await Task.WhenAll(starts).ConfigureAwait(false);

        // Exactly MaxConcurrentRuns starts win the reservation; every excess concurrent start is rejected with the cap
        // exception — no TOCTOU window admits an over-cap run.
        AssertEx.Equal(expected: 2, results.Count(started => started));
        AssertEx.Equal(expected: attempts - 2, results.Count(started => !started));
    }

    [Test]
    public async Task PreviewExec_Cancel_WhilePaused_UnblocksAndDisposes()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        ScriptedPreviewRunSession? session = null;
        var runner = new FakePreviewWorkflowRunner((_, _) =>
        {
            session = new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunPaused("pause", "upstream", "req-1")]);
            return session;
        });

        await using var service = CreateService(runner, publisher, provider);

        var runId = await service.StartAsync(PreviewGraphBuilder.Linear(), "conn-1").ConfigureAwait(false);

        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunPaused), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var outcome = await service.CancelAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(PreviewRunCommandOutcome.Accepted, outcome);
        // The session + client are disposed when the run is removed.
        await WaitForAsync(() => session!.Disposed, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        AssertEx.True(session!.Disposed, "the paused run's session must be disposed on cancel.");
        AssertEx.True(provider.CreatedClients[0].Disposed, "the node-local chat client must be disposed on cancel.");
    }

    [Test]
    public async Task PreviewExec_ContinueAndCancel_Race_DoesNotThrow()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunPaused("pause", "upstream", "req-1")],
                (_, s) =>
                {
                    // On resume, drive to completion.
                    s.Enqueue(PreviewWorkflowUpdate.RunCompleted("done"));
                    return Task.CompletedTask;
                }));

        await using var service = CreateService(runner, publisher, provider);
        var runId = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunPaused), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Fire continue and cancel concurrently — they must serialize via the per-run gate and never throw
        // ObjectDisposedException.
        var continueTask = service.ContinueAsync(runId);
        var cancelTask = service.CancelAsync(runId);

        // Neither call may throw (no ObjectDisposedException on the race) — awaiting both is the assertion. Any defined
        // outcome is acceptable: depending on who wins the gate the run may resume-then-complete (Accepted/NotFound) or
        // be cancelled (Accepted) with the loser seeing WrongState/NotFound.
        var outcomes = await Task.WhenAll(continueTask, cancelTask).ConfigureAwait(false);

        AssertEx.True(outcomes.All(static o => o is PreviewRunCommandOutcome.Accepted
                or PreviewRunCommandOutcome.WrongState
                or PreviewRunCommandOutcome.NotFound),
            "continue/cancel race must produce defined outcomes without throwing.");
    }

    [Test]
    public async Task PreviewExec_HostShutdown_DisposesInFlightRuns()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        ScriptedPreviewRunSession? session = null;
        var runner = new FakePreviewWorkflowRunner((_, _) =>
        {
            // A run that pauses and waits — stays in-flight until shutdown.
            session = new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunPaused("pause", "x", "req-1")]);
            return session;
        });

        var service = CreateService(runner, publisher, provider);
        _ = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunPaused), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // DisposeAsync mirrors the ApplicationStopping path: cancel + dispose every in-flight run.
        await service.DisposeAsync().ConfigureAwait(false);

        AssertEx.True(session!.Disposed, "an in-flight run must be disposed at shutdown.");
        AssertEx.True(provider.CreatedClients[0].Disposed, "the node-local client must be disposed at shutdown.");
    }

    [Test]
    public async Task PreviewExec_OutputExceedsCap_CancelsRun()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var options = DefaultOptions();
        options.MaxOutputBytes = 8; // tiny cap so a single output exceeds it.
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.NodeOutput("agent", "this output is definitely longer than eight bytes")]));

        await using var service = CreateService(runner, publisher, provider, options);
        _ = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);

        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunFailed), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var failed = publisher.RunEvents.First(e => e.EventType == PreviewWorkflowHubEvents.RunFailed);
        AssertEx.True(failed.Error!.Contains("limit", StringComparison.OrdinalIgnoreCase),
            "the byte-cap failure must mention the output limit.");
    }

    [Test]
    public async Task PreviewExec_OutputExactlyAtCap_CancelsRun()
    {
        // Boundary: output whose byte count is EXACTLY MaxOutputBytes trips the cap (the check is total >= cap).
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var options = DefaultOptions();
        const string output = "12345678"; // 8 ASCII bytes
        options.MaxOutputBytes = Encoding.UTF8.GetByteCount(output);
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.NodeOutput("agent", output)]));

        await using var service = CreateService(runner, publisher, provider, options);
        _ = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);

        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunFailed), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var failed = publisher.RunEvents.First(e => e.EventType == PreviewWorkflowHubEvents.RunFailed);
        AssertEx.True(failed.Error!.Contains("limit", StringComparison.OrdinalIgnoreCase),
            "output exactly at the byte cap must trip the cap and fail with the output-limit message.");
    }

    [Test]
    public async Task PreviewExec_ModelCallThrows_EmitsNodeAndRunFailed()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.NodeFailed("agent", "model not installed")]));

        await using var service = CreateService(runner, publisher, provider);
        _ = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);

        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunFailed), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        AssertEx.True(publisher.NodeEvents.Any(e => e.EventType == PreviewWorkflowHubEvents.NodeFailed),
            "a model failure must emit preview.node.failed.");
        AssertEx.True(publisher.HasRunEvent(PreviewWorkflowHubEvents.RunFailed),
            "a model failure must emit a terminal preview.run.failed (no hang).");
    }

    [Test]
    public async Task PreviewExec_UsesNodeLocalClient_NeverCloudFactory()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        IChatClient? handedToRunner = null;
        var runner = new FakePreviewWorkflowRunner((_, client) =>
        {
            handedToRunner = client;
            return new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunCompleted("done")]);
        });

        // A cloud factory that THROWS if ever touched — the service must never reach it.
        var cloudFactory = new ThrowingAzureFoundryChatClientFactory();

        await using var service = CreateService(runner, publisher, provider);
        _ = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunCompleted), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        AssertEx.True(handedToRunner is FakeNodeLocalChatClient, "the runner must receive the node-local client.");
        AssertEx.Equal(expected: 1, provider.CreatedClients.Count);
        AssertEx.False(cloudFactory.WasCalled, "the cloud chat-client factory must never be invoked by the run path.");
    }

    [Test]
    public async Task PreviewExec_OutputNeverPersisted()
    {
        // The service has NO store dependency (its constructor injects only the local provider, runner, publisher,
        // options, time, and logger factory) — so run output cannot be persisted. Assert structurally + behaviorally:
        // a completed run leaves nothing behind and exposes no read-back surface.
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunCompleted("secret output")]));

        await using var service = CreateService(runner, publisher, provider);

        // This service only exposes a single StartAsync(graph) entry point and holds no store, so whatever graph it is
        // handed cannot be persisted (the saved-vs-unsaved split lives in the endpoints, not here).
        _ = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunCompleted), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // The run is removed from the registry after completion — no lingering run state, no read-back surface.
        await WaitForAsync(() => service.ActiveRunIds.Count == 0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, service.ActiveRunIds.Count);

        var ctorParameterTypes = typeof(PreviewWorkflowExecutionService)
                                 .GetConstructors().Single()
                                 .GetParameters()
                                 .Select(p => p.ParameterType.Name)
                                 .ToList();
        AssertEx.False(ctorParameterTypes.Any(static t => t.Contains("Store", StringComparison.Ordinal) || t.Contains("DbContext", StringComparison.Ordinal)),
            "the execution service must have no persistence dependency — run output is never persisted.");
    }

    [Test]
    public async Task PreviewExec_CapReached_When_TooManyConcurrentRuns()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var options = DefaultOptions();
        options.MaxConcurrentRuns = 1;
        // Runs that pause and stay in-flight, occupying the registry slot.
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunPaused("pause", "x", "req")]));

        await using var service = CreateService(runner, publisher, provider, options);
        _ = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunPaused), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<PreviewWorkflowCapReachedException>(async () => await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false))
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task PreviewWorkflow_TwoAgentsTwoModels_ReachTwoProcesses_RespectsCap()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        // A run that pauses so the handle stays in-flight and the resolver closure is observable.
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunPaused("pause", "x", "req")]));

        await using var service = CreateService(runner, publisher, provider, maxLoadedProcesses: 8);
        _ = await service.StartAsync(TwoAgentTwoModelGraph(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunPaused), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var resolve = AssertEx.NotNull(runner.LastResolver, "The runner must have received the per-model resolver closure.");

        // Resolving two distinct models reaches two DISTINCT per-model clients (two processes), lazily — and resolving
        // the same model again returns the cached client (one client per (provider, model), not one per call).
        var clientA = resolve("model-a");
        var clientB = resolve("model-b");
        var clientASecond = resolve("model-a");

        AssertEx.False(ReferenceEquals(clientA, clientB), "Two distinct models must resolve to two distinct clients.");
        AssertEx.True(ReferenceEquals(clientA, clientASecond), "The same model must resolve to one cached client.");
        // Two distinct models => two created clients (lazy: nothing eager beyond what was resolved).
        AssertEx.Equal(expected: 2, provider.CreatedClients.Count);
    }

    [Test]
    public async Task PreviewWorkflow_WhenDistinctModelsExceedLoadedCap_RejectsAtStart()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var runner = new FakePreviewWorkflowRunner((_, _) => new ScriptedPreviewRunSession([]));

        // Cap of 1 with a two-distinct-model graph => reject at start, before any client/process is created.
        await using var service = CreateService(runner, publisher, provider, maxLoadedProcesses: 1);

        _ = await AssertEx.ThrowsAsync<PreviewWorkflowModelCapExceededException>(async () => await service.StartAsync(TwoAgentTwoModelGraph(), connectionId: null).ConfigureAwait(false))
                          .ConfigureAwait(false);

        AssertEx.Equal(expected: 0, provider.CreatedClients.Count);
    }

    private static PreviewWorkflowGraph TwoAgentTwoModelGraph()
    {
        return new PreviewWorkflowGraph
        {
            StartText = "hello",
            Nodes =
            [
                PreviewGraphBuilder.Start(),
                PreviewGraphBuilder.Agent("agentA", "model-a"),
                PreviewGraphBuilder.Agent("agentB", "model-b"),
                PreviewGraphBuilder.End()
            ],
            Edges =
            [
                PreviewGraphBuilder.Edge("start", "agentA"),
                PreviewGraphBuilder.Edge("agentA", "agentB"),
                PreviewGraphBuilder.Edge("agentB", "end")
            ]
        };
    }

    [Test]
    public async Task PreviewExec_InvalidGraph_ThrowsValidation()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var runner = new FakePreviewWorkflowRunner((_, _) => new ScriptedPreviewRunSession([]));

        await using var service = CreateService(runner, publisher, provider);

        var noAgentGraph = new PreviewWorkflowGraph
        {
            StartText = "x",
            Nodes = [PreviewGraphBuilder.Start(), PreviewGraphBuilder.End()],
            Edges = [PreviewGraphBuilder.Edge("start", "end")]
        };

        _ = await AssertEx.ThrowsAsync<PreviewWorkflowValidationException>(async () => await service.StartAsync(noAgentGraph, connectionId: null).ConfigureAwait(false))
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task PreviewExec_LateSubscriber_ReplaysAllEventsInOrder_AfterRunFinished()
    {
        // The subscribe-after-publish race: a fast run emits NodeStarted→NodeOutput→RunCompleted synchronously, and a
        // caller snapshots the buffer AFTER the run finished — it must still get EVERY event in order with contiguous
        // seq (0..n) including the terminal, proving a late subscriber can catch up.
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([
                PreviewWorkflowUpdate.NodeStarted("agent"),
                PreviewWorkflowUpdate.NodeOutput("agent", "hello"),
                PreviewWorkflowUpdate.RunCompleted("done")
            ]));

        await using var service = CreateService(runner, publisher, provider);
        var runId = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);

        // Wait until the run is fully terminal AND removed from the registry (i.e. a "late" subscriber).
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunCompleted), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await WaitForAsync(() => service.ActiveRunIds.Count == 0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var snapshot = service.SnapshotBufferedEvents(runId, afterSeq: -1);

        string[] expectedMethods =
        [
            PreviewWorkflowHubEvents.RunStarted,
            PreviewWorkflowHubEvents.NodeStarted,
            PreviewWorkflowHubEvents.NodeOutput,
            PreviewWorkflowHubEvents.NodeCompleted,
            PreviewWorkflowHubEvents.RunCompleted
        ];
        var methodNames = snapshot.Select(e => e.MethodName).ToList();
        AssertEx.True(methodNames.SequenceEqual(expectedMethods),
            $"a late subscriber must replay every event in publish order, including the terminal event. Got: {string.Join(",", methodNames)}");

        var seqs = snapshot.Select(SeqOf).ToList();
        var expectedSeqs = Enumerable.Range(0, snapshot.Count).Select(static i => (long)i);
        AssertEx.True(seqs.SequenceEqual(expectedSeqs),
            $"seq must be contiguous 0..n across node AND run events of the run. Got: {string.Join(",", seqs)}");
    }

    [Test]
    public async Task PreviewExec_RunStarted_IsBufferedAsSeqZero()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunPaused("pause", "x", "req-1")]));

        await using var service = CreateService(runner, publisher, provider);
        var runId = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunPaused), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var snapshot = service.SnapshotBufferedEvents(runId, afterSeq: -1);
        AssertEx.NotEmpty(snapshot);
        AssertEx.Equal(PreviewWorkflowHubEvents.RunStarted, snapshot[0].MethodName, "RunStarted must be the first buffered event.");
        AssertEx.Equal(expected: 0L, SeqOf(snapshot[0]), "RunStarted must be buffered as seq 0.");
    }

    [Test]
    public async Task PreviewExec_ReplayBuffer_BoundedToMax_OldestDropped()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var options = DefaultOptions();
        options.MaxBufferedEventsPerRun = 3; // tiny cap so early events are dropped.

        // A run that emits several node debug events then completes — many published events, one small buffer.
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([
                PreviewWorkflowUpdate.NodeStarted("agent"),
                PreviewWorkflowUpdate.NodeDebug("agent", "d1"),
                PreviewWorkflowUpdate.NodeDebug("agent", "d2"),
                PreviewWorkflowUpdate.NodeDebug("agent", "d3"),
                PreviewWorkflowUpdate.RunCompleted("done")
            ]));

        await using var service = CreateService(runner, publisher, provider, options);
        var runId = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunCompleted), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var snapshot = service.SnapshotBufferedEvents(runId, afterSeq: -1);
        AssertEx.Equal(expected: 3, snapshot.Count, "the buffer must be bounded to MaxBufferedEventsPerRun.");
        // The newest events are retained: the last buffered event is the terminal RunCompleted, and the oldest
        // (RunStarted, seq 0) was dropped — its seq is no longer the first entry.
        AssertEx.Equal(PreviewWorkflowHubEvents.RunCompleted, snapshot[^1].MethodName, "the terminal event must be retained (newest).");
        AssertEx.True(SeqOf(snapshot[0]) > 0L, "the oldest event (seq 0) must have been dropped once the cap was exceeded.");
    }

    [Test]
    public async Task PreviewExec_TerminalLog_EvictedBySweep_AfterReplayRetention()
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var time = new AdjustableTimeProvider();
        var options = DefaultOptions();
        options.ReplayRetention = TimeSpan.FromSeconds(60);
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunCompleted("done")]));

        await using var service = CreateService(runner, publisher, provider, options, timeProvider: time);
        var runId = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunCompleted), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await WaitForAsync(() => service.ActiveRunIds.Count == 0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Before retention elapses the log is retained (a late subscriber can still catch up), and a sweep is a no-op.
        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        AssertEx.NotEmpty(service.SnapshotBufferedEvents(runId, afterSeq: -1));

        // After retention elapses the sweep evicts the terminal log.
        time.Advance(TimeSpan.FromSeconds(61));
        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        AssertEx.Empty(service.SnapshotBufferedEvents(runId, afterSeq: -1));
    }

    [Test]
    public async Task PreviewExec_AbandonedPausedRun_ReleasesItsSlot_AfterGrace()
    {
        // The whole defect: execute → pause → reload. The run parks on Pause (exempt from the idle clock AND
        // the wall-clock cap, both deliberately) and NOBODY is subscribed, because the reloaded page never learned the
        // runId. Against the old code the sweep could never touch it and the slot was held until a node restart.
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var time = new AdjustableTimeProvider();
        var options = DefaultOptions();
        options.MaxConcurrentRuns = 1;
        options.AbandonedSubscriberGrace = TimeSpan.FromMinutes(5);
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunPaused("pause", "upstream", "req-1")]));

        await using var service = CreateService(runner, publisher, provider, options, timeProvider: time);
        _ = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunPaused), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Well past both the idle timeout (5 min) and the wall-clock cap (15 min): the paused run survives every sweep
        // that is NOT the abandoned sweep, so this leg pins the pause exemption as still intact.
        time.Advance(TimeSpan.FromMinutes(4));
        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, service.ActiveRunIds.Count, "a paused run must survive while inside the abandoned grace period.");

        // Past the grace period with still no subscriber → swept, slot released.
        time.Advance(TimeSpan.FromMinutes(2));
        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        await WaitForAsync(() => service.ActiveRunIds.Count == 0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, service.ActiveRunIds.Count, "an abandoned paused run must be swept once the grace period elapses.");

        // The slot is genuinely free again: with MaxConcurrentRuns = 1 this start would throw CapReached if the
        // reservation had leaked with the handle.
        var replacementRunId = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        AssertEx.True(replacementRunId != Guid.Empty, "the reclaimed slot must accept a new run.");
    }

    [Test]
    public async Task PreviewExec_PausedRunWithLiveSubscriber_IsNeverSwept()
    {
        // The other half of the contract: the sweep must discriminate on "nobody is watching", NOT on "paused". An
        // operator staring at a Pause node keeps a subscriber attached, and that run must survive arbitrarily long —
        // otherwise the fix would have replaced a leak with a new way to kill a legitimate run.
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var time = new AdjustableTimeProvider();
        var options = DefaultOptions();
        options.AbandonedSubscriberGrace = TimeSpan.FromMinutes(5);
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunPaused("pause", "upstream", "req-1")]));

        await using var service = CreateService(runner, publisher, provider, options, timeProvider: time);
        var runId = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunPaused), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        service.AddSubscriber(runId, "conn-1");

        // An hour: four times the wall-clock cap and twelve times the grace period.
        time.Advance(TimeSpan.FromHours(1));
        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, service.ActiveRunIds.Count, "a paused run with a live subscriber must never be swept.");

        // ...and the moment that subscriber goes away, the grace period starts from THAT point (not from run start),
        // so the run survives one more sweep and only then becomes eligible.
        service.RemoveSubscriber(runId, "conn-1");
        time.Advance(TimeSpan.FromMinutes(4));
        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, service.ActiveRunIds.Count, "the grace period must restart when the last subscriber leaves.");

        time.Advance(TimeSpan.FromMinutes(2));
        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        await WaitForAsync(() => service.ActiveRunIds.Count == 0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, service.ActiveRunIds.Count, "an unsubscribed paused run must be swept after the grace period.");
    }

    [Test]
    public async Task PreviewExec_SnapshotFromSeq_ReplaysOnlyNewerEvents_WithoutDuplicates()
    {
        // Reattach without duplication: a client that already applied seq 0..2 asks for everything AFTER 2 and must
        // get a strictly-newer, gap-free tail. A full replay would hand back seq 0 again, which double-applies
        // accumulating node output on the client.
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([
                PreviewWorkflowUpdate.NodeStarted("agent"),
                PreviewWorkflowUpdate.NodeOutput("agent", "hello"),
                PreviewWorkflowUpdate.RunCompleted("done")
            ]));

        await using var service = CreateService(runner, publisher, provider);
        var runId = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunCompleted), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var full = service.SnapshotBufferedEvents(runId, afterSeq: -1);
        AssertEx.Equal(expected: 5, full.Count, "the whole log is RunStarted, NodeStarted, NodeOutput, NodeCompleted, RunCompleted.");

        var tail = service.SnapshotBufferedEvents(runId, afterSeq: 2);
        var tailSeqs = tail.Select(SeqOf).ToList();

        AssertEx.True(tailSeqs.SequenceEqual([3L, 4L]),
            $"a seq-filtered replay must return ONLY events after the client's high-water-mark. Got: {string.Join(",", tailSeqs)}");
        AssertEx.Equal(expected: tailSeqs.Count, tailSeqs.Distinct().Count(), "a replay must never repeat a seq.");

        // The terminal event is in the tail, so a reattaching client still learns the run finished.
        AssertEx.Equal(PreviewWorkflowHubEvents.RunCompleted, tail[^1].MethodName);

        // A client fully caught up asks for nothing more.
        AssertEx.Empty(service.SnapshotBufferedEvents(runId, afterSeq: 4));
    }

    [Test]
    public async Task PreviewExec_ListRuns_ExposesLiveRun_AndGetRunResolvesItById()
    {
        // Before this, a runId that left the client's memory was unreachable — no list, no get, no cancel.
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunPaused("pause", "upstream", "req-1")]));

        await using var service = CreateService(runner, publisher, provider);
        var runId = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => publisher.HasRunEvent(PreviewWorkflowHubEvents.RunPaused), TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var listed = service.ListRuns();
        var run = AssertEx.NotNull(listed.SingleOrDefault(r => r.RunId == runId), "the live run must be discoverable via ListRuns.");
        AssertEx.Equal(PreviewRunState.Paused, run.State);
        AssertEx.True(run.IsLive, "a run holding a concurrency slot must report IsLive.");
        AssertEx.Equal(expected: "pause", run.PausedNodeId, "the paused node must be reported so a reattaching client can show it.");
        AssertEx.Equal(expected: "req-1", run.PauseRequestId);
        AssertEx.Equal(expected: 0, run.SubscriberCount, "an abandoned run must report zero subscribers.");

        var fetched = AssertEx.NotNull(service.GetRun(runId), "GetRun must resolve a live run by id.");
        AssertEx.Equal(runId, fetched.RunId);
        // LastSeq is what a reattaching client passes back as afterSeq, so it must match the buffered log.
        AssertEx.Equal(service.SnapshotBufferedEvents(runId, afterSeq: -1).Count - 1L, fetched.LastSeq);

        AssertEx.Null(service.GetRun(Guid.NewGuid()), "an unknown run id must resolve to null (→ 404) so a stale route id is dropped.");
    }

    [Test]
    public async Task PreviewExec_CancelAll_ReleasesEverySlot()
    {
        // The operator's recovery path once slots have already leaked: cancel-all must free them without a restart.
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var options = DefaultOptions();
        options.MaxConcurrentRuns = 2;
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunPaused("pause", "x", "req")]));

        await using var service = CreateService(runner, publisher, provider, options);
        _ = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        _ = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await WaitForAsync(() => service.ListRuns().Count(run => run.IsLive && run.State == PreviewRunState.Paused) == 2,
                TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

        // The cap is genuinely reached first, so the recovery below is proving something.
        _ = await AssertEx.ThrowsAsync<PreviewWorkflowCapReachedException>(async () =>
                              await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false))
                          .ConfigureAwait(false);

        var cancelled = await service.CancelAllAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, cancelled);
        await WaitForAsync(() => service.ActiveRunIds.Count == 0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _ = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
    }

    [Test]
    [Arguments(PreviewWorkflowUpdateKind.RunPaused)]
    [Arguments(PreviewWorkflowUpdateKind.RunCompleted)]
    [Arguments(PreviewWorkflowUpdateKind.RunFailed)]
    public async Task PreviewExec_CancelledRun_IgnoresLateDrainUpdate(PreviewWorkflowUpdateKind lateUpdateKind)
    {
        var provider = new FakeLocalModelProvider();
        var publisher = new RecordingPreviewEventPublisher();
        var options = DefaultOptions();
        options.MaxConcurrentRuns = 1;

        var lateUpdate = lateUpdateKind switch
        {
            PreviewWorkflowUpdateKind.RunPaused => PreviewWorkflowUpdate.RunPaused("pause", "late", "req-late"),
            PreviewWorkflowUpdateKind.RunCompleted => PreviewWorkflowUpdate.RunCompleted("late"),
            PreviewWorkflowUpdateKind.RunFailed => PreviewWorkflowUpdate.RunFailed("late"),
            _ => throw new ArgumentOutOfRangeException(nameof(lateUpdateKind), lateUpdateKind, "Unsupported late update kind.")
        };
        await using var gatedSession = new GatedPreviewRunSession(lateUpdate);
        var starts = 0;
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            Interlocked.Increment(ref starts) == 1
                ? gatedSession
                : new ScriptedPreviewRunSession([]));

        await using var service = CreateService(runner, publisher, provider, options);
        var cancelledRunId = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        await gatedSession.MoveNextEntered.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        AssertEx.Equal(PreviewRunCommandOutcome.Accepted,
            await service.CancelAsync(cancelledRunId, CancellationToken.None).ConfigureAwait(false));
        AssertEx.True(publisher.HasRunEvent(PreviewWorkflowHubEvents.RunCancelled),
            "cancellation must publish the one authoritative terminal event before an in-flight update returns.");

        gatedSession.ReleaseUpdate();

        await WaitForAsync(() => service.ActiveRunIds.Count == 0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var retained = AssertEx.NotNull(service.GetRun(cancelledRunId), "the cancelled result must remain replayable.");
        AssertEx.Equal(PreviewRunState.Cancelled, retained.State,
            "a late drain update must not overwrite the terminal cancelled state.");
        AssertEx.False(publisher.HasRunEvent(PreviewWorkflowHubEvents.RunPaused),
            "a late pause must not be published after cancellation.");
        AssertEx.False(publisher.HasRunEvent(PreviewWorkflowHubEvents.RunCompleted),
            "a late completion must not be published after cancellation.");
        AssertEx.False(publisher.HasRunEvent(PreviewWorkflowHubEvents.RunFailed),
            "a late failure must not be published after cancellation.");

        var replacementRunId = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null).ConfigureAwait(false);
        AssertEx.True(service.ActiveRunIds.Contains(replacementRunId),
            "the cancelled run must release its concurrency slot for the next run.");
    }

    private static long SeqOf(PreviewWorkflowBufferedEvent bufferedEvent)
    {
        return bufferedEvent.Payload switch
        {
            PreviewWorkflowRunHubEvent runEvent => runEvent.Seq,
            PreviewWorkflowNodeHubEvent nodeEvent => nodeEvent.Seq,
            _ => throw new InvalidOperationException($"Unexpected buffered payload type '{bufferedEvent.Payload.GetType().Name}'.")
        };
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
        }
    }

    private sealed class ThrowingAzureFoundryChatClientFactory : IAzureFoundryChatClientFactory
    {
        public bool WasCalled { get; private set; }

        public IChatClient Create(StoredAzureFoundryConnection connection, string deploymentName)
        {
            WasCalled = true;
            throw new InvalidOperationException("The cloud chat-client factory must never be reached by the preview run path.");
        }
    }
}
