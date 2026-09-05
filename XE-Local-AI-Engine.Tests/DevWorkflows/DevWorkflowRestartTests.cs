namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Killing the engine at each interesting point of a run, one test per row of the runtime plan's restart
///     walkthrough.
///     <para>
///         This is the feature, not a corner of it: a workflow run legitimately spans days, so surviving a restart
///         without an operator having to restart anything is the whole reason the runtime keeps its truth in rows. Each
///         test asserts what was LOST as much as where the run landed.
///     </para>
///     <para>
///         Every test here keeps a private host, so this class alone does not share one the way the rest of the
///         namespace does: <c>RestartAsync</c> runs the two startup reconcilers, which sweep EVERY run and every
///         workflow-owned work session in the database. On a shared host they would terminalize concurrent siblings'
///         runs — and reconciling the whole node is precisely what these tests assert, so it cannot be narrowed.
///     </para>
/// </summary>
public sealed class DevWorkflowRestartTests
{
    private const string SingleAgent = """
                                       {
                                         "schemaVersion": 1,
                                         "nodes": [{ "nodeKey": "research", "nodeType": "Agent", "label": "Research",
                                                     "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" }],
                                         "edges": []
                                       }
                                       """;

    private const string GateOnly = """
                                    {
                                      "schemaVersion": 1,
                                      "nodes": [{ "nodeKey": "approve", "nodeType": "HumanGate", "label": "Approve" }],
                                      "edges": []
                                    }
                                    """;

    private const string SingleTool = """
                                      {
                                        "schemaVersion": 1,
                                        "nodes": [{ "nodeKey": "validate", "nodeType": "Tool" }],
                                        "edges": []
                                      }
                                      """;

    /// <summary>Row #1 — killed before the graph snapshot was ever materialized. There is nothing to reconcile.</summary>
    [Test]
    public async Task ARunKilledBeforeItMaterialized_MaterializesOnTheFirstTickAfterTheRestart()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);

        await harness.RestartAsync().ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status);
        AssertEx.Equal(expected: 1,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "node.materialized"),
            "the graph is materialized once, whichever dispatcher gets there first.");
    }

    /// <summary>
    ///     Row #2 — killed with the start already written. The keyed upserts re-drive it to completion rather than
    ///     re-doing it, so a second dispatcher adds no second set of rows.
    /// </summary>
    [Test]
    public async Task ARunKilledJustAfterItStarted_IsNotMaterializedTwice()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        await harness.RestartAsync().ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count);
        AssertEx.Equal(expected: 1, (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "node.materialized"));

        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     Row #3 — killed mid agent step. The session reconciles to <c>Interrupted</c> one level down, the node run
    ///     goes back to <c>Pending</c> without spending an attempt, and the dispatcher resumes THAT session: the step
    ///     loop rebuilds its state from the database, so at most one step was lost.
    /// </summary>
    [Test]
    public async Task AnAgentKilledMidStep_ResumesItsOwnSessionWithoutSpendingAnAttempt()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var sessionId = await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false);

        await harness.RestartAsync().ConfigureAwait(false);

        var collapsed = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, collapsed.Status);
        AssertEx.Equal(expected: 1, collapsed.Attempt, "a restart is not a failure, so it is not an attempt.");
        AssertEx.Equal(sessionId, collapsed.WorkSessionId, "the row keeps its session; that is what makes the resume a continuation.");
        AssertEx.Contains(await harness.ReadEventTrailAsync(runId).ConfigureAwait(false), "node.interrupted");

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, harness.Agent.Created.Count, "the resumed node run must not start a second session.");
        AssertEx.Contains(harness.Agent.Calls, call => call == ("resume", sessionId));
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     Row #4 — killed while the session had already parked on its own step budget. Nothing terminalizes it, and
    ///     the node run resumes from the checkpoint the session wrote itself.
    /// </summary>
    [Test]
    public async Task AnAgentKilledWhileItsSessionWasParked_ResumesFromThatPause()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var sessionId = await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        await harness.RestartAsync().ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var resumed = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, resumed.Status);
        AssertEx.Equal(sessionId, resumed.WorkSessionId);
        AssertEx.Equal(expected: 1, resumed.Attempt);
    }

    /// <summary>
    ///     Row #3's other half — the session landed in the window between finishing and the poll writing what it said.
    ///     Nothing needs re-running: the row settles off the session's own answer, which is what that tick would have
    ///     written had it got there.
    /// </summary>
    [Test]
    public async Task AnAgentWhoseSessionLandedBeforeTheCrash_SettlesOnItRatherThanStartingOver()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var sessionId = await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false);

        // Completed, but the node run is still Running: nothing polled it before the host died.
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        await harness.RestartAsync().ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, harness.Agent.Created.Count, "a finished session must not be replaced by a second one doing the same work again.");
        var settled = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, settled.Status);
        AssertEx.Equal(sessionId, settled.WorkSessionId);
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A human retry is the opposite case, and the two are told apart by whether the row still holds its session: a
    ///     retry releases it, so the fresh attempt gets a fresh session rather than being settled straight back off the
    ///     answer that made it stop.
    /// </summary>
    [Test]
    public async Task AHumanRetryOfAFailedAgent_ReleasesItsSessionAndGetsANewOne()
    {
        // No resumes allowed, so the first park blocks the node run for a human WITH its session still attached — which
        // is the state the two rules have to be told apart in.
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxSessionResumesPerNodeRun", "0"));
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var spentSessionId = await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false);

        await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Paused).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Status);

        await harness.DecideAsync(runId, "research", DevWorkflowDecisionKind.Retry).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var retried = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, retried.Attempt);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, retried.Status);
        AssertEx.True(retried.WorkSessionId is { } fresh && fresh != spentSessionId,
            "resuming the session that ran out of budget would resume the context that ran out with it.");
        AssertEx.Equal(expected: 0, retried.SessionResumes, "the fresh attempt gets a fresh resume budget, or it is blocked before it takes a step.");
        AssertEx.Equal(expected: 2, harness.Agent.Created.Count);
    }

    /// <summary>
    ///     The one case a restart cannot repair: the session is gone, so there is no transcript to resume and a second
    ///     one would be work nobody asked for. It goes to a human with the reason on the row.
    /// </summary>
    [Test]
    public async Task AnAgentWhoseSessionWasDeleted_BlocksForAHumanAfterTheRestart()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.Agent.DeleteAsync(await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false)).ConfigureAwait(false);
        await harness.RestartAsync().ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.Configuration, blocked.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(blocked.TerminalReason), "no longer exists");
    }

    /// <summary>
    ///     A sandbox node run is different: its process is gone and its workspace may be half-prepared, so the re-run is
    ///     a real second attempt and has to count. Driven straight to <c>Running</c> because no sandbox lane exists yet
    ///     to put it there — the reconciler's rule is what is under test, not how the row got that way.
    /// </summary>
    [Test]
    public async Task AToolNodeKilledMidCommand_CountsItsReRunAsASecondAttempt()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleTool).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "validate", DevWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        await harness.RestartAsync().ConfigureAwait(false);

        var reconciled = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, reconciled.Status);
        AssertEx.Equal(expected: 2, reconciled.Attempt, "the command batch has to run again from the start, so the budget pays for it.");
    }

    /// <summary>
    ///     A run whose node runs keep being interrupted must not re-attempt forever. The budget counts a run's
    ///     RE-attempts, so a graph that has merely started has spent none of it.
    /// </summary>
    [Test]
    public async Task ARunThatHasSpentItsAttemptBudget_BlocksItsInterruptedNodeRunsInsteadOfLooping()
    {
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxTotalAttempts", "1"));
        var runId = await harness.StartRunAsync(SingleTool).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "validate", DevWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        // The first restart spends the run's one re-attempt; the second finds the budget gone.
        await harness.RestartAsync().ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "validate", DevWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        await harness.RestartAsync().ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.BudgetExhausted, blocked.FailureClass);
    }

    /// <summary>
    ///     The restart's own crash window: the host dies while recovering, between collapsing the stranded rows and
    ///     writing what each one costs. Nothing may be left half-repaired — a row that read as an ordinary <c>Pending</c>
    ///     would be re-run on the next boot with no attempt spent and no budget consulted, for ever.
    /// </summary>
    [Test]
    public async Task ARecoveryThatDiesBeforeItCommits_LeavesTheNodeRunForTheNextBootToRepairExactlyOnce()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleTool).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "validate", DevWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        await harness.FailRecoveryAsync().ConfigureAwait(false);
        await harness.FailRecoveryAsync().ConfigureAwait(false);

        var stranded = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, stranded.Status, "A recovery that did not commit leaves the row exactly as the dead host left it.");
        AssertEx.Equal(expected: 1, stranded.Attempt);

        await harness.RestartAsync().ConfigureAwait(false);

        var repaired = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, repaired.Status, "The boot that does commit finds the row and repairs it.");
        AssertEx.Equal(expected: 2, repaired.Attempt, "One interruption costs one attempt, however many boots died trying to record it.");
        AssertEx.Equal(expected: 1,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "node.interrupted"),
            "and one interrupted event, for the same reason.");
    }

    /// <summary>
    ///     The same window, for the row a restart cannot repair at all: a failed recovery must not lose the fact that
    ///     this node run needs a human, which is what a row left at <c>Pending</c> would do.
    /// </summary>
    [Test]
    public async Task ARecoveryThatDiesBeforeItCommits_StillBlocksAnAgentWhoseSessionIsGoneOnTheNextBoot()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.Agent.DeleteAsync(await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false)).ConfigureAwait(false);

        await harness.FailRecoveryAsync().ConfigureAwait(false);
        await harness.RestartAsync().ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.Configuration, blocked.FailureClass);
    }

    /// <summary>
    ///     And for the budget: the guard against a restart loop is exactly what a lost repair would disable, so a boot
    ///     that died mid-recovery must still hand its over-budget node runs to a human rather than re-attempting them.
    /// </summary>
    [Test]
    public async Task ARecoveryThatDiesBeforeItCommits_StillEnforcesTheAttemptBudgetOnTheNextBoot()
    {
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxTotalAttempts", "1"));
        var runId = await harness.StartRunAsync(SingleTool).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "validate", DevWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        // The first restart spends the run's one re-attempt; the second dies before it can record anything, and the
        // third has to find the budget gone rather than a Pending row nothing accounted for.
        await harness.RestartAsync().ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "validate", DevWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        await harness.FailRecoveryAsync().ConfigureAwait(false);
        await harness.RestartAsync().ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.BudgetExhausted, blocked.FailureClass);
        AssertEx.Equal(expected: 2,
            blocked.Attempt,
            "The first boot spent the run's one re-attempt; the boot that died spent nothing, and the last had nothing left to spend.");
    }

    /// <summary>
    ///     The budget is a run-wide total, so a restart that finds several interrupted sandbox node runs cannot hand
    ///     each of them an attempt: what is left is handed out in node-key order, and the rows it does not reach are
    ///     blocked UNSPENT. Otherwise a run overspends its cap by the width of its fan-out on every boot — which is the
    ///     restart loop the cap exists to stop.
    /// </summary>
    [Test]
    public async Task ARestartWithFewerAttemptsLeftThanInterruptedToolNodes_SpendsOnlyWhatTheRunHasAndBlocksTheRest()
    {
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxTotalAttempts", "1"));
        var runId = await harness.StartRunAsync("""
                                                {
                                                  "schemaVersion": 1,
                                                  "nodes": [{ "nodeKey": "validate-a", "nodeType": "Tool" },
                                                            { "nodeKey": "validate-b", "nodeType": "Tool" }],
                                                  "edges": [{ "from": "validate-a", "to": "validate-b" }]
                                                }
                                                """)
                                 .ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "validate-a", DevWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "validate-b", DevWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        await harness.RestartAsync().ConfigureAwait(false);

        var nodeRuns = await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1,
            nodeRuns.Sum(static nodeRun => nodeRun.Attempt - 1),
            "The run spent one re-attempt because one is all it had, however many of its node runs were interrupted.");

        var unfunded = await harness.ReadNodeRunAsync(runId, "validate-b").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, unfunded.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.BudgetExhausted, unfunded.FailureClass);
        AssertEx.Equal(expected: 1, unfunded.Attempt, "A row nothing could pay for must not read as having tried again.");
    }

    /// <summary>
    ///     FU3-4 race B. A <c>Retry</c> recorded before the crash and never applied has spent no attempt for a sum over
    ///     <c>Attempt</c> to see, but the dispatcher turns it into one on its first tick after this boot. Counting only
    ///     that sum handed an interrupted sandbox row the very slot the pending decision had already promised, and the
    ///     run made one more re-attempt than it allows. Recovery counts spent PLUS reserved, exactly as the live retry
    ///     policy and the store's own admission do.
    /// </summary>
    [Test]
    public async Task ARestartWithAPendingRetryAlreadyReservingTheLastSlot_DoesNotSpendItTwice()
    {
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxTotalAttempts", "1"));
        var runId = await harness.StartRunAsync("""
                                                {
                                                  "schemaVersion": 1,
                                                  "nodes": [{ "nodeKey": "validate-a", "nodeType": "Tool" },
                                                            { "nodeKey": "validate-b", "nodeType": "Tool" }],
                                                  "edges": [{ "from": "validate-a", "to": "validate-b" }]
                                                }
                                                """)
                                 .ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        // The answer is durable and the attempt it buys is not: the host died before the dispatcher could turn this
        // decision into one. It reserves the run's only re-attempt all the same.
        await harness.DecideAsync(runId, "validate-a", DevWorkflowDecisionKind.Retry).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "validate-b", DevWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        await harness.RestartAsync().ConfigureAwait(false);

        var unfunded = await harness.ReadNodeRunAsync(runId, "validate-b").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, unfunded.Status, "The pending Retry has promised the only slot, so nothing is left for the interrupted row.");
        AssertEx.Equal(DevWorkflowFailureClasses.BudgetExhausted, unfunded.FailureClass);
        AssertEx.Equal(expected: 1, unfunded.Attempt, "A row nothing could pay for must not read as having tried again.");
    }

    /// <summary>
    ///     FU3-4 race C. The run-wide budget is not the only bound: a node run also carries its OWN cap, which the live
    ///     path checks before every automatic re-attempt. Recovery bypassed that check entirely, so an interrupted row
    ///     already at its cap was reset to <c>Pending</c> with one attempt more than it declares — the runtime reporting
    ///     that it broke its own budget, on a run with plenty of room left.
    /// </summary>
    [Test]
    public async Task ARestartOfAnInterruptedRowAlreadyAtItsOwnCap_BlocksItRatherThanIncrementingPastTheCap()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync("""
                                                {
                                                  "schemaVersion": 1,
                                                  "nodes": [{ "nodeKey": "validate", "nodeType": "Tool", "maxAttempts": 1 }],
                                                  "edges": []
                                                }
                                                """)
                                 .ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "validate", DevWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        await harness.RestartAsync().ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status, "The run-wide budget is untouched; this row simply has no attempt of its own left.");
        AssertEx.Equal(expected: 1, blocked.Attempt, "1 of 1 must not become 2 of 1.");
        AssertEx.Contains(AssertEx.NotNull(blocked.TerminalReason),
            "as many as it allows",
            message: "And it says which cap ran out, so nobody reads it as the run-wide budget.");
    }

    /// <summary>
    ///     A node run that keeps moving under recovery is settled by its last pass rather than left in flight. Nothing
    ///     downstream would ever pick it up — the dispatcher admits <c>Pending</c> rows and follows <c>Running</c> agent
    ///     ones, and no boot is scheduled to try again — so a row recovery walked away from would wedge its run for
    ///     good. It goes to a human instead, unspent.
    /// </summary>
    [Test]
    public async Task ANodeRunThatKeepsMovingUnderRecovery_IsSettledForAHumanRatherThanLeftInFlight()
    {
        await using var harness = DevWorkflowHarness.WithASecondWriter();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // From here every session read re-attempts the row, so each pass judges a row that has already moved on.
        harness.Drift.Target = (runId, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Id);

        await harness.RestartAsync().ConfigureAwait(false);

        AssertEx.Empty(await harness.ReadInterruptedNodeRunsAsync().ConfigureAwait(false),
            "Nothing may still be in flight when the dispatcher starts: it polls neither a Queued row nor a Running sandbox one.");
        var settled = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, settled.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.Interrupted, settled.FailureClass);
        AssertEx.Equal(DevWorkflowDecisionKind.Abandon, settled.PendingDecisionKind, "and it asks for an answer, or nobody would look at it.");
        AssertEx.Contains(AssertEx.NotNull(settled.TerminalReason), "could not settle");
    }

    /// <summary>
    ///     T4: startup recovery is the one node-run write path deliberately OUTSIDE the telemetry choke point —
    ///     <c>ReconcileNonTerminalNodeRunsAsync</c> is forwarded straight to the inner store — so a row it settles keeps
    ///     its verdict and carries no cost at all.
    ///     <para>
    ///         Driven on a row that would otherwise have plenty to report: an agent node run with a live session and a
    ///         real consumption row on it, landing in <c>Blocked</c>, which IS inside the choke point's status set. The
    ///         twelve nulls therefore prove the bypass rather than an absence of anything to collect.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Recovery_LeavesTelemetryNull()
    {
        await using var harness = DevWorkflowHarness.WithASecondWriter();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // What the collector WOULD have found, had this path crossed it.
        await DevWorkflowNodeRunTelemetryTests
              .AppendStepConsumptionAsync(harness,
                  runId,
                  "research",
                  """{"providerCalls":2,"estimatedInputTokens":120,"toolCallsCompleted":1,"toolSchemaTokens":40}""")
              .ConfigureAwait(false);

        // From here every session read re-attempts the row, so recovery gives up on it and settles it for a human.
        harness.Drift.Target = (runId, (await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).Id);
        await harness.RestartAsync().ConfigureAwait(false);

        var settled = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, settled.Status, "The verdict is the reconciler's, unchanged.");
        AssertEx.Equal(DevWorkflowFailureClasses.Interrupted, settled.FailureClass);
        DevWorkflowNodeRunTelemetryTests.AssertEmptyTelemetry(settled,
            "The reconciler bypasses the publishing decorator, so it writes no telemetry — not even for a row that had cost to report.");
    }

    /// <summary>Row #7 — the two human waits are durable states. A restart does not touch them at all.</summary>
    [Test]
    [Arguments(DevWorkflowNodeRunStatus.WaitingForApproval)]
    [Arguments(DevWorkflowNodeRunStatus.Blocked)]
    public async Task AHumanWait_SurvivesARestartUntouched(DevWorkflowNodeRunStatus wait)
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        if (wait == DevWorkflowNodeRunStatus.Blocked)
        {
            await harness.TransitionNodeRunAsync(runId, "approve", DevWorkflowNodeRunStatus.Blocked).ConfigureAwait(false);
        }

        var before = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);
        await harness.RestartAsync().ConfigureAwait(false);
        var after = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);

        AssertEx.Equal(wait, after.Status);
        AssertEx.Equal(before.Attempt, after.Attempt);
        AssertEx.Empty((await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Where(static entry => entry.EventType == "node.interrupted"),
            "nothing was interrupted: a durable human wait is exactly where it should be.");
    }

    /// <summary>
    ///     Row #8 — killed between the human's answer and the dispatcher acting on it. The decision is a durable row, so
    ///     the next tick reads it and acts; nothing is lost, only deferred.
    /// </summary>
    [Test]
    public async Task ADecisionTakenJustBeforeTheCrash_IsAppliedAfterTheRestart()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);

        await harness.RestartAsync().ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     Row #10 — killed mid run-terminalization. The answer is re-derived from node-run states that did not change,
    ///     so the run lands where it was always going to land.
    /// </summary>
    [Test]
    public async Task ARunKilledBeforeItsOwnTerminalWasWritten_TerminalizesAfterTheRestart()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // Every node run terminal, the run row not yet: the window a crash between the two writes leaves behind.
        await harness.TransitionNodeRunAsync(runId, "approve", DevWorkflowNodeRunStatus.Succeeded).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        await harness.RestartAsync().ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Completed, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     Row #12 — killed while a drain was still settling. The intent is on the run row, the reconciler settles the
    ///     node runs, and the next tick finishes the transition the operator asked for.
    /// </summary>
    [Test]
    [Arguments(DevWorkflowRunStatus.Cancelling, DevWorkflowRunStatus.Cancelled)]
    [Arguments(DevWorkflowRunStatus.Pausing, DevWorkflowRunStatus.Paused)]
    public async Task ADrainKilledBeforeItSettled_CompletesAfterTheRestart(DevWorkflowRunStatus draining, DevWorkflowRunStatus settled)
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.TransitionRunAsync(runId, draining).ConfigureAwait(false);

        await harness.RestartAsync().ConfigureAwait(false);
        AssertEx.Equal(draining, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status, "the reconciler never touches a run row; the intent stands.");

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(settled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     Row #13 — a decision taken while the run was paused, across a restart. Neither the pause nor the crash can
    ///     lose it: it is a row, and the first tick after the resume settles it.
    /// </summary>
    [Test]
    public async Task ADecisionTakenWhilePaused_SurvivesARestartAndSettlesOnTheResume()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(GateOnly).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Pausing).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Paused, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.RestartAsync().ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Paused,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "a paused run stays paused; the answer waits rather than restarting the run behind the operator's back.");

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Running).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A workflow session that was never driven and that no node run references is deleted at startup. A session a
    ///     node run owns is kept — and so is one that RAN and then lost its reference, because that is a re-attempt's
    ///     superseded session and its transcript is the only record of what that attempt actually did.
    /// </summary>
    [Test]
    public async Task ARestart_DeletesNeverDrivenOrphansAndKeepsEveryDrivenSession()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var owned = await harness.ReadSessionIdAsync(runId, "research").ConfigureAwait(false);

        // Exactly what a crash between CreateAsync and AttachWorkSessionAsync leaves: a real workflow-kind session row,
        // never started, that no node run has ever pointed at.
        var orphan = (await harness.Agent.CreateAsync("Orphaned research", "objective", Guid.NewGuid()).ConfigureAwait(false)).Id;

        // And the shape a re-attempt leaves behind: driven, then released when the retry took a fresh session.
        var superseded = (await harness.Agent.CreateAsync("Superseded attempt", "objective", Guid.NewGuid()).ConfigureAwait(false)).Id;
        _ = await harness.Agent.StartAsync(superseded).ConfigureAwait(false);

        await harness.RestartAsync().ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => harness.Agent.GetAsync(orphan),
                              "a never-driven unreferenced session holds no transcript and nothing can ever reach it, so the restart cleans it up.")
                          .ConfigureAwait(false);
        _ = await harness.Agent.GetAsync(superseded).ConfigureAwait(false);
        _ = await harness.Agent.GetAsync(owned).ConfigureAwait(false);
    }
}
