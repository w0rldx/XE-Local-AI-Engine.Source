namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    /// <summary>
    ///     The X9 shape on an implementation node: the check routes its failure back at the node that produced what it
    ///     was judging, which is a real Development task rather than a work session.
    /// </summary>
    private const string DevTaskFixLoop = """
                                          {
                                            "schemaVersion": 1,
                                            "nodes": [
                                              { "nodeKey": "implement", "nodeType": "DevTask", "label": "Implement", "maxAttempts": 2 },
                                              { "nodeKey": "validate", "nodeType": "Tool", "label": "Validate", "retryTarget": "implement" }
                                            ],
                                            "edges": [{ "from": "implement", "to": "validate" }]
                                          }
                                          """;

    /// <summary>
    ///     What the validate node's own report says, in the shape <c>DevWorkflowToolCommands</c> writes it: the fix
    ///     loop's routed payload carries only counts, so the REPORT is where the sentence a coder can act on lives.
    /// </summary>
    private const string FailingReport = """
                                         {"passed":false,"nodeKey":"validate","attempt":1,"baseCommit":"0123456789abcdef",
                                          "commandProfileId":"generic-git","commandProfileDigest":"digest",
                                          "failureCode":"tests_failed",
                                          "failureDetail":"The release test command reported failing tests.",
                                          "commands":[{"commandId":"dotnet_test_release_no_build","exitCode":1,"completed":true,
                                                       "outputTruncated":false,"durationMilliseconds":1200,
                                                       "standardOutput":"Failed! TheThing.ShouldWork","standardError":"",
                                                       "testOutcome":{"adapter":"dotnet","parsed":true,"discovered":15,"executed":15,
                                                                      "passed":12,"failed":3,"parseFailureCode":null,"parseFailureDetail":null}}],
                                          "completedAtUtc":0}
                                         """;

    /// <summary>
    ///     The same X9 shape with an extra attempt on the implementation node, so a transient failure can be retried
    ///     WITHOUT ending the fix loop the route started.
    /// </summary>
    private const string PatientDevTaskFixLoop = """
                                                 {
                                                   "schemaVersion": 1,
                                                   "nodes": [
                                                     { "nodeKey": "implement", "nodeType": "DevTask", "label": "Implement", "maxAttempts": 4 },
                                                     { "nodeKey": "validate", "nodeType": "Tool", "label": "Validate", "retryTarget": "implement" }
                                                   ],
                                                   "edges": [{ "from": "implement", "to": "validate" }]
                                                 }
                                                 """;

    /// <summary>The rule set a workflow injects, so an assertion can name the text rather than a hash.</summary>
    private const string HouseRules = "Never touch production without an approved plan.";

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
    ///     The fix loop can now FIX a development task. A routed re-attempt against a task the chain has already taken
    ///     to <c>AwaitingApply</c> asks Dev Mode for a new coder round — with the routed node's own validation report as
    ///     the review evidence — instead of re-succeeding in the same tick against work nothing asked to be changed.
    ///     <para>
    ///         Measured live on 2026-09-01 as the opposite: both routed re-attempts of the implementation node emitted
    ///         <c>node.started</c> and <c>node.completed</c> in the same second, and all three validation reports
    ///         carried the identical patch hash.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARoutedReAttemptAsksAnApprovedTaskForANewRoundInsteadOfReSucceeding()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        await DriveToAwaitingApplyAsync(harness, projectId, taskId).ConfigureAwait(false);

        // The round the change request asks for is held mid-flight, which is the only place its brief can be read: it
        // is composed when the attempt starts, from what the transition that asked for it recorded.
        harness.Chain.HoldNextAttempt();
        harness.Tools.Answer("validate",
            FakeDevWorkflowToolCommands.Failing() with
            {
                Report = Encoding.UTF8.GetBytes(FailingReport)
            },
            FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(DevTaskFixLoop, "Add the feature.", projectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var routed = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Single(static entry => entry.EventType == "node.retry.routed");
        AssertEx.Contains(AssertEx.NotNull(routed.DetailJson), "\"from\":\"validate\"");
        AssertEx.Contains(AssertEx.NotNull(routed.DetailJson), "\"to\":\"implement\"");
        AssertEx.Contains(harness.Chain.Actions,
            static action => action == nameof(DevelopmentTaskStatus.ChangesRequested),
            $"the routed re-attempt asked for rework and a genuinely new coder round ran from it: {string.Join(", ", harness.Chain.Actions)}");
        AssertEx.Equal(DevelopmentTaskStatus.InProgress,
            (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status,
            "the task left the approval it was sitting on and is being implemented again, rather than re-succeeding on the patch just refused.");

        var feedback = AssertEx.NotNull((await ReadCoderSnapshotAsync(harness, taskId).ConfigureAwait(false)).PreviousRoundFeedback,
            "the new round must be told what was wrong with the last one, or it re-implements blind.");
        AssertEx.Contains(feedback, "dotnet_test_release_no_build");
        AssertEx.Contains(feedback, "3 of 15 tests failed");
        AssertEx.Contains(feedback,
            "TheThing.ShouldWork",
            StringComparison.Ordinal,
            "the tail of the failing command's own output is what a coder can act on; the routed counts alone are not.");

        // The second round lands where the first did, and the node run settles on ITS attempt rather than asking again:
        // the change request is one-shot per attempt, keyed on the operation the transition was written under.
        await LandTheHeldAttemptAsync(harness, taskId).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));
        AssertEx.Equal(expected: 2, implemented.Attempt);
        AssertEx.Contains(AssertEx.NotNull(implemented.OutputJson), "\"taskStatus\":\"AwaitingApply\"");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     L8: a node's OWN transient failure must not read as a downstream node rejecting the work.
    ///     <para>
    ///         A same-node retry used to write <c>priorFailureNode = &lt;itself&gt;</c> onto the next attempt's input —
    ///         the identical carrier the cross-node fix loop uses for a real validation verdict — so the executor met an
    ///         approved, <c>AwaitingApply</c> task carrying what looked like a rejection and asked Dev Mode to implement
    ///         it again. Measured live on 2026-09-02: three transient reviewer failures on <c>add-negate-method</c> spent
    ///         the task's last review round that way, and the node ended <c>Blocked / BudgetExhausted</c> while a coder
    ///         attempt that had SUCCEEDED and a reviewer attempt that had APPROVED it sat unapplied.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ATransientFailureOnTheNodeItselfDoesNotAskAnApprovedTaskToBeImplementedAgain()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        harness.Chain.FailNextAttempts(1);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));
        AssertEx.Equal(expected: 2, implemented.Attempt, "the transient failure cost the node one of its own attempts, which is the only budget it should touch.");
        AssertEx.False(AssertEx.NotNull(implemented.InputJson).Contains("priorFailureNode", StringComparison.Ordinal),
            $"a same-node retry names no rejecting node, because there is none: {implemented.InputJson}");

        var task = await ReadTaskAsync(harness, taskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, task.Status);
        AssertEx.Equal(expected: 1,
            task.CurrentReviewRound,
            "the approved work went through review exactly once; a retry that spends a review round is spending the wrong budget.");
        AssertEx.False(harness.Chain.Actions.Contains(nameof(DevelopmentTaskStatus.ChangesRequested), StringComparer.Ordinal),
            $"nothing judged this implementation, so nothing may ask for it to be done again: {string.Join(", ", harness.Chain.Actions)}");
    }

    /// <summary>
    ///     L4: a rework reason has to be backed by something that actually ran. A routed failure carrying no readable
    ///     validation report and no command or test counts produced the sentence "0 of 0 commands failed, 0 tests
    ///     failed" — a measurement of nothing — and spent a coder round on it.
    ///     <para>
    ///         Stood down rather than succeeded: succeeding would send the run straight back round the loop to re-fail
    ///         the same check and spend the whole budget having tried nothing, so the node ends where an operator can
    ///         read why and Retry it, with the approved task untouched behind it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARoutedFailureWithNoReportAndNoCountsStandsTheNodeDownInsteadOfAskingForARound()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        await DriveToAwaitingApplyAsync(harness, projectId, taskId).ConfigureAwait(false);
        var drivenBefore = harness.Chain.Actions.Count;
        harness.Tools.Answer("validate",
            FakeDevWorkflowToolCommands.Refusing(DevWorkflowFailureClasses.ToolCommandFailed, "The gate stopped before it ran anything."),
            FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(DevTaskFixLoop, "Add the feature.", projectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var task = await ReadTaskAsync(harness, taskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, task.Status, "a verdict nobody reached does not move an approved task.");
        AssertEx.Equal(expected: 1, task.CurrentReviewRound, "and it spends none of the rounds a real rejection would.");
        AssertEx.False(harness.Chain.Actions.Skip(drivenBefore).Contains(nameof(DevelopmentTaskStatus.ChangesRequested), StringComparer.Ordinal),
            $"no change was requested: {string.Join(", ", harness.Chain.Actions.Skip(drivenBefore))}");
        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, implemented.Status, "the node ends rather than polling forever on an ask it will not make.");
        AssertEx.Equal(DevWorkflowFailureClasses.Configuration, implemented.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(implemented.TerminalReason), "left no validation report or failing counts");
        AssertEx.Equal(DevWorkflowDecisionKind.Abandon, implemented.PendingDecisionKind, "a human is asked, which is what makes the dead end recoverable.");
        AssertEx.Contains(await harness.ReadEventTrailAsync(runId).ConfigureAwait(false),
            "node.intervention.required",
            message: "the stand-down reaches the feed, so the operator is told rather than left reading a stalled row.");
    }

    /// <summary>
    ///     The interaction the two L8 fixes could have broken between them: a transient same-node failure landing in the
    ///     MIDDLE of a genuine fix loop. The routed verdict's own evidence has to survive it — keeping the routed node's
    ///     name while overwriting its counts with this node's count-less output would hand the L4 gate a rejection it
    ///     could no longer evidence, and stand a genuinely rejected implementation down as unactionable.
    /// </summary>
    [Test]
    public async Task ATransientFailureInsideAFixLoopKeepsTheRoutedVerdictAndItsEvidence()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        await DriveToAwaitingApplyAsync(harness, projectId, taskId).ConfigureAwait(false);
        harness.Tools.Answer("validate",
            FakeDevWorkflowToolCommands.Failing() with
            {
                Report = Encoding.UTF8.GetBytes(FailingReport)
            },
            FakeDevWorkflowToolCommands.Passing());

        // The coder round the route asks for fails transiently, which is what sends the node round its OWN retry while
        // the routed failure is still outstanding on its inputs.
        harness.Chain.FailNextAttempts(1);
        var runId = await harness.StartRunAsync(PatientDevTaskFixLoop, "Add the feature.", projectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        var input = AssertEx.NotNull(implemented.InputJson);
        AssertEx.Contains(input, "\"priorFailureNode\":\"validate\"", message: "the transient retry must not drop the node whose verdict routed the run back.");
        AssertEx.Contains(input, "\"testsFailed\":3", message: "nor overwrite that verdict's counts with its own count-less output.");
        AssertEx.Contains(harness.Chain.Actions,
            static action => action == nameof(DevelopmentTaskStatus.ChangesRequested),
            $"the rejection still asks for a new round: {string.Join(", ", harness.Chain.Actions)}");

        var feedback = AssertEx.NotNull((await ReadCoderSnapshotAsync(harness, taskId).ConfigureAwait(false)).PreviousRoundFeedback);
        AssertEx.Contains(feedback, "dotnet_test_release_no_build", StringComparison.Ordinal, "and still quotes the report rather than a generic sentence.");
        AssertEx.Contains(feedback, "3 of 15 tests failed");
    }

    /// <summary>
    ///     The rejection is answered ONCE, however many of its own attempts the node spends getting there.
    ///     <para>
    ///         The one ask used to be keyed on the target's own attempt, and a transient failure between the change
    ///         request and the round it asked for moves that attempt while the same rejection is still on the input. So
    ///         the rework round arrived back at <c>AwaitingApply</c> under a key nothing had written, and the node asked
    ///         for the SAME rejection to be implemented again — a second coder round against work a reviewer had just
    ///         approved, spending a review round each time until the task ran out of them and its approved patch was
    ///         discarded.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ATransientFailureAfterTheChangeRequest_DoesNotAskForTheSameRejectionTwice()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        await DriveToAwaitingApplyAsync(harness, projectId, taskId).ConfigureAwait(false);
        harness.Tools.Answer("validate",
            FakeDevWorkflowToolCommands.Failing() with
            {
                Report = Encoding.UTF8.GetBytes(FailingReport)
            },
            FakeDevWorkflowToolCommands.Passing());

        // The coder round the change request asked for fails transiently, which is what moves the node's own attempt
        // while the rejection that asked for it is still outstanding.
        harness.Chain.FailNextAttempts(1);
        var runId = await harness.StartRunAsync(PatientDevTaskFixLoop, "Add the feature.", projectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1,
            harness.Chain.Actions.Count(static action => action == nameof(DevelopmentTaskStatus.ChangesRequested)),
            $"one rejection is one ask, whatever the node spent reaching it: {string.Join(", ", harness.Chain.Actions)}");

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));

        var task = await ReadTaskAsync(harness, taskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, task.Status, "the round the rejection asked for landed, and nothing re-opened it afterwards.");
        AssertEx.Equal(expected: 2,
            task.CurrentReviewRound,
            "one round for the original approval and one for the rework: a third would be the same rejection charged twice.");
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A check that refuses before running anything writes no report, and must not be evidenced by the PREVIOUS
    ///     attempt's.
    ///     <para>
    ///         The report was picked by run and producing node key alone, so the latest one for the key was the earlier
    ///         attempt's — about an implementation that has since been rewritten and re-approved. That made the reason
    ///         look evidenced and asked a coder to fix output nothing had just produced, straight past the stand-down
    ///         that exists for exactly this case.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ACheckThatRefusedWithoutRunningAnything_DoesNotBorrowTheEarlierAttemptsReport()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        await DriveToAwaitingApplyAsync(harness, projectId, taskId).ConfigureAwait(false);

        // Round one refuses WITH a report, which is a real verdict. Round two refuses before it runs anything, so the
        // only report the node has ever written is round one's.
        harness.Tools.Answer("validate",
            FakeDevWorkflowToolCommands.Failing() with
            {
                Report = Encoding.UTF8.GetBytes(FailingReport)
            },
            FakeDevWorkflowToolCommands.Refusing(DevWorkflowFailureClasses.ToolCommandFailed, "The gate stopped before it ran anything."),
            FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(PatientDevTaskFixLoop, "Add the feature.", projectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1,
            harness.Chain.Actions.Count(static action => action == nameof(DevelopmentTaskStatus.ChangesRequested)),
            $"only the verdict that actually judged something asked for a round: {string.Join(", ", harness.Chain.Actions)}");

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, implemented.Status, AssertEx.NotNull(implemented.TerminalReason ?? implemented.OutputJson));
        AssertEx.Equal(DevWorkflowFailureClasses.Configuration, implemented.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(implemented.TerminalReason), "left no validation report or failing counts");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply,
            (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status,
            "and the approved work is untouched behind the stand-down.");
    }

    /// <summary>
    ///     A disk or permission fault reading the routed node's report is not a reason to stop asking for the round.
    ///     The blob store answers a missing or tampered blob with a status, but it still throws on an I/O fault — and
    ///     letting that escape would fail the tick and re-throw on every sweep after it, so the routed counts are what
    ///     the change request carries instead.
    /// </summary>
    [Test]
    public async Task WithTheReportUnreadable_TheChangeRequestFallsBackToWhatTheRouteItselfCarried()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain(clock: null,
            services =>
            {
                services.RemoveAll<IDevWorkflowArtifactBlobStore>();
                services.AddSingleton<IDevWorkflowArtifactBlobStore, UnreadableArtifactBlobStore>();
            });
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        await DriveToAwaitingApplyAsync(harness, projectId, taskId).ConfigureAwait(false);
        harness.Chain.HoldNextAttempt();
        harness.Tools.Answer("validate", FakeDevWorkflowToolCommands.Failing());
        var runId = await harness.StartRunAsync(DevTaskFixLoop, "Add the feature.", projectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var feedback = AssertEx.NotNull((await ReadCoderSnapshotAsync(harness, taskId).ConfigureAwait(false)).PreviousRoundFeedback,
            "an unreadable report costs the detail, not the round.");
        AssertEx.Contains(feedback, "1 of 4 commands failed, 3 tests failed");
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A task that has spent every review round is stood down where the reason is still legible, instead of being
    ///     driven through a whole coder attempt whose only possible end is Dev Mode blocking it at the review hop. The
    ///     change request itself charges no round — rounds are spent ENTERING review, and this transition never does.
    /// </summary>
    [Test]
    public async Task WithNoReviewRoundsLeft_ARoutedReAttemptStandsDownInsteadOfAskingForARoundThatCannotFinish()
    {
        await using var harness = NewHarness();
        var (projectId, _) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        var onlyOneRound = await AddTaskAsync(harness, projectId, "One review round only", maxReviewRounds: 1).ConfigureAwait(false);
        await DriveToAwaitingApplyAsync(harness, projectId, onlyOneRound).ConfigureAwait(false);
        harness.Tools.Answer("validate", FakeDevWorkflowToolCommands.Failing());
        var runId = await harness.StartRunAsync(DevTaskFixLoop, "Add the feature.", projectId).ConfigureAwait(false);
        await PinTaskAsync(harness, runId, "implement", onlyOneRound).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var implemented = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, implemented.Status, AssertEx.NotNull(implemented.OutputJson));
        AssertEx.Equal(DevWorkflowFailureClasses.BudgetExhausted, implemented.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(implemented.TerminalReason), "review rounds");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply,
            (await ReadTaskAsync(harness, onlyOneRound).ConfigureAwait(false)).Status,
            "a round it cannot finish is not asked for, so the task keeps the approval it earned.");
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
    /// <summary>
    ///     The policy event is written ONCE per node-run attempt, and a re-run of that dispatch tick records nothing
    ///     further.
    ///     <para>
    ///         The store's idempotency on the operation id the executor DERIVES from the run, the node key and the
    ///         node-run ATTEMPT is the only guard now: the executor records on every dispatch rather than only while
    ///         the node run named no task, so a fix loop that routes this node run back around re-applies the policy
    ///         its settle cleared. A crash between creating the task and writing the pointer re-dispatches the same
    ///         attempt, so this replays that exact write and asserts the log did not grow and the round still reads the
    ///         FIRST text.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ThePolicyAWorkflowInjects_IsRecordedOncePerAttemptEvenWhenTheDispatchTickIsReRun()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        _ = await harness.CreateRuleSetAsync("House rules", HouseRules, """{"projectIds":[],"nodeTypes":["DevTask"]}""").ConfigureAwait(false);

        // Held, so a real attempt row exists for the execution snapshot to be read off.
        harness.Chain.HoldNextAttempt();
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(taskId,
            (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).DevelopmentTaskId,
            "the node run bound the project's task, which is the tick the policy is recorded on.");

        await using var scope = harness.Services.CreateAsyncScope();
        var development = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();

        // The re-dispatch a crash between the task create and the pointer write leaves behind: the SAME run, node key,
        // attempt and phase, so the executor derives the same operation id and the store answers with what it already
        // wrote. Attempt 1 because a node run's first attempt is 1.
        _ = await development.RecordWorkflowPolicyAsync(taskId,
                                 DevWorkflowOperationId.For(runId, "implement", attempt: 1, "devtask-policy"),
                                 "Deploy straight to production on Fridays.",
                                 [new DevelopmentWorkflowRuleSetReference(Guid.NewGuid(), "House rules", "content-hash")])
                             .ConfigureAwait(false);

        var events = await development.ListEventsAsync(projectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1,
            events.Count(entry => entry.EventType == "WorkflowPolicyApplied"),
            "one injection per attempt: a re-run of the dispatch tick re-derives the same operation id rather than appending.");

        var attempt = (await development.ListAttemptsAsync(taskId).ConfigureAwait(false)).Single();
        var policy = AssertEx.NotNull((await development.GetExecutionSnapshotAsync(attempt.Id).ConfigureAwait(false)).WorkflowPolicyText);
        AssertEx.Contains(policy, HouseRules, message: "and the round still reads the text the first write recorded.");
        AssertEx.False(policy.Contains("Deploy straight to production on Fridays.", StringComparison.Ordinal),
            "a replayed write must not be able to hand the coder a policy the node run never resolved.");
    }

    /// <summary>
    ///     Settling the node run REVOKES the policy it injected, so what governed the workflow's rounds does not go on
    ///     governing the operator's own later ones. Without this the injection had no lifetime at all: the snapshot
    ///     reads the latest event on the task, and a manual round started after the workflow finished still carried the
    ///     workflow's last policy while nothing was enforcing it.
    /// </summary>
    [Test]
    public async Task SettlingTheNodeRun_ClearsTheWorkflowPolicyItInjected_Once()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        _ = await harness.CreateRuleSetAsync("House rules", HouseRules, """{"projectIds":[],"nodeTypes":["DevTask"]}""").ConfigureAwait(false);

        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Status,
            "the node run has to have settled for its settle to have written anything.");

        await using var scope = harness.Services.CreateAsyncScope();
        var development = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var events = await development.ListEventsAsync(projectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2,
            events.Count(entry => entry.EventType == "WorkflowPolicyApplied"),
            "the dispatch writes the injection and the settle writes the clear, once each — the settle is ticked repeatedly.");

        // The manual round an operator starts on the task the workflow left behind, driven through the store directly
        // because no workflow is asking for it any more. It is the round the stale policy used to govern.
        var awaiting = await development.GetTaskAsync(taskId).ConfigureAwait(false);
        var reworking = await development.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(taskId,
                                             Guid.NewGuid(),
                                             DevelopmentTaskStatus.ChangesRequested,
                                             awaiting.Version))
                                         .ConfigureAwait(false);
        var inProgress = await development.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(taskId,
                                              Guid.NewGuid(),
                                              DevelopmentTaskStatus.InProgress,
                                              reworking.Version))
                                          .ConfigureAwait(false);
        var attemptId = Guid.NewGuid();
        _ = await development.StartAttemptAsync(new DevelopmentStartAttemptCommand(taskId,
                                 attemptId,
                                 Guid.NewGuid(),
                                 DevelopmentAttemptRole.Coder,
                                 "local-model",
                                 "local",
                                 inProgress.Version))
                             .ConfigureAwait(false);

        AssertEx.Null((await development.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).WorkflowPolicyText,
            "a round started after the workflow settled is governed by nothing the workflow injected.");
    }

    /// <summary>
    ///     The OTHER ways a node run stops driving its task, each of which left the injection standing while the
    ///     dispatch re-recorded it every tick: a stand-down for a human, a cancelled attempt settling the row, and the
    ///     run cancel that writes the terminal itself. The clear now sits with the shared writers rather than at the
    ///     call sites, which is why one assertion covers all three.
    ///     <para>
    ///         Nothing transitions out of Blocked or Cancelled, so the proof here is that the clear was WRITTEN. That a
    ///         blank row reads back as no policy is the store's own test.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments("blocked", DevWorkflowNodeRunStatus.Blocked)]
    [Arguments("attempt-cancelled", DevWorkflowNodeRunStatus.Cancelled)]
    [Arguments("run-cancelled", DevWorkflowNodeRunStatus.Cancelled)]
    public async Task EveryTerminalPath_ClearsTheWorkflowPolicyItInjected(string shape, DevWorkflowNodeRunStatus expected)
    {
        await using var harness = NewHarness();
        var (projectId, _) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);
        _ = await harness.CreateRuleSetAsync("House rules", HouseRules, """{"projectIds":[],"nodeTypes":["DevTask"]}""").ConfigureAwait(false);

        switch (shape)
        {
            case "blocked":
                harness.Chain.RefuseNextAttemptsOnPolicy(5);
                break;
            case "attempt-cancelled":

                // Held, so the cancel finds a real attempt in flight: the stop cancels THAT and the next poll settles
                // the row off what it landed as.
                harness.Chain.HoldNextAttempt();
                break;
            default:

                // Stalled in validation, so the run cancel finds NO attempt in flight and writes the terminal itself.
                harness.Chain.StallInValidation(count: 10);
                break;
        }

        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        if (shape != "blocked")
        {
            await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Cancelling).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        }

        AssertEx.Equal(expected, (await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false)).Status);

        await using var scope = harness.Services.CreateAsyncScope();
        var development = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var events = await development.ListEventsAsync(projectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2,
            events.Count(entry => entry.EventType == "WorkflowPolicyApplied"),
            $"the '{shape}' terminal must revoke the policy it was dispatched with, once.");
    }

    /// <summary>
    ///     FU2-3, the load-bearing half: the operator's reason for retrying a BLOCKED implementation node reaches the
    ///     coder round the retry starts, through the task's own change request — which is the one channel Dev Mode
    ///     composes a coder prompt out of. Observed live on 2026-09-02 as the opposite: a workspace policy refusal
    ///     blocked the node, the operator retried it saying exactly what to do differently, and the next round was
    ///     handed the same three fields the refused one was.
    ///     <para>
    ///         And asked ONCE. The reason stays on the node run's inputs for the life of the attempt, so without the
    ///         ledger operation id behind it every poll tick would ask for another round.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AnOperatorRetryOfABlockedDevTask_TellsTheNextCoderRoundWhatTheySaid()
    {
        await using var harness = NewHarness();
        var (projectId, taskId) = await SeedDevelopmentTaskAsync(harness).ConfigureAwait(false);

        // One refusal blocks the node with the task left InProgress behind a FAILED coder attempt, which is where a
        // retried implementation node lands. Nothing is held after it: the round the change request asks for has to
        // actually run, because the tick that walks the task back through InProgress is the one that re-reaches the
        // guard, and a held attempt would return at the "something is already working" check long before it.
        harness.Chain.RefuseNextAttemptsOnPolicy(1);
        var runId = await harness.StartRunAsync(SingleDevTask, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var blocked = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, (await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status);

        await harness.DecideAsync(runId, "implement", DevWorkflowDecisionKind.Retry, comment: "Do not add a test file; extend the existing negate tests.")
                     .ConfigureAwait(false);

        // Read WHILE the node run is still live. An OPERATOR instruction, not the previous round's feedback: the two
        // are separate fields because the prompts rank them, a person's sentence amending the task's immutable
        // requirements and outranking a reviewer's. It is bounded by the node run that made it, so the read has to
        // happen before the settle rather than after — see the last assertion in this test.
        var snapshot = await AdvanceUntilTheOperatorIsQuotedAsync(harness, runId, taskId).ConfigureAwait(false);
        var said = AssertEx.NotNull(snapshot.OperatorInstruction,
            "the round the operator paid for must be told what they said, or it redoes exactly what was refused.");
        AssertEx.Contains(said, "extend the existing negate tests");
        AssertEx.Contains(said, "implement", message: "and which step of the workflow the person was answering.");
        AssertEx.Null(snapshot.PreviousRoundFeedback, "nothing reviewed this round, so there is no previous round to quote.");

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var retried = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, retried.Attempt);
        AssertEx.Equal(expected: 3, retried.MaxAttempts, "the retry bought the attempt it is spending.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            retried.Status,
            $"the round the retry asked for ran to AwaitingApply: {retried.TerminalReason ?? retried.OutputJson}");

        AssertEx.Null((await ReadCoderSnapshotAsync(harness, taskId).ConfigureAwait(false)).OperatorInstruction,
            "and it stops governing the moment the node run that made it settles, or it would outrank the requirements of every round after.");

        // The tick after the asked-for round walks the task back to InProgress with no attempt this node run is
        // answerable for, so it reaches the guard a SECOND time on the same attempt with the reason still on its
        // inputs. Only the ledger operation id stops it asking again — without it the task loops back to
        // ChangesRequested for as long as the dispatcher keeps polling, and the round count below climbs with it.
        AssertEx.Equal(expected: 1,
            harness.Chain.Actions.Count(static action => action == nameof(DevelopmentTaskStatus.ChangesRequested)),
            $"the change request is one-shot per attempt: {string.Join(", ", harness.Chain.Actions)}");
    }

    private static DevWorkflowHarness NewHarness(TimeProvider? clock = null) =>
        DevWorkflowHarness.WithAScriptedChain(clock);

    private static Task<(Guid ProjectId, Guid TaskId)> SeedDevelopmentTaskAsync(DevWorkflowHarness harness) =>
        harness.SeedDevelopmentProjectAsync();

    private static Task<DevelopmentTaskSnapshot> ReadTaskAsync(DevWorkflowHarness harness, Guid taskId) =>
        harness.ReadDevelopmentTaskAsync(taskId);

    private static Task<IReadOnlyList<DevelopmentTaskSnapshot>> ListTasksAsync(DevWorkflowHarness harness, Guid projectId) =>
        harness.ListDevelopmentTasksAsync(projectId);

    /// <summary>A second task on the project, through the internal capability decomposition uses.</summary>
    private static async Task<Guid> AddTaskAsync(DevWorkflowHarness harness, Guid projectId, string title, int maxReviewRounds = 3)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>()
                                 .CreateTaskAsync(new DevelopmentCreateTaskCommand(projectId,
                                     Guid.NewGuid(),
                                     Guid.NewGuid(),
                                     title,
                                     "It has to do the other thing.",
                                     "[\"it does the other thing\"]",
                                     maxReviewRounds))
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

    /// <summary>Walks the task to <c>AwaitingApply</c> out of band, which is where a routed re-attempt finds it.</summary>
    private static async Task DriveToAwaitingApplyAsync(DevWorkflowHarness harness, Guid projectId, Guid taskId)
    {
        while ((await ReadTaskAsync(harness, taskId).ConfigureAwait(false)).Status != DevelopmentTaskStatus.AwaitingApply)
        {
            _ = await harness.Chain.StartNextActionAsync(projectId, taskId, Guid.NewGuid()).ConfigureAwait(false);
        }
    }

    /// <summary>The brief the task's latest coder attempt was composed from — the channel a rework reason travels down.</summary>
    /// <summary>
    ///     Ticks one at a time until the round the operator asked for is running and carries their sentence. One at a
    ///     time because the instruction is bounded by the node run: advancing straight to quiescence settles the node
    ///     run, which correctly revokes it, and would read as if it had never arrived.
    /// </summary>
    private static async Task<DevelopmentExecutionSnapshot> AdvanceUntilTheOperatorIsQuotedAsync(DevWorkflowHarness harness,
        Guid runId,
        Guid taskId,
        int maxTicks = 20)
    {
        for (var tick = 0; tick < maxTicks; tick++)
        {
            _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
            var snapshot = await ReadCoderSnapshotAsync(harness, taskId).ConfigureAwait(false);
            if (snapshot.OperatorInstruction is not null)
            {
                return snapshot;
            }
        }

        throw new InvalidOperationException($"No coder round quoting the operator started within {maxTicks} ticks.");
    }

    private static async Task<DevelopmentExecutionSnapshot> ReadCoderSnapshotAsync(DevWorkflowHarness harness, Guid taskId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var attempts = await store.ListAttemptsAsync(taskId).ConfigureAwait(false);
        return await store.GetExecutionSnapshotAsync(attempts[^1].Id).ConfigureAwait(false);
    }

    /// <summary>
    ///     Takes the bytes and then refuses to hand them back the way a disk fault does — a status the read contract
    ///     has no code for, which is the case the executor has to survive rather than re-throw every sweep.
    /// </summary>
    private sealed class UnreadableArtifactBlobStore : IDevWorkflowArtifactBlobStore
    {
        public Task<DevWorkflowArtifactBlobWriteResult> WriteAsync(Guid runId,
            Guid artifactId,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DevWorkflowArtifactBlobWriteResult($"{runId:N}/{artifactId:N}",
                Convert.ToHexString(SHA256.HashData(content.Span)),
                content.Length));

        public Task<DevWorkflowArtifactBlobReadResult> ReadAsync(Guid runId,
            Guid artifactId,
            string expectedHash,
            long expectedByteCount,
            CancellationToken cancellationToken = default) =>
            throw new IOException("The managed artifact blob could not be read.");

        public void Delete(Guid runId, Guid artifactId)
        {
        }

        public void DeleteRun(Guid runId)
        {
        }
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
