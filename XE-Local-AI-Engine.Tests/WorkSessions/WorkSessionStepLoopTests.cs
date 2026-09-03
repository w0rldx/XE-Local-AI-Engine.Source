namespace XE_Local_AI_Engine.Tests.WorkSessions;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The supervisor's step loop against a scripted stream service — its real dependency, since a fake chat client
///     would exercise the framework's tool pipeline rather than anything this loop decides.
/// </summary>
public sealed class WorkSessionStepLoopTests
{
    /// <summary>Web defaults, matching the camelCase convention the supervisor writes the consumption record in.</summary>
    private static readonly JsonSerializerOptions ConsumptionJsonOptions = new(JsonSerializerDefaults.Web);

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
        // through, so the long MaxParkedSeconds here proves the resume happened rather than the timeout. 599 rather
        // than an arbitrarily huge number: WorkSessionOptionsValidator refuses a park budget that reaches the node's
        // 10-minute pending tool-call age.
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1"), ("WorkSessions:MaxParkedSeconds", "599")),
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
        var waiting = AssertEx.NotNull(events.FirstOrDefault(entry => entry.EventType == "SessionStatusChanged" && entry.Outcome == nameof(AgentWorkSessionStatus.WaitingForApproval)),
            "The approval request has to show as WaitingForApproval while it is pending.");
        var running = AssertEx.NotNull(events.FirstOrDefault(entry => entry.EventType == "SessionStatusChanged"
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
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxParkedSeconds", "599")),
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
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxParkedSeconds", "599")),
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
    public async Task Supervisor_WhenMoreSessionsStartAtOnceThanTheCapAllows_AdmitsExactlyTheCap()
    {
        // Distinct session ids, so the ConcurrentDictionary never refuses the add: the cap is the only thing that can
        // turn a start down. A cap checked AFTER the add let two starts each see room and then each back out, admitting
        // fewer than the cap allows — the gate has to be taken before the run is registered.
        var sessionIds = Enumerable.Range(start: 0, count: 4).Select(_ => Guid.NewGuid()).ToArray();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxConcurrentSessions", "3"),
                ("WorkSessions:MaxParkedSeconds", "599")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionIds[0]),
                publisher)
        };

        foreach (var sessionId in sessionIds)
        {
            _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        }

        var fake = ResolveStream(factory, ref stream);
        // Every admitted run parks and holds its slot until it is cancelled, so the count below is not a race with a
        // run that already finished.
        foreach (var _ in sessionIds)
        {
            fake.Enqueue(new StepScript([], Park: true));
        }

        var supervisor = factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>();
        var admitted = await Task.WhenAll(sessionIds.Select(sessionId => Task.Run(() => supervisor.TryStart(sessionId)))).ConfigureAwait(false);

        AssertEx.Equal(expected: 3, admitted.Count(started => started), "The cap is three, and four starts raced for it.");
        AssertEx.False(supervisor.HasCapacity, "Every slot is taken while the admitted sessions are in flight.");

        foreach (var sessionId in sessionIds)
        {
            _ = await supervisor.TryStopAsync(sessionId, WorkSessionStopReason.Cancel).ConfigureAwait(false);
        }

        AssertEx.True(supervisor.HasCapacity, "A stopped run hands its slot back, or the node admits nothing ever again.");
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

    [Test]
    public async Task Loop_WhenTheSessionsModelHasLeftTheToolCapableList_PausesTheStepInsteadOfSendingIt_AndResumeWorksOnceItIsBack()
    {
        // The allow-list is read LIVE on every offer, so an operator edit lands mid-run and the create-time refusal
        // cannot cover it. Without this guard the step still goes out — with the four state tools missing from the
        // offer — and the session spends its whole budget on "Requested function update_work_plan not found".
        //
        // PAUSED rather than Failed is the load-bearing half: the refusal tells the operator to list the model, and
        // Resume accepts only Paused/Interrupted, so a Failed session could not be restarted after they did it. The
        // second act of this test is exactly that round trip.
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            // One step per run, so the resumed run sends its turn and pauses on the budget instead of looping to 25.
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        var agentId = await WorkSessionServiceTests.SeedAgentAsync(factory, "tool-capable-model").ConfigureAwait(false);
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId, agentDefinitionId: agentId).ConfigureAwait(false);
        await SetAllowListAsync(factory.Services, "some-other-model").ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        var refused = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, refused.StepCount, "A step that never ran must not be charged to the budget.");
        AssertEx.Empty(fake.Requests, "The turn must not be sent at all — a sent one would come back tool-less and look like a model failure.");

        var events = await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false);
        AssertEx.Contains(events,
            entry => entry.EventType == WorkSessionEventTypes.StepEnded && entry.Outcome == "ToolGate",
            "The row names the gate that stopped the step; a StepFailed row on a paused session would read as a contradiction.");
        AssertEx.False(events.Any(entry => entry.EventType == WorkSessionEventTypes.StepFailed), "Nothing failed here.");
        AssertEx.Contains(events,
            entry => entry.EventType == "SessionStatusChanged"
                     && entry.Outcome == nameof(AgentWorkSessionStatus.Paused)
                     && entry.DetailJson?.Contains("tool-capable model list", StringComparison.Ordinal) == true,
            "The reason has to name the list the operator must edit.");

        // The operator does what the refusal asked, then presses Resume.
        await SetAllowListAsync(factory.Services, "tool-capable-model").ConfigureAwait(false);
        await ResumeWhenTheNodeCanAdmitAsync(factory.Services, sessionId).ConfigureAwait(false);
        var resumed = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, resumed.StepCount, "The resumed run takes the step the gate had stopped.");
        AssertEx.Equal(expected: 1, fake.Requests.Count, "And this time the turn is actually sent.");
        AssertEx.Contains(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false),
            entry => entry.EventType == WorkSessionEventTypes.StepStarted);
    }

    /// <summary>
    ///     What an operator does in Node Settings: the stored allow-list replaces the seeded one outright, so the
    ///     session's model is either on it or it is not.
    /// </summary>
    private static async Task SetAllowListAsync(IServiceProvider services, params string[] models)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INodeSettingsStore>();
        var stored = await store.LoadAsync().ConfigureAwait(false);
        await store.SaveAsync(stored with
                   {
                       ToolCapableModels = models
                   })
                   .ConfigureAwait(false);
    }

    /// <summary>
    ///     Resumes through the REST service, retrying the admission race. The settled run releases the node's one slot
    ///     in its <c>finally</c>, which lands AFTER the status row it settled — so a resume issued the instant the test
    ///     sees <c>Paused</c> can legitimately lose the race and be refused. That is real behaviour, not a defect, and
    ///     an operator clicking Resume hits the same window; retrying is what makes the assertion about the GATE rather
    ///     than about timing.
    /// </summary>
    private static async Task ResumeWhenTheNodeCanAdmitAsync(IServiceProvider services, Guid sessionId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            await using var scope = services.CreateAsyncScope();
            try
            {
                _ = await scope.ServiceProvider.GetRequiredService<IWorkSessionService>().ResumeAsync(sessionId).ConfigureAwait(false);
                return;
            }
            catch (WorkSessionInvalidTransitionException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
    }

    [Test]
    public async Task Loop_WhenAStepCompletes_RecordsWhatItSpentOnTheStepEndedRow()
    {
        // The measurement the per-step provider-call cap is meant to be sized from. It has to land on the ORDINARY
        // step too: a record written only when the cap trips would always read "10/10" and would measure the bound
        // rather than the work.
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1"), ("WorkSessions:MaxProviderCallsPerStep", "6")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantCompleted], (_, _) => SpendProviderCallsAsync()));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        var events = await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false);
        var ended = AssertEx.NotNull(events.FirstOrDefault(entry => entry.EventType == WorkSessionEventTypes.StepEnded),
            "Every step that ends without a fault records what it spent.");
        AssertEx.Equal("Completed", ended.Outcome, "An ordinary step's outcome is not the name of a bound.");

        var consumption = ReadConsumption(ended.DetailJson);
        AssertEx.Equal(expected: 3, consumption.ProviderCalls);
        AssertEx.Equal(expected: 4_500L, consumption.EstimatedInputTokens);
        AssertEx.Equal(expected: 2, consumption.ToolCallsCompleted);
        AssertEx.Equal(expected: 6, consumption.ProviderCallCap, "The cap the step was seeded with rides along, so the calls can be sized against it.");
        AssertEx.Equal(expected: 1, consumption.AttachedBudgets, "One invocation ran, which is what makes the calls a ratio against the cap.");
    }

    [Test]
    public async Task Loop_WhenAStepFails_RecordsWhatItSpentOnTheStepFailedRow()
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
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantFailed], (_, _) => SpendProviderCallsAsync()));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Failed).ConfigureAwait(false);

        var events = await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false);
        var failed = AssertEx.NotNull(events.FirstOrDefault(entry => entry.EventType == WorkSessionEventTypes.StepFailed));
        var consumption = ReadConsumption(failed.DetailJson);
        AssertEx.Equal(expected: 3, consumption.ProviderCalls, "A step that broke still spent what it spent, and that is the interesting part.");
        AssertEx.Equal(expected: 2, consumption.ToolCallsCompleted);
        AssertEx.Empty(events.Where(entry => entry.EventType == WorkSessionEventTypes.StepEnded).ToList(),
            "A failed step records on its StepFailed row; it does not also claim to have ended cleanly.");
    }

    [Test]
    public async Task Loop_WhenAStepMakesNoProviderRound_LeavesTheDetailEmpty()
    {
        // An empty record would read as "this step was free". Nothing ran under the cap scope, so the row says nothing.
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
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantCompleted]));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        var events = await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false);
        var ended = AssertEx.NotNull(events.FirstOrDefault(entry => entry.EventType == WorkSessionEventTypes.StepEnded));
        AssertEx.Null(ended.DetailJson, "No budget was created under the step's cap scope, so there is nothing to report.");
    }

    private static WorkSessionStepConsumptionDetail ReadConsumption(string? detailJson)
    {
        var detail = AssertEx.NotNull(detailJson, "The step's terminal row carries its consumption record.");
        return AssertEx.NotNull(JsonSerializer.Deserialize<WorkSessionStepConsumptionDetail>(detail, ConsumptionJsonOptions),
            "The record is camelCase JSON, the same convention every other session payload uses.");
    }

    /// <summary>
    ///     Stands in for the invocation the send path would have run: it seeds the runner's own budget scope inside the
    ///     turn's async flow, exactly where the real runner seeds it, and registers rounds and tool calls against it.
    ///     That the supervisor can read those counters afterwards is the whole point — its own
    ///     <c>ProviderCallBudget.Current</c> is null again by then.
    /// </summary>
    private static Task SpendProviderCallsAsync()
    {
        using var scope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions());
        var budget = ProviderCallBudget.Current!;
        budget.RegisterProviderRound(estimatedInputTokens: 1_000);
        budget.RecordToolCallCompleted(TimeSpan.FromMilliseconds(5), resultBytes: 128, failed: false);
        budget.RegisterProviderRound(estimatedInputTokens: 1_500);
        budget.RecordToolCallCompleted(TimeSpan.FromMilliseconds(5), resultBytes: 128, failed: false);
        budget.RegisterProviderRound(estimatedInputTokens: 2_000);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     FU-5: the pins the caller admitted the session with are what every one of its turns is sent with, and the
    ///     effort travels flagged as a pin — without that flag a bound agent's own pinned effort would win over it.
    /// </summary>
    [Test]
    public async Task Loop_WhenTheCallerPinnedAModelAndEffort_SendsThemOnEveryStep()
    {
        var sessionId = Guid.NewGuid();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                new RecordingWorkSessionEventPublisher())
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantCompleted]));
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantCompleted], DeclareCompleteAsync));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>()
                             .TryStart(sessionId, new WorkSessionRuntimeOverride("qwen3-30b", "high")));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Completed).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, fake.Requests.Count);
        foreach (var request in fake.Requests)
        {
            AssertEx.Equal("qwen3-30b", request.Model);
            AssertEx.Equal("high", request.ReasoningEffort);
            AssertEx.True(request.ReasoningEffortOverridesAgentPin, "the caller's effort is a pin, so it must beat the bound agent's own.");
            AssertEx.True(request.IsWorkSessionTurn, "every supervised step is a work-session turn, pinned or not.");
        }
    }

    [Test]
    public async Task Loop_WithNoPin_SendsTheTurnExactlyAsItDoesToday()
    {
        var sessionId = Guid.NewGuid();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                new RecordingWorkSessionEventPublisher())
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantCompleted], DeclareCompleteAsync));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Completed).ConfigureAwait(false);

        AssertEx.Null(fake.Requests[0].Model, "an unpinned session resolves its model the way every other send does.");
        AssertEx.Null(fake.Requests[0].ReasoningEffort);
        AssertEx.False(fake.Requests[0].ReasoningEffortOverridesAgentPin);
        AssertEx.True(fake.Requests[0].IsWorkSessionTurn,
            "an unpinned session is still a supervised step, so the adaptive-effort swap must stay refused on it.");
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
