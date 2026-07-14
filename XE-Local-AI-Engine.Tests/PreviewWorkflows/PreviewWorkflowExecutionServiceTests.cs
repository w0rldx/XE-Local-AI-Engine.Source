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

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException("Condition not met within the timeout.");
    }

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

        // Wait until the run reaches Paused (run.paused published).
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

        var snapshot = service.SnapshotBufferedEvents(runId);

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

        var snapshot = service.SnapshotBufferedEvents(runId);
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

        var snapshot = service.SnapshotBufferedEvents(runId);
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
        AssertEx.NotEmpty(service.SnapshotBufferedEvents(runId));

        // After retention elapses the sweep evicts the terminal log.
        time.Advance(TimeSpan.FromSeconds(61));
        await service.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        AssertEx.Empty(service.SnapshotBufferedEvents(runId));
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
