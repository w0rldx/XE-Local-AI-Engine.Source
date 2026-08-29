namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.WorkSessions;

/// <summary>
///     The Slice A ship gate: the seeded workflow runs end to end on the REAL work-session machinery, survives the
///     engine restarting under it, and is replayable from its own event log afterwards.
///     <para>
///         Everything else in this namespace scripts the agent seam so the graph can be exercised without a model.
///         This one does not: it boots the real supervisor, the real lifecycle service and the real seeders, with only
///         the chat send path faked — which is the same substitution the work-session suites make one level down. It is
///         the only test that proves the two families are actually wired to each other.
///     </para>
/// </summary>
public sealed class DevWorkflowSliceATests
{
    /// <summary>How long one agent node's session may take to settle before the test calls it stuck.</summary>
    private static readonly TimeSpan SessionGrace = TimeSpan.FromSeconds(30);

    /// <summary>The seeder is idempotent on its slug, and an archived template is never resurrected under it.</summary>
    [Test]
    public async Task TheSeededTemplate_IsWrittenOnceAndPassesTheSameValidationARunStartUses()
    {
        await using var factory = NewFactory();

        await SeedTemplatesAsync(factory).ConfigureAwait(false);
        await SeedTemplatesAsync(factory).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var definitions = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListDefinitionsAsync(includeArchived: true).ConfigureAwait(false);
        var seeded = AssertEx.NotNull(definitions.SingleOrDefault(static definition => definition.SeedSlug == "research-plan-approval"),
            "the template is seeded exactly once, however often the node starts.");

        AssertEx.Equal(DevWorkflowDefinitionSource.Seeded, seeded.Source);
        AssertEx.Equal(expected: 3, seeded.NodeCount);
        AssertEx.False(seeded.Archived);

        var snapshot = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().GetDefinitionAsync(seeded.Id).ConfigureAwait(false);
        var graph = DevWorkflowGraph.Parse(snapshot.GraphJson);
        AssertEx.Equal("research", graph.EntryNodeKeys.Single(), "one entry node, and it is the one the run starts on.");
        AssertEx.Equal(DevWorkflowNodeType.HumanGate, graph.Nodes["approve"].NodeType, "the template ends on the approval that is the point of it.");
    }

    /// <summary>
    ///     The gate itself. The seeded "Research → Plan → Approval" template runs on real work sessions, is restarted
    ///     mid-run, finishes on a human's answer, and leaves an event log a client can replay from any watermark.
    ///     <para>
    ///         The restart is taken while the first node's session sits parked on its own step budget — walkthrough row
    ///         #4 — because that is the in-flight state a test process can reach honestly without killing itself. Row
    ///         #3, the genuinely mid-step kill, is asserted deterministically against the scripted seam next door.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TheSeededWorkflow_RunsOnRealWorkSessionsSurvivesARestartAndReplays()
    {
        FakeNodeChatStreamService? stream = null;
        var publisher = new RecordingWorkSessionEventPublisher();
        await using var factory = NewFactory(services => WorkSessionTestSupport.WithFakes(
            provider => stream = new FakeNodeChatStreamService(provider.GetRequiredService<INodeChatStreamCancellationRegistry>(), provider, Guid.Empty),
            publisher)(services));

        await SeedTemplatesAsync(factory).ConfigureAwait(false);

        // Two turns per agent node, in the order the node's one invocation slot forces: the first does work and stops on
        // the one-step budget, and the second finishes. That parking is the ordinary shape of a workflow agent node —
        // a node routinely needs more steps than one session run allows — and it is what the runtime resumes.
        var fake = ResolveStream(factory, ref stream);
        for (var node = 0; node < 2; node++)
        {
            fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantCompleted]));
            fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantCompleted], (services, _) => FinishTheWorkflowSessionAsync(services)));
        }

        var definitionId = await FindSeededDefinitionAsync(factory).ConfigureAwait(false);
        var workItemId = await CreateWorkItemAsync(factory, "Explain how the inference path works.").ConfigureAwait(false);

        Guid runId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var started = await scope.ServiceProvider.GetRequiredService<IDevWorkflowRunService>()
                                     .StartAsync(workItemId, definitionId, inputsJson: null, Guid.NewGuid())
                                     .ConfigureAwait(false);
            runId = started.Run.Id;
            AssertEx.Equal(expected: 3, started.NodeRuns.Count, "every node of the pinned graph has a row from the start.");
        }

        // Driven to the moment the first node's session has stopped on its step budget with the node run still Running:
        // the run is genuinely mid-work, and the engine dies here.
        var dispatcher = factory.Services.GetRequiredService<DevWorkflowDispatcher>();
        await DriveUntilAsync(factory,
                dispatcher,
                runId,
                _ => ReadNodeRun(factory, runId, "research") is { Status: DevWorkflowNodeRunStatus.Running, WorkSessionId: { } parked }
                     && ReadSession(factory, parked).Status == AgentWorkSessionStatus.Paused)
            .ConfigureAwait(false);

        var beforeRestart = ReadNodeRun(factory, runId, "research");
        var sessionId = beforeRestart.WorkSessionId;
        await using var restarted = await RestartAsync(factory, dispatcher).ConfigureAwait(false);

        var afterRestart = ReadNodeRun(factory, runId, "research");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, afterRestart.Status, "a restart makes an in-flight node run dispatchable again, not failed.");
        AssertEx.Equal(beforeRestart.Attempt, afterRestart.Attempt, "the restart cost no attempt: the session resumes from a checkpoint it wrote itself.");
        AssertEx.Equal(sessionId, afterRestart.WorkSessionId, "and it resumes THAT session rather than starting the work over.");

        await DriveUntilAsync(factory, restarted, runId, static run => run.Status == DevWorkflowRunStatus.WaitingForApproval).ConfigureAwait(false);

        var gate = ReadNodeRun(factory, runId, "approve");
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, gate.Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, ReadNodeRun(factory, runId, "research").Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, ReadNodeRun(factory, runId, "plan").Status);
        AssertEx.NotEmpty(await ReadConsumedArtifactIdsAsync(factory, gate.Id).ConfigureAwait(false),
            "the gate renders the evidence it was handed, so it has to have recorded consuming it.");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            _ = await scope.ServiceProvider.GetRequiredService<IDevWorkflowRunService>()
                           .DecideAsync(runId, gate.Id, Guid.NewGuid(), DevWorkflowDecisionKind.Approve, "Ship it.", payloadJson: null, "operator@localhost.test")
                           .ConfigureAwait(false);
        }

        await DriveUntilAsync(factory, restarted, runId, static run => run.Status == DevWorkflowRunStatus.Completed).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Completed, await ReadWorkItemStatusAsync(factory, workItemId).ConfigureAwait(false));

        // Two work sessions, one per agent node, each owned by the node run that created it and driven only by the run.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var sessions = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().ListAsync().ConfigureAwait(false);
            AssertEx.Equal(expected: 2, sessions.Count(static session => session.Kind == AgentWorkSessionKind.Workflow));
            AssertEx.Empty(sessions.Where(static session => session is { Kind: AgentWorkSessionKind.Workflow, Status: not AgentWorkSessionStatus.Completed }));
        }

        // Replayable from any watermark: strictly increasing, no repeats. NOT contiguous, deliberately — one counter
        // per run serves the events, the node runs and the artifacts, so the event feed steps over the numbers the rows
        // it describes took.
        var events = await ReadEventsAsync(factory, runId).ConfigureAwait(false);
        var sequences = events.Select(static entry => entry.Sequence).ToList();
        AssertEx.NotEmpty(sequences);
        AssertEx.True(sequences.Zip(sequences.Skip(1)).All(static pair => pair.First < pair.Second),
            "a client replaying from a watermark must see each row once and in order.");
        AssertEx.Contains(events, static entry => entry.EventType == "run.completed");
        AssertEx.Contains(events, static entry => entry.EventType == "node.interrupted", "the restart is in the audit, not hidden by it.");
        AssertEx.Contains(events, static entry => entry.EventType == "artifact.created", "what the agents produced belongs to the run, not only to their sessions.");
    }

    /// <summary>
    ///     The two seeders, run by hand: the test host strips every hosted service, so nothing starts them on its own.
    ///     Their ORDER does not matter — the workflow template names its agents by seed slug, and the slug is resolved
    ///     when a node dispatches rather than when the template is written.
    /// </summary>
    private static async Task SeedTemplatesAsync(TestServerWebAppFactory factory)
    {
        var scopes = factory.Services.GetRequiredService<IServiceScopeFactory>();
        await new WorkSessionAgentSeeder(scopes, factory.Services.GetRequiredService<ILogger<WorkSessionAgentSeeder>>()).StartAsync(CancellationToken.None)
                                                                                                                        .ConfigureAwait(false);
        await new DevWorkflowDefinitionSeeder(scopes,
                  factory.Services.GetRequiredService<IOptions<DevWorkflowOptions>>(),
                  factory.Services.GetRequiredService<ILogger<DevWorkflowDefinitionSeeder>>())
              .StartAsync(CancellationToken.None)
              .ConfigureAwait(false);
    }

    /// <summary>A host restart: both reconcilers in registration order, then a dispatcher that remembers nothing.</summary>
    private static async Task<DevWorkflowDispatcher> RestartAsync(TestServerWebAppFactory factory, DevWorkflowDispatcher dispatcher)
    {
        await dispatcher.DisposeAsync().ConfigureAwait(false);

        var scopes = factory.Services.GetRequiredService<IServiceScopeFactory>();
        await new WorkSessionStartupReconciler(scopes,
                  factory.Services.GetRequiredService<IOptions<WorkSessionOptions>>(),
                  factory.Services.GetRequiredService<ILogger<WorkSessionStartupReconciler>>())
              .StartAsync(CancellationToken.None)
              .ConfigureAwait(false);
        await new DevWorkflowStartupReconciler(scopes,
                  factory.Services.GetRequiredService<IOptions<DevWorkflowOptions>>(),
                  factory.Services.GetRequiredService<ILogger<DevWorkflowStartupReconciler>>())
              .StartAsync(CancellationToken.None)
              .ConfigureAwait(false);

        return new DevWorkflowDispatcher(scopes,
            new DevWorkflowGraphCache(),
            factory.Services.GetRequiredService<IOptions<DevWorkflowOptions>>(),
            factory.Services.GetRequiredService<TimeProvider>(),
            factory.Services.GetRequiredService<ILogger<DevWorkflowDispatcher>>());
    }

    /// <summary>
    ///     Ticks the run and then waits for whatever the tick handed to the supervisor to settle, until the run reaches
    ///     the state the caller is waiting for.
    ///     <para>
    ///         The wait is real rather than scripted, because the session loop genuinely is asynchronous — but it waits
    ///         on the SESSION's own status rather than on a duration, so nothing here sleeps and hopes.
    ///     </para>
    /// </summary>
    private static async Task DriveUntilAsync(TestServerWebAppFactory factory,
        DevWorkflowDispatcher dispatcher,
        Guid runId,
        Func<DevWorkflowRunSnapshot, bool> settled,
        int maxTicks = 60)
    {
        for (var tick = 0; tick < maxTicks; tick++)
        {
            _ = await dispatcher.AdvanceOnceAsync(runId, CancellationToken.None).ConfigureAwait(false);

            // Waited BEFORE the condition is judged, so a test never reads a run in the middle of a turn the tick it
            // just started — which is the one state an assertion here could see and a real reader could not act on.
            await WaitForSessionsToSettleAsync(factory).ConfigureAwait(false);
            if (settled(ReadRun(factory, runId)))
            {
                return;
            }
        }

        throw new AssertionException($"Run {runId} was {ReadRun(factory, runId).Status} after {maxTicks} ticks.");
    }

    private static async Task WaitForSessionsToSettleAsync(TestServerWebAppFactory factory)
    {
        var deadline = DateTimeOffset.UtcNow + SessionGrace;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var sessions = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().ListAsync().ConfigureAwait(false);
            if (!sessions.Any(static session => session is { Kind: AgentWorkSessionKind.Workflow, Status: AgentWorkSessionStatus.Running }))
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new AssertionException("A workflow work session was still running after the grace period.");
    }

    /// <summary>What a turn's tools would have written: one artifact, then the request to complete the session.</summary>
    private static async Task FinishTheWorkflowSessionAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        var session = (await store.ListAsync().ConfigureAwait(false))
            .Single(static candidate => candidate is { Kind: AgentWorkSessionKind.Workflow, Status: AgentWorkSessionStatus.Running });

        var artifactId = Guid.NewGuid();
        var written = await scope.ServiceProvider.GetRequiredService<IWorkSessionArtifactBlobStore>()
                                 .WriteAsync(session.Id, artifactId, Encoding.UTF8.GetBytes("# What this step found"))
                                 .ConfigureAwait(false);
        _ = await store.AppendArtifactAsync(new AppendWorkSessionArtifactCommand(session.Id,
                           artifactId,
                           WorkSessionVersions.Any,
                           Guid.NewGuid(),
                           AgentWorkSessionArtifactKind.Report,
                           "notes.md",
                           "text/markdown",
                           written.ContentHash,
                           written.ByteCount,
                           written.OpaqueReference))
                       .ConfigureAwait(false);

        _ = await store.AppendEventAsync(new AppendWorkSessionEventCommand(session.Id,
                           WorkSessionVersions.Any,
                           WorkSessionEventTypes.CompletionRequested,
                           Guid.NewGuid(),
                           Outcome: null,
                           JsonSerializer.Serialize(new
                           {
                               summary = "This step is done."
                           })))
                       .ConfigureAwait(false);
    }

    /// <summary>
    ///     One step per session run, so every agent node parks once and is resumed by its node run. That is the
    ///     ordinary shape of a workflow agent node — a node routinely needs more steps than one run allows — and it is
    ///     also what puts the run in a genuinely in-flight state for the restart.
    /// </summary>
    private static TestServerWebAppFactory NewFactory(Action<IServiceCollection>? configureExtra = null) =>
        WorkSessionServiceTests.NewFactory(configureExtra,
            ("WorkSessions:MaxStepsPerRun", "1"),
            ("DevWorkflows:Enabled", "true"),
            ("DevWorkflows:SweepSeconds", "3600"));

    private static FakeNodeChatStreamService ResolveStream(TestServerWebAppFactory factory, ref FakeNodeChatStreamService? stream)
    {
        // Forces the singleton factory to run: nothing has sent a turn yet, so the field is still null.
        _ = factory.Services.GetRequiredService<INodeChatStreamService>();
        return AssertEx.NotNull(stream, "the fake stream service must be resolved before the loop takes a step.");
    }

    private static async Task<Guid> FindSeededDefinitionAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var definitions = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListDefinitionsAsync().ConfigureAwait(false);
        return definitions.Single(static definition => definition.SeedSlug == "research-plan-approval").Id;
    }

    private static async Task<Guid> CreateWorkItemAsync(TestServerWebAppFactory factory, string request)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var workItem = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                                  .CreateWorkItemAsync(new CreateDevWorkflowWorkItemCommand(Guid.NewGuid(), "Understand the inference path", request))
                                  .ConfigureAwait(false);
        return workItem.Id;
    }

    private static DevWorkflowRunSnapshot ReadRun(TestServerWebAppFactory factory, Guid runId)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().GetRunAsync(runId).GetAwaiter().GetResult();
    }

    private static DevWorkflowNodeRunSnapshot ReadNodeRun(TestServerWebAppFactory factory, Guid runId, string nodeKey)
    {
        using var scope = factory.Services.CreateScope();
        var nodeRuns = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListNodeRunsAsync(runId).GetAwaiter().GetResult();
        return nodeRuns.Single(nodeRun => string.Equals(nodeRun.NodeKey, nodeKey, StringComparison.Ordinal));
    }

    private static AgentWorkSessionSnapshot ReadSession(TestServerWebAppFactory factory, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().GetAsync(sessionId).GetAwaiter().GetResult();
    }

    private static async Task<IReadOnlyList<Guid>> ReadConsumedArtifactIdsAsync(TestServerWebAppFactory factory, Guid nodeRunId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListConsumedArtifactIdsAsync(nodeRunId).ConfigureAwait(false);
    }

    private static async Task<DevWorkflowWorkItemStatus> ReadWorkItemStatusAsync(TestServerWebAppFactory factory, Guid workItemId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().GetWorkItemAsync(workItemId).ConfigureAwait(false)).Status;
    }

    private static async Task<IReadOnlyList<DevWorkflowRunEventSnapshot>> ReadEventsAsync(TestServerWebAppFactory factory, Guid runId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().ListEventsAsync(runId, sinceSequence: 0, limit: 500).ConfigureAwait(false);
    }
}
