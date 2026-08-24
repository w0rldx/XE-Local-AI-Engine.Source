namespace XE_Local_AI_Engine.Tests.WorkSessions;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The supervisor's step loop against a scripted stream service — its real dependency, since a fake chat client
///     would exercise the framework's tool pipeline rather than anything this loop decides.
/// </summary>
public sealed class WorkSessionStepLoopTests
{
    [Test]
    public async Task Loop_WhenTheAgentCompletesTheSession_RunsItsStepsAndLandsCompleted()
    {
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantDelta, ChatStreamEventTypes.AssistantCompleted]));
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantCompleted], DeclareCompleteAsync));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        var settled = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Completed).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, settled.StepCount, "Both scripted turns count as steps.");
        AssertEx.Equal(expected: 2, fake.Requests.Count);
        AssertEx.Contains(fake.Requests[0].Content, "[work session state", message: "Every step sends the rebuilt state block.");
        AssertEx.True(fake.Requests[0].UseLocalTools, "A work session step must offer the local tools or the state tools are unreachable.");

        var events = await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false);
        AssertEx.Contains(events, entry => entry.EventType == "SessionStatusChanged" && entry.Outcome == nameof(AgentWorkSessionStatus.Completed));
        AssertEx.Contains(await WorkSessionTestSupport.ReadCheckpointsAsync(factory.Services, sessionId).ConfigureAwait(false),
            checkpoint => checkpoint.Step == 2,
            "Completing a session checkpoints it first, so the final state is recoverable.");
    }

    [Test]
    public async Task Loop_PublishesTheStepBeforeItSends_SoAClientCanAttachToTheLiveTurn()
    {
        // Ordering, not merely occurrence: by the time a step terminalizes, the invocation resume registry has dropped
        // its entry, so a client told about the step only afterwards re-attaches to an empty stream.
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        // The publisher and the stream both record into their own lists; the event rows give the shared order.
        var events = await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false);
        var stepStarted = AssertEx.NotNull(events.FirstOrDefault(entry => entry.EventType == "StepStarted"), "The loop records a StepStarted before it sends.");
        AssertEx.Contains(publisher.Published, published => published.Sequence == stepStarted.Sequence && published.Kind == WorkSessionChangeKind.Step);
        AssertEx.Equal(expected: 1, fake.Requests.Count);
    }

    [Test]
    public async Task Loop_WhenTheStepBudgetIsReached_CheckpointsAndPauses()
    {
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "2")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        var settled = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, settled.StepCount, "The budget bounds the run, and both steps still count.");
        AssertEx.Equal(expected: 2, fake.Requests.Count, "A paused run must not send a third turn.");
        AssertEx.NotEmpty(await WorkSessionTestSupport.ReadCheckpointsAsync(factory.Services, sessionId).ConfigureAwait(false));
    }

    [Test]
    public async Task Loop_WhenAStepFails_CheckpointsAndLandsFailed()
    {
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantFailed]));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        var settled = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Failed).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, settled.StepCount, "A failed turn is not a completed step.");
        AssertEx.Contains(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false),
            entry => entry.EventType == WorkSessionEventTypes.StepFailed);
    }

    [Test]
    public async Task Loop_WhenAParkOutlivesMaxParkedSeconds_CheckpointsPausesAndRecordsTheOpenQuestion()
    {
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            // One second, so the park's own clock — not the test's patience — is what ends the step.
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxParkedSeconds", "1")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([], Park: true));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        var settled = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, settled.StepCount);

        var events = await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false);
        AssertEx.Contains(events,
            entry => entry.EventType == "SessionStatusChanged" && entry.Outcome == nameof(AgentWorkSessionStatus.WaitingForApproval),
            "An approval request has to show as WaitingForApproval while it is pending.");
        AssertEx.Contains(events, entry => entry.EventType == WorkSessionEventTypes.ParkTimedOut);

        // The order is load-bearing: a crash between the two reconciles to Interrupted off a VALID checkpoint, where a
        // status-first write would resume from a stale state block.
        var checkpoint = AssertEx.NotNull(events.FirstOrDefault(entry => entry.EventType == "CheckpointRecorded"), "The park timeout checkpoints before it pauses.");
        var paused = AssertEx.NotNull(events.FirstOrDefault(entry => entry.EventType == "SessionStatusChanged" && entry.Outcome == nameof(AgentWorkSessionStatus.Paused)),
            "The park timeout pauses the session.");
        AssertEx.True(checkpoint.Sequence < paused.Sequence, "The checkpoint must commit before the Paused status.");

        var findings = await WorkSessionTestSupport.ReadFindingsAsync(factory.Services, sessionId).ConfigureAwait(false);
        AssertEx.ContainsSingle(findings,
            finding => finding.Kind == AgentWorkSessionFindingKind.OpenQuestion,
            "The unanswered prompt is recorded as an open question so the next step re-asks it; the park itself does not survive.");
    }

    [Test]
    public async Task Loop_WhenAParkedTurnIsAnswered_MovesWaitingForApprovalBackToRunning()
    {
        // The other half of the park: the prompt is answered, the turn carries on, and the session must leave
        // WaitingForApproval instead of sitting in it for the rest of the run. The park clock is disarmed on the way
        // through, so the long MaxParkedSeconds here proves the resume happened rather than the timeout.
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1"), ("WorkSessions:MaxParkedSeconds", "3600")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantDelta, ChatStreamEventTypes.AssistantCompleted], ParkThenContinue: true));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        var settled = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, settled.StepCount, "An answered park still finishes its step.");

        var events = await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false);
        var waiting = AssertEx.NotNull(
            events.FirstOrDefault(entry => entry.EventType == "SessionStatusChanged" && entry.Outcome == nameof(AgentWorkSessionStatus.WaitingForApproval)),
            "The approval request has to show as WaitingForApproval while it is pending.");
        var running = AssertEx.NotNull(
            events.FirstOrDefault(entry => entry.EventType == "SessionStatusChanged"
                                           && entry.Outcome == nameof(AgentWorkSessionStatus.Running)
                                           && entry.Sequence > waiting.Sequence),
            "The next delta must move the session back to Running.");
        AssertEx.True(running.Sequence > waiting.Sequence);

        AssertEx.Empty(await WorkSessionTestSupport.ReadFindingsAsync(factory.Services, sessionId).ConfigureAwait(false),
            "An answered park records no open question — that finding exists only because a timeout loses the prompt.");
        AssertEx.False(events.Any(entry => entry.EventType == WorkSessionEventTypes.ParkTimedOut), "The park clock was disarmed, not fired.");
    }

    [Test]
    public async Task Loop_WhenTheSessionIsCancelledMidStep_LandsCancelledWithoutACheckpoint()
    {
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxParkedSeconds", "3600")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([], Park: true));

        var supervisor = factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>();
        AssertEx.True(supervisor.TryStart(sessionId));
        await AssertEx.EventuallyAsync(() => fake.Requests.Count == 1, TimeSpan.FromSeconds(15), "The step has to be in flight before it can be cancelled.")
                      .ConfigureAwait(false);

        AssertEx.True(await supervisor.TryStopAsync(sessionId, WorkSessionStopReason.Cancel).ConfigureAwait(false));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Cancelled).ConfigureAwait(false);

        AssertEx.Empty(await WorkSessionTestSupport.ReadCheckpointsAsync(factory.Services, sessionId).ConfigureAwait(false),
            "A cancelled session is not going to be resumed, so it is not checkpointed.");
    }

    [Test]
    public async Task Supervisor_WhenASessionIsAlreadyInFlight_RefusesTheSecondStart()
    {
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxParkedSeconds", "3600")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([], Park: true));

        var supervisor = factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>();
        AssertEx.True(supervisor.TryStart(sessionId));
        AssertEx.False(supervisor.TryStart(sessionId), "One session is driven once; a second driver would double every write.");
        AssertEx.False(supervisor.HasCapacity, "The default admission cap is one session, and it is taken.");

        _ = await supervisor.TryStopAsync(sessionId, WorkSessionStopReason.Cancel).ConfigureAwait(false);
    }

    [Test]
    public async Task Loop_WhenAToolWritesWhileTheSupervisorMovesTheStatus_DoesNotThrow()
    {
        // Two writers by design: the supervisor moves the status while the turn's tools write from inside the
        // invocation loop. The supervisor's status writes pass the sentinel version precisely so they never lose that
        // race — a concurrency exception here would end the run.
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantCompleted], RecordFindingsDuringTheTurnAsync));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        AssertEx.Equal(expected: 3, (await WorkSessionTestSupport.ReadFindingsAsync(factory.Services, sessionId).ConfigureAwait(false)).Count);
    }

    private static FakeNodeChatStreamService ResolveStream(TestServerWebAppFactory factory, ref FakeNodeChatStreamService? stream)
    {
        // Resolving the singleton is what runs the factory delegate that assigns the field.
        _ = factory.Services.GetRequiredService<INodeChatStreamService>();
        return AssertEx.NotNull(stream, "The fake stream service must have been resolved.");
    }

    private static async Task DeclareCompleteAsync(IServiceProvider services, Guid sessionId)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        _ = await store.AppendEventAsync(new AppendWorkSessionEventCommand(sessionId,
                    WorkSessionVersions.Any,
                    WorkSessionEventTypes.CompletionRequested,
                    Guid.NewGuid(),
                    Outcome: null,
                    JsonSerializer.Serialize(new
                    {
                        summary = "Every task is done and the findings tell the whole story."
                    })))
            .ConfigureAwait(false);
    }

    private static async Task RecordFindingsDuringTheTurnAsync(IServiceProvider services, Guid sessionId)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        for (var index = 0; index < 3; index++)
        {
            var session = await store.GetAsync(sessionId).ConfigureAwait(false);
            _ = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                        Guid.NewGuid(),
                        session.Version,
                        Guid.NewGuid(),
                        AgentWorkSessionFindingKind.Finding,
                        $"Finding {index}."))
                .ConfigureAwait(false);
        }
    }
}
