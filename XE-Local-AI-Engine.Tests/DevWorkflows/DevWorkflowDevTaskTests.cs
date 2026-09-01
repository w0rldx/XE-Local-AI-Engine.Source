namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The implementation lane: a <c>DevTask</c> node run driving a real Development task through the chain that
///     already exists, and what happens to the node run when that chain fails, is cancelled, or is not there at all.
///     <para>
///         Every test takes a host of its OWN: each scripts the development chain, whose stand-in is a container
///         singleton, and each seeds its own development project.
///     </para>
/// </summary>
public sealed class DevWorkflowDevTaskTests
{
    /// <summary>
    ///     The Slice B shape end to end: plan it, implement it, validate the result, and put it in front of a human.
    ///     <para>
    ///         The implementation node's success is the task reaching <c>AwaitingApply</c> — apply itself is a later act
    ///         behind the gate this graph ends on, which is the whole point of routing it through the workflow.
    ///     </para>
    /// </summary>
    private const string PlanImplementValidateReview = """
                                                       {
                                                         "schemaVersion": 1,
                                                         "nodes": [
                                                           { "nodeKey": "plan", "nodeType": "Agent", "label": "Plan",
                                                             "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                           { "nodeKey": "implement", "nodeType": "DevTask", "label": "Implement" },
                                                           { "nodeKey": "validate", "nodeType": "Tool", "label": "Validate", "retryTarget": "implement" },
                                                           { "nodeKey": "review", "nodeType": "HumanGate", "label": "Human review" }
                                                         ],
                                                         "edges": [
                                                           { "from": "plan", "to": "implement" },
                                                           { "from": "implement", "to": "validate" },
                                                           { "from": "validate", "to": "review" }
                                                         ]
                                                       }
                                                       """;

    /// <summary>One implementation node on a five-second budget, with nothing left to try after it.</summary>
    private const string ImpatientDevTask = """
                                            {
                                              "schemaVersion": 1,
                                              "nodes": [{ "nodeKey": "implement", "nodeType": "DevTask", "label": "Implement", "maxAttempts": 1,
                                                          "nodeTimeoutSeconds": 5 }],
                                              "edges": []
                                            }
                                            """;

    /// <summary>One implementation node with one re-attempt in hand.</summary>
    private const string SingleDevTask = """
                                         {
                                           "schemaVersion": 1,
                                           "nodes": [{ "nodeKey": "implement", "nodeType": "DevTask", "label": "Implement", "maxAttempts": 2 }],
                                           "edges": []
                                         }
                                         """;

    /// <summary>
    ///     The Slice B6 gate: "Plan → Implement → Validate → Human review", with the implementation node driving a real
    ///     development task and the validation node's report in the run's own artifacts.
    /// </summary>
    [Test]
    public async Task ThePlanImplementValidateReviewChainRunsEndToEnd()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        var runId = await harness.StartRunAsync(PlanImplementValidateReview, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "plan", "plan.md", "# Plan\n\nChange the thing.").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "plan").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));
        AssertEx.Equal(taskId, implemented.DevelopmentTaskId, "the node run names the task it drove, which is how a reader drills into the evidence.");
        AssertEx.Contains(AssertEx.NotNull(implemented.OutputJson), "\"taskStatus\":\"AwaitingApply\"");
        AssertEx.Equal("Planned, Ready, InProgress, Validation, InReview",
            string.Join(", ", harness.Chain.Actions),
            "the node run drove the existing chain one stage at a time rather than forcing a status.");

        var task = await ReadTaskAsync(harness, taskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, task.Status, "and the task is left waiting to be applied: this node applies nothing.");

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false)).Status);
        AssertEx.Contains(await harness.ReadArtifactsAsync(runId).ConfigureAwait(false),
            artifact => artifact.Kind == DevWorkflowArtifactKind.ValidationReport,
            "the validation node's report is in the run's own artifacts, next to the plan the agent wrote.");

        var review = await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, review.Status);
        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        await harness.DecideAsync(runId, "review", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply,
            (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status,
            "approving the workflow's own gate is not applying the patch — that is a later act, behind its own gate.");
    }

    /// <summary>
    ///     A re-attempt KEEPS the task the previous one drove, rather than clearing the pointer.
    ///     <para>
    ///         The pointer names the task this node implements for the life of the run: clearing it would take away the
    ///         operator's link to the work while it is being retried, and — now that a project can carry several tasks —
    ///         would let the re-attempt bind to a sibling's. The plan's own §7.2 text says the pointer is cleared and a
    ///         new task created; the evidence on the task it already drove is the stronger fact.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AReAttemptKeepsThePointerToTheTaskItIsStillImplementing()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.FailNextAttempts(1);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, implemented.Attempt, "the failed development attempt cost the node run one of its own.");
        AssertEx.Equal(taskId, implemented.DevelopmentTaskId, "the pointer survived the re-attempt: the task it names holds the evidence of the attempt that failed.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status);

        var retry = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Single(static entry => entry.EventType == "node.retry.scheduled");
        AssertEx.Contains(AssertEx.NotNull(retry.DetailJson), "\"failureClass\":\"ProviderError\"");
    }

    /// <summary>
    ///     A development attempt that fails is the retry policy's to answer, exactly as a failed work session is: the
    ///     node re-attempts while it has attempts, and stands down for a human when it does not.
    /// </summary>
    [Test]
    public async Task ADevelopmentAttemptThatKeepsFailingStandsTheNodeRunDownForAHuman()
    {
        await using var harness = NewHarness();
        var (projectId, _) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.FailNextAttempts(5);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Equal(expected: 2, blocked.Attempt, "the node allowed two attempts, and both were spent on a real development attempt.");
        AssertEx.Equal(DevWorkflowFailureClasses.ProviderError, blocked.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(blocked.TerminalReason), "The scripted coder attempt failed.");
        AssertEx.Contains(AssertEx.NotNull(blocked.TerminalReason), "as many attempts as this node allows");
        AssertEx.Equal(DevWorkflowDecisionKind.Abandon, blocked.PendingDecisionKind);
        AssertEx.Equal(expected: 2, harness.Chain.Actions.Count, "each node-run attempt asked the chain for its own action rather than re-reading the last one's failure.");
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     L5: a workspace policy refusing the attempt's own diff is not the provider failing. Classed as
    ///     <c>ProviderError</c> it was retried until the budget ran out — live, four attempts and about ten minutes of
    ///     real model time — and the operator was then handed a generic sentence naming no rule. Classed as a policy
    ///     refusal it goes to a human on the first answer, carrying the sentence that says what to change.
    /// </summary>
    [Test]
    public async Task AWorkspacePolicyRefusalStandsTheNodeDownForAHumanInsteadOfSpendingTheRetryBudget()
    {
        await using var harness = NewHarness();
        var (projectId, _) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.RefuseNextAttemptsOnPolicy(5);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.Policy, blocked.FailureClass);
        AssertEx.Equal(expected: 1, blocked.Attempt, "a refusal on evidence answers the same way every time, so the second attempt is not spent finding that out.");
        AssertEx.Contains(AssertEx.NotNull(blocked.TerminalReason),
            "test that existed at the base commit",
            message: "the policy's own sentence is what tells the operator which rule was broken.");
    }

    /// <summary>
    ///     N2: a task inside its own deterministic-validation window is WORKING, not misconfigured.
    ///     <para>
    ///         Dev Mode drives that phase from its own supervisor and holds no attempt row while it does, so a tick that
    ///         lands inside it is told the task has no executable next action — which is true, and which the lane read
    ///         as a configuration fault. Live, that stood two children down 24 ms after validation started, each with a
    ///         SUCCEEDED coder attempt on it, and every operator Retry that recovered one spent an attempt out of the
    ///         very budget the fix loop needs.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ATaskInsideItsOwnValidationWindowIsWaitedOnRatherThanStoodDown()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.StallInValidation(1);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var waiting = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, waiting.Status, $"the node was left on {waiting.Status}: {waiting.TerminalReason}");
        AssertEx.Null(waiting.FailureClass, "nothing failed — Dev Mode is one status ahead of this tick, and will move the task on itself.");
        AssertEx.Equal(expected: 1, waiting.Attempt, "and the wait cost the node none of its budget.");
        AssertEx.Equal(DevelopmentTaskStatus.Validation, (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply,
            (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status,
            "the window closed and the chain carried on from where it was, without an operator being asked for anything.");
    }

    /// <summary>
    ///     The same race one hop later, which naming a single status would lose: the supervisor FINISHES between the ask
    ///     and the guard's re-read, so the task is no longer in Validation by the time anyone looks — it is in InReview,
    ///     with a bumped version and still no attempt row. A guard that matched only <c>Validation</c> would stand a
    ///     healthy task down here. A task that MOVED since the snapshot this tick opened with is working, whatever it
    ///     moved to.
    /// </summary>
    [Test]
    public async Task ATaskThatMovedOnBetweenTheAskAndTheReReadIsWaitedOnRatherThanStoodDown()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.StallInValidation(1, advance: true);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var waiting = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, waiting.Status, $"the node was left on {waiting.Status}: {waiting.TerminalReason}");
        AssertEx.Null(waiting.FailureClass, "the task is one status FURTHER on than the window, and nothing about that is a fault.");
        AssertEx.Equal(DevelopmentTaskStatus.InReview,
            (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status,
            "the supervisor had already moved it past Validation by the time the guard looked.");

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     Cancelling a run stops the development attempt rather than abandoning it, and the row settles on what the
    ///     attempt actually did.
    /// </summary>
    [Test]
    public async Task CancellingARunStopsTheDevelopmentAttemptItWasDriving()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.HoldNextAttempt();
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Status,
            "the chain is working, so the node run waits rather than asking it for more.");

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Cancelling).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, harness.Chain.CancelledAttempts.Count, "the development chain owns what stopping its work costs.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Cancelled, (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress,
            (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status,
            "the task itself is left where it stood: a cancelled run abandons its own node run, not the operator's task.");
    }

    /// <summary>
    ///     A pause lets the attempt in flight finish and parks the node run, so the resume re-drives the same task from
    ///     wherever the chain left it.
    /// </summary>
    [Test]
    public async Task PausingARunLeavesTheDevelopmentAttemptToFinishAndParksTheNodeRun()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.HoldNextAttempt();
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Pausing).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Empty(harness.Chain.CancelledAttempts, "a pause is meant to be resumed, so the attempt is left to finish rather than thrown away.");
        AssertEx.Equal(DevWorkflowRunStatus.Pausing,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "and the run says so honestly until the attempt has landed.");

        // The attempt lands, and the pause can then complete.
        await LandTheHeldAttemptAsync(harness, taskId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var parked = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, parked.Status, "a paused implementation node waits rather than terminalizing.");
        AssertEx.Equal(taskId, parked.DevelopmentTaskId, "it keeps the task, which is what makes the resume a continuation.");
        AssertEx.Equal(DevWorkflowRunStatus.Paused, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Running).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A node with nothing to drive says so: Development Mode switched off is a configuration answer for a human,
    ///     not a failure to retry and not a container error inside a tick.
    /// </summary>
    [Test]
    public async Task ADevTaskNodeOnANodeWithDevelopmentModeOffStandsDown()
    {
        await using var harness = new DevWorkflowHarness(("Development:Enabled", "false"));
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", Guid.NewGuid()).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.Configuration, blocked.FailureClass, "no retry answers a node that has nothing to run on.");
        AssertEx.Equal(expected: 1, blocked.Attempt, "and no attempt is spent finding that out.");
        AssertEx.Contains(AssertEx.NotNull(blocked.TerminalReason), "Development Mode is switched off");
    }

    /// <summary>
    ///     A node deadline reaches the development chain too: the attempt in flight is stopped and the node run ends on
    ///     the clock rather than on whatever that attempt would eventually have said.
    /// </summary>
    [Test]
    public async Task ADevTaskNodeRunPastItsDeadlineStopsTheAttemptAndEndsOnTheClock()
    {
        var clock = new ManualTimeProvider();
        await using var harness = NewHarness(clock);
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.HoldNextAttempt();
        var runId = await harness.StartRunAsync(ImpatientDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Status);

        clock.Advance(TimeSpan.FromMinutes(10));
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, harness.Chain.CancelledAttempts.Count, "the attempt is stopped rather than left running under a settled row.");
        var expired = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, expired.Status, "the node allowed one attempt, so a timeout has nowhere left to go but a human.");
        AssertEx.Equal(DevWorkflowFailureClasses.Timeout, expired.FailureClass, "the clock is why it ended, not the attempt it happened to cancel on the way.");
        AssertEx.Contains(AssertEx.NotNull(expired.TerminalReason), "5 seconds");
        AssertEx.Equal(taskId, expired.DevelopmentTaskId);
    }

    /// <summary>
    ///     A node run against a task the chain has ALREADY driven to <c>AwaitingApply</c> succeeds without asking it for
    ///     anything — the development state machine has no way back to <c>InProgress</c>, and the claim the node makes
    ///     is true either way. This is what a fix loop routed into an implementation node lands on.
    /// </summary>
    [Test]
    public async Task ANodeRunAgainstAnAlreadyImplementedTaskSucceedsWithoutDrivingIt()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        while ((await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status != DevelopmentTaskStatus.AwaitingApply)
        {
            _ = await harness.Chain.StartNextActionAsync(projectId, taskId, Guid.NewGuid()).ConfigureAwait(false);
        }

        var driven = harness.Chain.Actions.Count;
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));
        AssertEx.Equal(expected: 1, implemented.Attempt);
        AssertEx.Contains(AssertEx.NotNull(implemented.OutputJson), "\"taskStatus\":\"AwaitingApply\"");
        AssertEx.Equal(driven, harness.Chain.Actions.Count, "the node run asked the chain for nothing: there was nothing left for it to do.");
    }

    /// <summary>
    ///     A node run that already names a task drives THAT task, whatever else the project carries. The pointer is
    ///     written once and survives a reset, so re-resolving here would let a re-attempt walk away from the work its
    ///     first attempt started — and now that a project can carry several tasks, it would walk onto somebody else's.
    /// </summary>
    [Test]
    public async Task ANodeRunThatAlreadyNamesATaskDrivesThatOneAndNotTheProjectsFirst()
    {
        await using var harness = NewHarness();
        var (projectId, firstTaskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        var secondTaskId = await AddTaskAsync(harness, projectId, "Implement the second slice").ConfigureAwait(false);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);
        await PinTaskAsync(harness, runId, "implement", secondTaskId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));
        AssertEx.Equal(secondTaskId, implemented.DevelopmentTaskId, "the row's own pointer decides, not the order the project's tasks happen to be in.");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await ReadTaskAsync(harness, secondTaskId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.Planned,
            (await ReadTaskAsync(harness, firstTaskId).ConfigureAwait(false)).Status,
            "and the project's first task was never touched, which is the whole point of naming one.");
    }

    /// <summary>
    ///     The undecomposed graph is unchanged: a node run that names no task and was not materialized from anything
    ///     drives the project's first task, exactly as it did when that was the only task a project could have.
    /// </summary>
    [Test]
    public async Task AnOrdinaryNodeRunStillDrivesTheProjectsFirstTask()
    {
        await using var harness = NewHarness();
        var (projectId, firstTaskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        var secondTaskId = await AddTaskAsync(harness, projectId, "Implement the second slice").ConfigureAwait(false);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(firstTaskId, implemented.DevelopmentTaskId, "the project's own task is the one an undecomposed graph implements.");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await ReadTaskAsync(harness, firstTaskId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.Planned,
            (await ReadTaskAsync(harness, secondTaskId).ConfigureAwait(false)).Status,
            "and nothing else in the project moved.");
        AssertEx.Equal(expected: 2,
            (await ListTasksAsync(harness, projectId).ConfigureAwait(false)).Count,
            "no task was created: this node had one to drive.");
    }

    /// <summary>
    ///     A materialized child implements its own slice, so it gets a task of its OWN in the same project — inheriting
    ///     that project's acceptance criteria and review budget, and taking its title and requirements from the brief
    ///     its materialization wrote onto the row.
    ///     <para>
    ///         Nothing materializes <c>DevTask</c> children yet; this is the branch decomposition will arrive on, and
    ///         driving it by hand is the only way to hold it to the contract before then.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AMaterializedChildImplementsATaskOfItsOwnInTheSameProject()
    {
        await using var harness = NewHarness();
        var (projectId, firstTaskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);
        await MaterializeChildAsync(harness,
                runId,
                "implement",
                "implement#1",
                projectId,
                """{"title":"Implement the second slice","requirements":"Do the other half."}""")
            .ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var child = await harness.ReadNodeRunAsync(runId, "implement#1").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, child.Status, AssertEx.NotNull(child.TerminalReason ?? child.OutputJson));
        var tasks = await ListTasksAsync(harness, projectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, tasks.Count, "the child implements its own slice, so it created its own task rather than sharing one.");
        AssertEx.Equal(firstTaskId,
            (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).DevelopmentTaskId,
            "the template's own node still drives the project's task.");

        var childTaskId = tasks.Single(task => task.Id != firstTaskId).Id;
        AssertEx.Equal(childTaskId, child.DevelopmentTaskId, "and the child names the task it created, which is how a reader reaches its evidence.");

        var created = await ReadTaskAsync(harness, childTaskId).ConfigureAwait(false);
        var parent = await ReadTaskAsync(harness, firstTaskId).ConfigureAwait(false);
        AssertEx.Equal("Implement the second slice", created.Title, "the brief the materialization wrote is what the child implements.");
        AssertEx.Equal("Do the other half.", created.Requirements);
        AssertEx.Equal(parent.AcceptanceCriteriaJson, created.AcceptanceCriteriaJson, "a slice of a project is done by that project's standard.");
        AssertEx.Equal(parent.MaxReviewRounds, created.MaxReviewRounds, "and gets the review budget the operator configured for it.");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, created.Status, "the child drove its own task through the same chain.");
    }

    /// <summary>
    ///     A child whose brief does not say what to implement stands down for a human rather than inheriting the
    ///     project's whole feature — and, as much to the point, rather than staying <c>Pending</c>: a row that never
    ///     started carries no deadline anything can fire, so an exception escaping the resolve would leave it to be
    ///     re-dispatched by every sweep for the life of the run.
    /// </summary>
    [Test]
    public async Task AMaterializedChildWithNothingToImplementStandsDownInsteadOfWedging()
    {
        await using var harness = NewHarness();
        var (projectId, firstTaskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);
        await MaterializeChildAsync(harness, runId, "implement", "implement#1", projectId, """{"title":"Implement the second slice","requirements":"   "}""")
            .ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var child = await harness.ReadNodeRunAsync(runId, "implement#1").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, child.Status, $"the row settled on {child.Status} rather than waiting for a sweep that would never finish it.");
        AssertEx.Equal(DevWorkflowFailureClasses.Configuration, child.FailureClass, "a brief that names nothing to build is a decided fact, not something another attempt answers.");
        AssertEx.Contains(AssertEx.NotNull(child.TerminalReason), "implement#1");
        AssertEx.Contains(AssertEx.NotNull(child.TerminalReason), "requirements");
        AssertEx.Null(child.DevelopmentTaskId, "and it names no task, because it never created one.");
        AssertEx.Equal(expected: 1,
            (await ListTasksAsync(harness, projectId).ConfigureAwait(false)).Count,
            "the project still carries only its own task: a child with nothing to implement must not add one.");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply,
            (await ReadTaskAsync(harness, firstTaskId).ConfigureAwait(false)).Status,
            "and the sibling that could run was unaffected.");
    }

    /// <summary>
    ///     The other side of a DevTask node's deep link: the Development task it drives names the run back, which is
    ///     what lets that page defer the apply to the workflow's gate. A task nobody's workflow drives names nothing.
    /// </summary>
    [Test]
    public async Task ATaskADevTaskNodeRunDrives_NamesThatRunBackOnTheDevelopmentTask()
    {
        // A host of its own with the REAL Development management service: this is about what Dev Mode's own page reads.
        await using var harness = new DevWorkflowHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        var undrivenTaskId = await AddTaskAsync(harness, projectId, "Nobody's workflow drives this").ConfigureAwait(false);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);
        await PinTaskAsync(harness, runId, "implement", taskId).ConfigureAwait(false);

        await using var scope = harness.Services.CreateAsyncScope();
        var management = scope.ServiceProvider.GetRequiredService<IDevelopmentManagementService>();

        AssertEx.Equal(runId,
            (await management.GetTaskAsync(projectId, taskId).ConfigureAwait(false)).WorkflowRunId,
            "the task a node run named names that node run's run back.");
        AssertEx.Null((await management.GetTaskAsync(projectId, undrivenTaskId).ConfigureAwait(false)).WorkflowRunId,
            "and a task an operator drives themselves says nothing about workflows, which is every task on a node that runs none.");
    }

    /// <summary>
    ///     Y3 is enforced by the SERVER, not by a hidden button: while the run driving a task is live, Dev Mode's own
    ///     apply refuses — for the endpoint, for a script, for anything that is not that run's apply lane.
    ///     <para>
    ///         The three answers in one test because they are one rule: no run named ⇒ refused; the OWNING run named ⇒
    ///         through; the run ENDED ⇒ through again, because a terminal run answers no further gate and withholding
    ///         the apply then would strand an already-validated patch.
    ///     </para>
    ///     <para>
    ///         "Through" is asserted as "not refused for this reason" rather than as a landed patch: past the guard the
    ///         apply reaches the repository binding and the approved-subject preconditions, and neither this harness's
    ///         seeded folder nor its untouched task can satisfy them. What the guard owns is exactly the sentence.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ADevModeApply_IsRefusedWhileALiveWorkflowRunOwnsTheTask_AndOnlyForACallerThatIsNotThatRun()
    {
        // The REAL Development management service: the scripted chain is the thing under test's stand-in everywhere
        // else, and a guard asserted against a stand-in is not asserted at all.
        await using var harness = new DevWorkflowHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);
        await PinTaskAsync(harness, runId, "implement", taskId).ConfigureAwait(false);

        var operatorApply = await ApplyFailureAsync(harness, projectId, taskId, onBehalfOfWorkflowRunId: null).ConfigureAwait(false);
        AssertEx.Contains(operatorApply, "has not ended");
        AssertEx.Contains(operatorApply, runId.ToString("D"));

        var strangerApply = await ApplyFailureAsync(harness, projectId, taskId, Guid.NewGuid()).ConfigureAwait(false);
        AssertEx.Contains(strangerApply, "has not ended", message: "and naming SOME run is not naming this one.");

        var ownApply = await ApplyFailureAsync(harness, projectId, taskId, runId).ConfigureAwait(false);
        AssertEx.False(ownApply.Contains("has not ended", StringComparison.Ordinal), $"the run's own lane must get past its own gate: {ownApply}");

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Completed).ConfigureAwait(false);
        var afterTheRunEnded = await ApplyFailureAsync(harness, projectId, taskId, onBehalfOfWorkflowRunId: null).ConfigureAwait(false);
        AssertEx.False(afterTheRunEnded.Contains("has not ended", StringComparison.Ordinal), $"a terminal run hands the authority back: {afterTheRunEnded}");
    }

    /// <summary>
    ///     With the module SWITCHED OFF the ownership guard stands down, and it has to.
    ///     <para>
    ///         The dispatcher only runs when <c>DevWorkflows:Enabled</c> is set, so a run that was live when the switch
    ///         flipped never reaches a terminal status and never answers another gate. An unconditional guard would
    ///         then refuse that run's tasks from Dev Mode for ever — behind a workflow UI that is off too, so there is
    ///         no way to approve them anywhere. Off means there is no competing gate to protect.
    ///     </para>
    /// </summary>
    [Test]
    public async Task WithTheModuleSwitchedOff_ALiveRunsTaskIsStillTheOperatorsToApply()
    {
        await using var harness = new DevWorkflowHarness(("DevWorkflows:Enabled", "false"));
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);
        await PinTaskAsync(harness, runId, "implement", taskId).ConfigureAwait(false);

        // The run really is live and really does own the task — this is the same row that is refused with the module on.
        AssertEx.False(DevWorkflowStateMachine.IsTerminal((await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status));

        var failure = await ApplyFailureAsync(harness, projectId, taskId, onBehalfOfWorkflowRunId: null).ConfigureAwait(false);
        AssertEx.False(failure.Contains("has not ended", StringComparison.Ordinal),
            $"a run nothing can advance cannot hold a patch hostage: {failure}");
    }

    /// <summary>A task no node run ever named is nobody's but the operator's, which is nearly every task there is.</summary>
    [Test]
    public async Task ADevModeApply_OnATaskNoWorkflowDrives_IsNotRefusedByTheOwnershipGuard()
    {
        await using var harness = new DevWorkflowHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        var undriven = await AddTaskAsync(harness, projectId, "Nobody's workflow drives this").ConfigureAwait(false);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);
        await PinTaskAsync(harness, runId, "implement", taskId).ConfigureAwait(false);

        var failure = await ApplyFailureAsync(harness, projectId, undriven, onBehalfOfWorkflowRunId: null).ConfigureAwait(false);
        AssertEx.False(failure.Contains("has not ended", StringComparison.Ordinal), $"a live run next door owns its own task, not the project: {failure}");
    }

    /// <summary>The message Development refused an apply with, whatever refused it.</summary>
    private static async Task<string> ApplyFailureAsync(DevWorkflowHarness harness, Guid projectId, Guid taskId, Guid? onBehalfOfWorkflowRunId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var management = scope.ServiceProvider.GetRequiredService<IDevelopmentManagementService>();
        return (await AssertEx.ThrowsAsync<Exception>(() => management.ApplyAsync(projectId, taskId, Guid.NewGuid(), onBehalfOfWorkflowRunId),
                                 "an apply on a task with neither a connected repository nor an approved subject cannot succeed.")
                              .ConfigureAwait(false)).Message;
    }

    /// <summary>The workflow host with the development chain scripted, and its own development project to drive.</summary>
    private static DevWorkflowHarness NewHarness(TimeProvider? clock = null) =>
        DevWorkflowHarness.WithAScriptedChain(clock);

    private static Task<(Guid ProjectId, Guid TaskId)> SeedDevelopmentTaskAsync(DevWorkflowHarness harness) =>
        harness.SeedDevelopmentProjectAsync();

    private static Task<DevelopmentTaskSnapshot> ReadTaskAsync(DevWorkflowHarness harness, Guid taskId) =>
        harness.ReadDevelopmentTaskAsync(taskId);

    private static Task<IReadOnlyList<DevelopmentTaskSnapshot>> ListTasksAsync(DevWorkflowHarness harness, Guid projectId) =>
        harness.ListDevelopmentTasksAsync(projectId);

    /// <summary>A second task on the project, through the internal capability decomposition uses.</summary>
    private static async Task<Guid> AddTaskAsync(DevWorkflowHarness harness, Guid projectId, string title)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>()
                                 .CreateTaskAsync(new DevelopmentCreateTaskCommand(projectId,
                                     Guid.NewGuid(),
                                     Guid.NewGuid(),
                                     title,
                                     "It has to do the other thing.",
                                     "[\"it does the other thing\"]"))
                                 .ConfigureAwait(false);
        return created.TaskId ?? throw new AssertionException("The create answered without naming the task it created.");
    }

    /// <summary>
    ///     Writes the pointer onto a node run before it is dispatched — what a materialization that already knew the
    ///     task, or an attempt that has already run, leaves behind.
    /// </summary>
    private static async Task PinTaskAsync(DevWorkflowHarness harness, Guid runId, string nodeKey, Guid taskId)
    {
        var nodeRun = await harness.ReadNodeRunAsync(runId, nodeKey).ConfigureAwait(false);
        await using var scope = harness.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                       .TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(runId,
                           nodeRun.Id,
                           DevWorkflowVersions.Any,
                           DevWorkflowNodeRunStatus.Pending,
                           DevelopmentTaskId: taskId))
                       .ConfigureAwait(false);
    }

    /// <summary>
    ///     Materializes one child of <paramref name="templateNodeKey" />, rewriting the run's graph to carry it — the
    ///     shape decomposition will produce, driven by hand because nothing produces it yet.
    /// </summary>
    private static async Task MaterializeChildAsync(DevWorkflowHarness harness,
        Guid runId,
        string templateNodeKey,
        string childNodeKey,
        Guid projectId,
        string inputJson)
    {
        var template = await harness.ReadNodeRunAsync(runId, templateNodeKey).ConfigureAwait(false);
        await using var scope = harness.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                       .MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(runId,
                           DevWorkflowVersions.Any,
                           Guid.NewGuid(),
                           [
                               new DevWorkflowNodeRunSeed(Guid.NewGuid(),
                                   childNodeKey,
                                   DevWorkflowNodeType.DevTask,
                                   MaxAttempts: 1,
                                   DevelopmentProjectId: projectId,
                                   InputJson: inputJson,
                                   MaterializedFromNodeRunId: template.Id,
                                   MaterializationIndex: 1)
                           ],
                           $$"""
                             {
                               "schemaVersion": 1,
                               "nodes": [{ "nodeKey": "{{templateNodeKey}}", "nodeType": "DevTask", "label": "Implement", "maxAttempts": 2 },
                                         { "nodeKey": "{{childNodeKey}}", "nodeType": "DevTask", "label": "Implement (1)", "maxAttempts": 1 }],
                               "edges": [{ "from": "{{templateNodeKey}}", "to": "{{childNodeKey}}" }]
                             }
                             """))
                       .ConfigureAwait(false);
    }

    /// <summary>Lands the held attempt the way its runner would have, so the drain has nothing left to wait for.</summary>
    private static async Task LandTheHeldAttemptAsync(DevWorkflowHarness harness, Guid taskId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var attempts = await store.ListAttemptsAsync(taskId).ConfigureAwait(false);
        var attempt = attempts[^1];
        _ = await store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand(attempt.Id,
                               Guid.NewGuid(),
                               DevelopmentAttemptStatus.Succeeded,
                               attempt.Version))
                       .ConfigureAwait(false);
    }
}
