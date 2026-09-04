namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     C4: the integration stage. A fan-out's patches reach the repository through Dev Mode's own apply gate, one after
///     another, and ONLY after an operator answered the workflow's own human gate (Y3).
///     <para>
///         What is real here is everything the workflow owns: the parse rule that puts the gate in front, the decision
///         row, the enumeration of which tasks this run implemented, the sequencing, the failure classes and the report.
///         What is scripted is the host mutation — <see cref="FakeDevelopmentTaskChain.ApplyAsync" /> runs the store's
///         own apply ledger commands and skips the evidence verification and the git apply, both of which need a real
///         repository and two real model attempts. The evidence chain itself is asserted where it is real and was left
///         untouched by this phase: <c>DevelopmentValidationReviewAndApplyTests</c> and
///         <c>TrustedDevelopmentHostApplyPortHardeningTests</c> in the Development suite.
///     </para>
///     <para>
///         Every test takes a host of its OWN: the scripted chain is a container singleton whose history they read.
///     </para>
/// </summary>
public sealed class DevWorkflowIntegrationApplyTests
{
    /// <summary>
    ///     Two slices that do not depend on each other, so both are implemented and both have a patch to apply. Each
    ///     names the file it changes because this template's implementation node is a <c>DevTask</c>, and a package for
    ///     one of those is refused when a task names none — a coder there has to export a patch to finish.
    /// </summary>
    private const string TwoIndependentTasks = """
                                               [
                                                 { "id": "alpha", "title": "Add the parser", "goal": "Parse the manifest.", "changes": ["src/Manifest/Parser.cs"] },
                                                 { "id": "beta", "title": "Add the writer", "goal": "Write the manifest.", "changes": ["src/Manifest/Writer.cs"] }
                                               ]
                                               """;

    /// <summary>
    ///     A gate parting into two branches that each carry their own validation, converging on an <c>Any</c> join.
    ///     Under <c>Any</c> only one branch may run, so BOTH have to carry the property the apply is judged on — which
    ///     is exactly why the structural rule combines an <c>Any</c> join's inbound edges with AND.
    /// </summary>
    private const string TwoValidatedBranchesIntoAnAnyJoin = """
                                                             {
                                                               "schemaVersion": 1,
                                                               "nodes": [
                                                                 { "nodeKey": "route", "nodeType": "HumanGate", "label": "Which way" },
                                                                 { "nodeKey": "alphacheck", "nodeType": "Tool", "label": "Validate alpha" },
                                                                 { "nodeKey": "betacheck", "nodeType": "Tool", "label": "Validate beta" },
                                                                 { "nodeKey": "merge", "nodeType": "Join", "joinPolicy": "Any", "label": "Merge" },
                                                                 { "nodeKey": "approval", "nodeType": "HumanGate", "label": "Approve integration" },
                                                                 { "nodeKey": "integrate", "nodeType": "Tool", "toolMode": "Apply", "label": "Apply the approved patches" }
                                                               ],
                                                               "edges": [
                                                                 { "from": "route", "to": "alphacheck", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                                 { "from": "route", "to": "betacheck", "condition": { "path": "decision", "op": "eq", "value": "Reject" } },
                                                                 { "from": "alphacheck", "to": "merge" },
                                                                 { "from": "betacheck", "to": "merge" },
                                                                 { "from": "merge", "to": "approval" },
                                                                 { "from": "approval", "to": "integrate", "condition": { "path": "decision", "op": "eq", "value": "Approve" } }
                                                               ]
                                                             }
                                                             """;

    /// <summary>
    ///     Two independent implementation branches, each behind its OWN human gate and its own apply node. The shape a
    ///     run has whenever more than one thing is integrated separately, and the one a run-wide enumeration gets wrong.
    /// </summary>
    private const string TwoGatedApplyLanes = """
                                              {
                                                "schemaVersion": 1,
                                                "nodes": [
                                                  { "nodeKey": "fork", "nodeType": "Parallel", "label": "Fork" },
                                                  { "nodeKey": "alphaimplement", "nodeType": "DevTask", "label": "Implement alpha", "nodeTimeoutSeconds": 900 },
                                                  { "nodeKey": "alphacheck", "nodeType": "Tool", "label": "Validate alpha" },
                                                  { "nodeKey": "alphaapproval", "nodeType": "HumanGate", "label": "Approve alpha" },
                                                  { "nodeKey": "alphaapply", "nodeType": "Tool", "toolMode": "Apply", "label": "Apply alpha" },
                                                  { "nodeKey": "betaimplement", "nodeType": "DevTask", "label": "Implement beta", "nodeTimeoutSeconds": 900 },
                                                  { "nodeKey": "betacheck", "nodeType": "Tool", "label": "Validate beta" },
                                                  { "nodeKey": "betaapproval", "nodeType": "HumanGate", "label": "Approve beta" },
                                                  { "nodeKey": "betaapply", "nodeType": "Tool", "toolMode": "Apply", "label": "Apply beta" }
                                                ],
                                                "edges": [
                                                  { "from": "fork", "to": "alphaimplement" },
                                                  { "from": "fork", "to": "betaimplement" },
                                                  { "from": "alphaimplement", "to": "alphacheck" },
                                                  { "from": "alphacheck", "to": "alphaapproval" },
                                                  { "from": "alphaapproval", "to": "alphaapply", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                  { "from": "betaimplement", "to": "betacheck" },
                                                  { "from": "betacheck", "to": "betaapproval" },
                                                  { "from": "betaapproval", "to": "betaapply", "condition": { "path": "decision", "op": "eq", "value": "Approve" } }
                                                ]
                                              }
                                              """;

    /// <summary>
    ///     The named C4 gate: two task patches apply sequentially, and only after the gate approves.
    ///     <para>
    ///         The "only after" half is asserted BEFORE the decision as well as after it, because it is the half that
    ///         cannot be inferred from the end state: a run that applied at the join and then approached the gate would
    ///         finish looking exactly like this one.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TwoTaskPatchesApplyInOrderAndOnlyAfterTheGateApproves()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        var (runId, projectId) = await ImplementTwoSlicesAsync(harness).ConfigureAwait(false);

        var gate = await harness.ReadNodeRunAsync(runId, "integrationapproval").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, gate.Status, $"the run stopped at {gate.Status} instead of asking: {gate.TerminalReason}");
        AssertEx.Empty(harness.Chain.Offered, "nothing may reach the repository before the operator has answered.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, (await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false)).Status);

        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var alpha = await TaskIdAsync(harness, runId, "implement#alpha").ConfigureAwait(false);
        var beta = await TaskIdAsync(harness, runId, "implement#beta").ConfigureAwait(false);
        AssertEx.Equal($"{alpha:N}, {beta:N}",
            string.Join(", ", harness.Chain.Offered.Select(static taskId => taskId.ToString("N"))),
            "both patches, one after the other, in the order the decomposition put the slices in.");

        // Every apply named THIS run. The real service refuses an apply that names no run for a task a live run drives
        // (Y3, server-side), so a lane that stopped threading its run id would refuse its own patches in production
        // while this scripted chain applied them happily.
        AssertEx.Equal($"{runId:D}, {runId:D}", string.Join(", ", harness.Chain.OnBehalfOf.Select(static id => id?.ToString("D") ?? "<none>")));

        var integrate = await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, integrate.Status, AssertEx.NotNull(integrate.TerminalReason ?? integrate.OutputJson));
        AssertEx.Equal(DevelopmentTaskStatus.Completed, (await harness.ReadDevelopmentTaskAsync(alpha).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.Completed, (await harness.ReadDevelopmentTaskAsync(beta).ConfigureAwait(false)).Status);
        AssertEx.Equal(expected: 3,
            (await harness.ListDevelopmentTasksAsync(projectId).ConfigureAwait(false)).Count,
            "and the operator's own task was not swept into the integration: this run implemented two.");

        // The report is the operator's answer to "what went in", so it names the tasks rather than counting them.
        var report = await ReadApplyReportAsync(harness, runId).ConfigureAwait(false);
        AssertEx.Contains(report, alpha.ToString("D"));
        AssertEx.Contains(report, beta.ToString("D"));
        AssertEx.Contains(report, "\"outcome\":\"applied\"");

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "fullvalidate").ConfigureAwait(false)).Status,
            "and the integrated result was validated after the applies, not instead of them.");
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        // A replayed pass — what a crash between the apply and the row's own write leaves behind — applies NOTHING a
        // second time. Driven through the production path rather than the dispatcher, because a completed run is
        // terminal and no tick will ever look at it again, which is exactly why the guard cannot be observed from one.
        await using var scope = harness.Services.CreateAsyncScope();
        var replay = await scope.ServiceProvider.GetRequiredService<DevWorkflowApplyCommands>()
                                .RunAsync(await harness.ReadRunAsync(runId).ConfigureAwait(false), integrate, CancellationToken.None)
                                .ConfigureAwait(false);
        AssertEx.True(replay.Passed, "a run whose patches are already in is not a failure to put them in.");
        AssertEx.Contains(Encoding.UTF8.GetString(replay.Report.Span), "already-applied");
        AssertEx.Equal(expected: 2, harness.Chain.Offered.Count, "and the gate was not asked a second time about a task that is already applied.");
    }

    /// <summary>
    ///     The other half of Y3: a refused gate applies NOTHING. The run ends where it was refused rather than
    ///     completing through a skipped apply, and the implemented tasks are left exactly as they were — waiting to be
    ///     applied by somebody who decides to.
    /// </summary>
    [Test]
    public async Task ARefusedIntegrationGateAppliesNothingAndEndsTheRun()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        var (runId, _) = await ImplementTwoSlicesAsync(harness).ConfigureAwait(false);

        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Reject).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Empty(harness.Chain.Offered, "a refused approval is the whole point of the gate.");
        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, run.Status, "a gate answer no branch accepts ends the run rather than completing it.");
        AssertEx.Equal(DevWorkflowFailureClasses.GateRejected, run.FailureClass);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Cancelled,
            (await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false)).Status,
            "the apply node was ended by the refusal's drain without ever being admitted.");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply,
            (await harness.ReadDevelopmentTaskAsync(await TaskIdAsync(harness, runId, "implement#alpha").ConfigureAwait(false)).ConfigureAwait(false)).Status,
            "and the work is still there, still waiting for somebody to decide.");
    }

    /// <summary>
    ///     The v2 boundary, pinned because it is the shape a real two-slice run meets today. The apply gate takes the
    ///     FIRST patch and refuses the second: an approved subject names the base commit it was reviewed against, and
    ///     the first apply is sitting in that tree. §5.6.3 names concurrent-patch merge as v2, and this is what that
    ///     costs at runtime — one patch in, the node standing down for a human with both facts on the record, rather
    ///     than a second patch applied onto a tree nobody judged.
    /// </summary>
    [Test]
    public async Task AnApplyTheGateRefusesStopsTheSequenceAndSaysWhatLanded()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        harness.Chain.AllowApplies(count: 1);
        var (runId, _) = await ImplementTwoSlicesAsync(harness).ConfigureAwait(false);

        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var alpha = await TaskIdAsync(harness, runId, "implement#alpha").ConfigureAwait(false);
        var beta = await TaskIdAsync(harness, runId, "implement#beta").ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.Completed, (await harness.ReadDevelopmentTaskAsync(alpha).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.Blocked,
            (await harness.ReadDevelopmentTaskAsync(beta).ConfigureAwait(false)).Status,
            "the refused patch is NOT applied — Dev Mode's own gate stands the task down and says why, and the sequence stops there.");

        var integrate = await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, integrate.Status, "a refusal on evidence is a human's answer, not another attempt's.");
        AssertEx.Equal(DevWorkflowFailureClasses.Policy, integrate.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(integrate.TerminalReason), "not at the exact base");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending,
            (await harness.ReadNodeRunAsync(runId, "fullvalidate").ConfigureAwait(false)).Status,
            "and nothing validates a half-integrated repository as though it were finished.");

        var report = await ReadApplyReportAsync(harness, runId).ConfigureAwait(false);
        AssertEx.Contains(report, "\"outcome\":\"applied\"");
        AssertEx.Contains(report, "\"outcome\":\"blocked\"");
        AssertEx.Contains(report, "\"tasksApplied\":1");
    }

    /// <summary>
    ///     A retry of a Blocked apply ASKS the gate again. It is the operator's only in-run recovery from the v1 ceiling
    ///     above — repair the repository by hand, then retry the node — and it is dead the moment the apply is keyed on
    ///     anything but the attempt: Dev Mode answers a recorded operation id with what it recorded, so a retry that
    ///     re-issued attempt 1's key would be handed attempt 1's refusal without the repository being looked at at all.
    ///     <para>
    ///         Read off the scripted chain's own ledger, which counts what the gate was ASKED — a memoized refusal never
    ///         reaches it — and off the reason, which changes because the second answer is derived from the ledger as it
    ///         stands now rather than replayed from a row.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARetriedApplyAsksTheGateAgainInsteadOfEchoingTheRecordedRefusal()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        harness.Chain.AllowApplies(count: 0);
        var (runId, _) = await ImplementTwoSlicesAsync(harness).ConfigureAwait(false);
        var alpha = await TaskIdAsync(harness, runId, "implement#alpha").ConfigureAwait(false);

        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var blocked = await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.Status, AssertEx.NotNull(blocked.TerminalReason ?? blocked.OutputJson));
        AssertEx.Contains(AssertEx.NotNull(blocked.TerminalReason), "not at the exact base");
        AssertEx.Equal($"{alpha:N}", string.Join(", ", harness.Chain.Offered.Select(static taskId => taskId.ToString("N"))));

        // The repair, and the retry a Blocked node run offers.
        harness.Chain.AllowApplies(count: 4);
        await harness.DecideAsync(runId, "integrate", DevWorkflowDecisionKind.Retry).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var retried = await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false);
        AssertEx.Equal(blocked.Attempt + 1, retried.Attempt, "a retry is the next attempt of the node, and the apply is keyed under it.");
        AssertEx.Equal($"{alpha:N}, {alpha:N}",
            string.Join(", ", harness.Chain.Offered.Select(static taskId => taskId.ToString("N"))),
            "the retry reached the apply gate a second time rather than being answered from attempt 1's recorded refusal.");
        AssertEx.Contains(AssertEx.NotNull(retried.TerminalReason),
            "awaiting explicit apply",
            message: "and the answer is the ledger's own, read now: a task Dev Mode already stood down cannot complete, which is a "
                     + "different sentence from the one attempt 1 recorded.");

        // N3: Dev Mode's sentence is about a PRECONDITION — true, and about neither the cause nor the operator's move.
        // On its own it reads to someone who has just pressed Retry as a smaller, different problem than the one they
        // were retrying, so the lane says what happened first and keeps Dev Mode's answer behind it.
        AssertEx.Contains(AssertEx.NotNull(retried.TerminalReason),
            "is stood down in Development",
            message: "the retried refusal names the stand-down rather than only the precondition it left behind.");
        AssertEx.Contains(AssertEx.NotNull(retried.TerminalReason),
            "not at the approved base",
            message: "and carries Development's own recorded reason for the stand-down, which is what attempt 1's sentence had "
                     + "and attempt 2's had lost — read off the task rather than assumed, since a declined apply is not the only "
                     + "way one of these gets blocked.");
        AssertEx.Contains(AssertEx.NotNull(retried.TerminalReason),
            "Development answered:",
            message: "with the sanitized original still there, because it is what the Development view says about the same task.");
    }

    /// <summary>
    ///     The retried refusal is composed — a lead sentence, a model-authored title, the stored blocked reason, and Dev
    ///     Mode's own message — and it lands in <c>terminal_reason</c>, which the schema bounds at 1024. SQLite does not
    ///     enforce a declared length, so an over-long one would break that contract silently and only bite on a provider
    ///     that does. The lead survives the cut; the tail does not.
    /// </summary>
    [Test]
    public async Task AComposedRefusalIsCappedAtTheColumnsOwnBound()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        harness.Chain.AllowApplies(count: 0);
        harness.Chain.BlockedReason = $"The scripted host repository was not at the approved base. {new string('x', 1200)}";
        var (runId, _) = await ImplementTwoSlicesAsync(harness).ConfigureAwait(false);

        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "integrate", DevWorkflowDecisionKind.Retry).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var reason = AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false)).TerminalReason);
        AssertEx.Equal(expected: 1024, reason.Length, "the row's terminal reason is capped at the length its column declares.");
        AssertEx.True(reason.EndsWith('…'), $"and says it was cut rather than ending mid-word: {reason[^40..]}");
        AssertEx.Contains(reason, "is stood down in Development", message: "with the lead — what happened — kept, because the tail is what is expendable.");
    }

    /// <summary>
    ///     A cancel that arrives BETWEEN two patches leaves evidence. One patch is in the repository by then and its task
    ///     is Completed, so a node run that ended with no report at all would leave the operator to work out what landed
    ///     by reading Dev Mode's task list against the run's graph — which is the one question this node exists to answer.
    ///     <para>
    ///         The checkpoint is what makes that possible: cancellation is answered as a RESULT naming what happened, not
    ///         by letting the token throw out of the loop, because the throwing path settles the row off the task's own
    ///         cancellation and never asks the pass for what it did.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ACancelBetweenTwoPatchesStillReportsWhatLanded()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        harness.Chain.HoldAfterApplies(count: 1);
        var (runId, _) = await ImplementTwoSlicesAsync(harness).ConfigureAwait(false);
        var alpha = await TaskIdAsync(harness, runId, "implement#alpha").ConfigureAwait(false);
        var beta = await TaskIdAsync(harness, runId, "implement#beta").ConfigureAwait(false);

        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // The first patch is in and the sequence is standing between the two, which is the only moment a cancel can
        // arrive mid-sequence at all.
        await harness.Chain.ApplyHeld.WaitAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Cancelling).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        harness.Chain.ReleaseApplies();
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal($"{alpha:N}", string.Join(", ", harness.Chain.Offered.Select(static taskId => taskId.ToString("N"))), "the second patch was never offered.");
        AssertEx.Equal(DevelopmentTaskStatus.Completed, (await harness.ReadDevelopmentTaskAsync(alpha).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply,
            (await harness.ReadDevelopmentTaskAsync(beta).ConfigureAwait(false)).Status,
            "and the one that was not offered is still waiting, unchanged.");

        // The evidence first: this is the whole point. A cancel that threw out of the loop would settle the row off the
        // task's own cancellation and never write a report at all.
        var report = await ReadApplyReportAsync(harness, runId).ConfigureAwait(false);
        AssertEx.Contains(report, "\"tasksApplied\":1");
        AssertEx.Contains(report, "\"outcome\":\"applied\"");
        AssertEx.Contains(report, "\"outcome\":\"cancelled\"", message: "the patch that was never offered is named rather than left out of the list.");
        AssertEx.Contains(report, beta.ToString("D"));

        var integrate = await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Cancelled, integrate.Status, "being stopped is not a failure for the retry policy to route.");
        AssertEx.Equal(DevWorkflowFailureClasses.Cancelled, integrate.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(integrate.TerminalReason), "1 of 2 approved patches");
    }

    /// <summary>
    ///     A task title is what the DECOMPOSING AGENT called the slice — model text — and it reaches an operator through
    ///     this node's terminal reason and through every entry of a stored report. It goes through the same sanitizer the
    ///     lane's exception messages do, so a host path a model wrote into a title does not survive the trip.
    /// </summary>
    [Test]
    public async Task AModelAuthoredTitleIsSanitizedBeforeItReachesTheReport()
    {
        const string Slices = """
                              [
                                { "id": "alpha", "title": "Fix /home/operator/secrets/repo/parser.cs", "goal": "Parse the manifest.", "changes": ["src/Manifest/Parser.cs"] }
                              ]
                              """;

        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        harness.Chain.AllowApplies(count: 0);
        var (projectId, _) = await harness.SeedDevelopmentProjectAsync().ConfigureAwait(false);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.DecompositionIntoDevTasksAndIntegration, "Add the feature.", projectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", Slices).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        // The title is stored on the Development task exactly as the model wrote it — the redaction is this node's, at
        // the point where the title becomes something an operator reads.
        var taskId = await TaskIdAsync(harness, runId, "implement#alpha").ConfigureAwait(false);
        AssertEx.Contains((await harness.ReadDevelopmentTaskAsync(taskId).ConfigureAwait(false)).Title, "/home/operator/");

        var integrate = await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, integrate.Status, AssertEx.NotNull(integrate.TerminalReason ?? integrate.OutputJson));
        AssertEx.Contains(AssertEx.NotNull(integrate.TerminalReason), "[REDACTED:development-path]");
        AssertEx.False(AssertEx.NotNull(integrate.TerminalReason).Contains("/home/operator/", StringComparison.Ordinal),
            $"the node's own sentence still carries the host path a model wrote: {integrate.TerminalReason}");

        var report = await ReadApplyReportAsync(harness, runId).ConfigureAwait(false);
        AssertEx.False(report.Contains("/home/operator/", StringComparison.Ordinal), $"and so does the stored report: {report}");
    }

    /// <summary>
    ///     The seeded <c>feature-development-v1</c> parses under every rule this runtime has — one entry node, an
    ///     acyclic graph, a template subtree nothing points into, an ancestor retry target, an <c>All</c> join over the
    ///     fan-out, no duplicate edge, and an apply node behind a human gate. Seeding runs the same parse, so a template
    ///     that would fail at run start fails at startup; this asserts it does neither.
    /// </summary>
    [Test]
    public async Task TheSeededFeatureTemplateParsesAndIsSeededOnceOnly()
    {
        await using var harness = new DevWorkflowHarness();

        await SeedAsync(harness).ConfigureAwait(false);
        await SeedAsync(harness).ConfigureAwait(false);

        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
        var definitions = await store.ListDefinitionsAsync(includeArchived: true).ConfigureAwait(false);
        var seeded = definitions.Where(definition => string.Equals(definition.SeedSlug, DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug, StringComparison.Ordinal))
                                .ToList();
        AssertEx.Equal(expected: 1, seeded.Count, "seeding is idempotent on the slug, so a second startup adds nothing.");

        var definition = await store.GetDefinitionAsync(seeded[0].Id).ConfigureAwait(false);
        var graph = DevWorkflowGraph.Parse(definition.GraphJson);
        AssertEx.Equal(expected: 11, graph.Nodes.Count);
        AssertEx.Equal(expected: 11, seeded[0].NodeCount);
        AssertEx.Equal("research", string.Join(", ", graph.EntryNodeKeys.Where(key => !graph.TemplateKeys.Contains(key))));
        AssertEx.Equal("implement, validate", string.Join(", ", graph.TemplateKeys.OrderBy(static key => key, StringComparer.Ordinal)));
        AssertEx.Equal(DevWorkflowToolMode.Apply, graph.Nodes["integrate"].ToolMode);
        AssertEx.Equal(DevWorkflowToolMode.Validate, graph.Nodes["fullvalidate"].ToolMode);
        AssertEx.Equal(DevWorkflowJoinPolicy.All, graph.Nodes["join"].JoinPolicy, "an Any join over a materialized fan-out is refused at parse when it expands to one child.");
        AssertEx.True(graph.Nodes["implement"].NodeTimeoutSeconds > 0, "every DevTask node in a shipped template declares its own bound.");
        AssertEx.Equal("implement", graph.Nodes["validate"].RetryTarget);

        // FU-6. The per-slice loop fires BEFORE anything downstream of it exists, so its supersessions never have a
        // recorded consumer to flag and the template reported zero staleness on every run. This second target is what
        // makes the link reachable: by the time the integrated result is judged, the gate has been handed the
        // verification as its evidence and the apply node has consumed it too.
        //
        // It names `verify` and NOT `implement`: `implement` is the materialization template key, which no run ever
        // instantiates under that name, so routing there would block the run on Configuration instead of re-attempting
        // anything. Pinned here because the seed is the one definition an operator does not author.
        AssertEx.Equal("verify", graph.Nodes["fullvalidate"].RetryTarget);
        AssertEx.False(graph.TemplateKeys.Contains("verify"), "a retry target has to be a node the run actually seeds.");
        AssertEx.Equal("fullvalidate, integrate, integrationapproval, verify",
            string.Join(", ", graph.Descendants("verify").Append("verify").OrderBy(static key => key, StringComparer.Ordinal)),
            "what a failure of the integrated result re-attempts: the verification and everything that acted on it.");

        // The gate in front of the apply carries the approval and nothing else, asked the way the tick asks it. Parsing
        // at all already proves this — the rule is a parse rule — but the shipped template is the one definition an
        // operator does not author, so what it routes on is pinned here rather than left implied by the absence of a throw.
        // N1: the verification's second inbound edge is what reaches PAST the decomposition to the approved plan — the
        // walk stops at the first producer on each path, and on the join's path that is `decompose` with its task
        // package. Pinned on the SHIPPED seed rather than on a test graph, because deleting the line is otherwise a
        // change no test notices and the verification goes blind again.
        var toVerify = graph.InboundEdges("verify").Single(static edge => edge.From == "planapproval");
        AssertEx.True(DevWorkflowStateMachine.GateEdgeFires(toVerify, DevWorkflowDecisionKind.Approve),
            "an approved plan is what the verification is asked to judge the slices against.");
        AssertEx.False(DevWorkflowStateMachine.GateEdgeFires(toVerify, DevWorkflowDecisionKind.Reject),
            "and a declined plan kills BOTH paths into the verification rather than half-feeding it.");
        AssertEx.False(DevWorkflowStateMachine.GateEdgeFires(toVerify, DevWorkflowDecisionKind.RequestChanges));

        var approval = graph.InboundEdges("integrate").Single();
        AssertEx.True(DevWorkflowStateMachine.GateEdgeFires(approval, DevWorkflowDecisionKind.Approve));
        AssertEx.False(DevWorkflowStateMachine.GateEdgeFires(approval, DevWorkflowDecisionKind.Reject), "a refused integration applies nothing.");
        AssertEx.False(DevWorkflowStateMachine.GateEdgeFires(approval, DevWorkflowDecisionKind.RequestChanges));
    }

    /// <summary>
    ///     An apply node integrates ITS OWN gate's work, not the run's.
    ///     <para>
    ///         With two gated apply lanes in one run, a run-wide enumeration hands each gate every succeeded DevTask
    ///         task there is — so approving alpha lands beta's patch, which alpha's gate never displayed and nobody
    ///         approved. The gate the graph puts in front of an apply node (Y3) covers the branch that REACHES that
    ///         node, so the enumeration is the node's graph ancestry.
    ///     </para>
    ///     <para>
    ///         Driven through the production path rather than the dispatcher: what is under test is which tasks the
    ///         pass enumerates, and standing the rows in the state a gate-approved run leaves them in is exactly what
    ///         the dispatcher would have done to get here.
    ///     </para>
    /// </summary>
    [Test]
    public async Task EachGatedApplyLaneAppliesItsOwnBranchAndLeavesTheOtherAlone()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        var (projectId, _) = await harness.SeedDevelopmentProjectAsync().ConfigureAwait(false);
        var alpha = await AwaitingApplyTaskAsync(harness, projectId, "Alpha slice").ConfigureAwait(false);
        var beta = await AwaitingApplyTaskAsync(harness, projectId, "Beta slice").ConfigureAwait(false);

        var runId = await harness.StartRunAsync(TwoGatedApplyLanes, "Add both features.", projectId).ConfigureAwait(false);
        await ImplementedAsync(harness, runId, "alphaimplement", alpha).ConfigureAwait(false);
        await ImplementedAsync(harness, runId, "betaimplement", beta).ConfigureAwait(false);

        // Beta's gate first, and beta's lane must not reach for alpha — which is the ordering that catches a run-wide
        // enumeration even when it stops at its first refusal, because alpha sorts ahead of beta by node key.
        var betaReport = await ApplyAsync(harness, runId, "betaapply").ConfigureAwait(false);
        AssertEx.Equal($"{beta:N}", string.Join(", ", harness.Chain.Offered.Select(static taskId => taskId.ToString("N"))));
        AssertEx.Contains(betaReport, beta.ToString("D"));
        AssertEx.False(betaReport.Contains(alpha.ToString("D"), StringComparison.Ordinal),
            $"beta's gate approved beta's work, and its report may not name a task from a branch it never showed: {betaReport}");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply,
            (await harness.ReadDevelopmentTaskAsync(alpha).ConfigureAwait(false)).Status,
            "and alpha's patch is still waiting for alpha's own gate.");

        var alphaReport = await ApplyAsync(harness, runId, "alphaapply").ConfigureAwait(false);
        AssertEx.Equal($"{beta:N}, {alpha:N}", string.Join(", ", harness.Chain.Offered.Select(static taskId => taskId.ToString("N"))));
        AssertEx.Contains(alphaReport, alpha.ToString("D"));
        AssertEx.False(alphaReport.Contains(beta.ToString("D"), StringComparison.Ordinal),
            $"and alpha's lane does not re-offer beta's task either, already-applied or not: {alphaReport}");
    }

    /// <summary>
    ///     Two mutually exclusive branches that each carry their OWN validation, converging on an <c>Any</c> join ahead
    ///     of the gate and the apply — the shape <c>GRAPH-C4-3</c>'s structural half demands of an <c>Any</c>
    ///     convergence, since only one branch may have run.
    ///     <para>
    ///         Driven down one branch, the other branch's validation lands <c>Skipped</c>, and the apply must run
    ///         anyway: the runtime pre-check asks about the SATISFIED provenance, so a row belonging to work that was
    ///         correctly not done is not in it. A rule that asked "did every validate ancestor succeed" blocks here,
    ///         on a branch the run was right to skip.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AnApplyBehindAnAnyJoinRunsOnTheBranchThatWasTaken()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        var (projectId, _) = await harness.SeedDevelopmentProjectAsync().ConfigureAwait(false);
        harness.Tools.Answer("alphacheck", FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(TwoValidatedBranchesIntoAnAnyJoin, "Add the feature.", projectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "route", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "approval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Skipped,
            (await harness.ReadNodeRunAsync(runId, "betacheck").ConfigureAwait(false)).Status,
            "the branch the gate did not take is skipped, which is what makes this the case that matters.");
        var integrate = await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false);
        AssertEx.NotEqual(DevWorkflowNodeRunStatus.Pending, integrate.Status, "the apply was dispatched at all, or this asserts nothing about the pre-check.");
        AssertEx.False((integrate.TerminalReason ?? string.Empty).Contains("GRAPH-C4-3", StringComparison.Ordinal),
            $"the branch that ran carried its own validation, and that is the only one the apply is judged on: {integrate.FailureClass} — {integrate.TerminalReason}");
    }

    /// <summary>A second task on the project, walked up the scripted chain until its patch is waiting to be applied.</summary>
    private static async Task<Guid> AwaitingApplyTaskAsync(DevWorkflowHarness harness, Guid projectId, string title)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var created = await store.CreateTaskAsync(new DevelopmentCreateTaskCommand(projectId,
                                     Guid.NewGuid(),
                                     Guid.NewGuid(),
                                     title,
                                     "It has to do the thing.",
                                     "[\"it does the thing\"]"))
                                 .ConfigureAwait(false);
        var taskId = created.TaskId ?? throw new AssertionException("The create answered without naming the task it created.");
        while ((await store.GetTaskAsync(taskId).ConfigureAwait(false)).Status != DevelopmentTaskStatus.AwaitingApply)
        {
            _ = await harness.Chain.StartNextActionAsync(projectId, taskId, Guid.NewGuid()).ConfigureAwait(false);
        }

        return taskId;
    }

    /// <summary>Stands a DevTask node run where a succeeded implementation leaves it: succeeded, naming its task.</summary>
    private static async Task ImplementedAsync(DevWorkflowHarness harness, Guid runId, string nodeKey, Guid taskId)
    {
        var nodeRun = await harness.ReadNodeRunAsync(runId, nodeKey).ConfigureAwait(false);
        await using var scope = harness.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                       .TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(runId,
                           nodeRun.Id,
                           DevWorkflowVersions.Any,
                           DevWorkflowNodeRunStatus.Succeeded,
                           DevelopmentTaskId: taskId))
                       .ConfigureAwait(false);
    }

    /// <summary>
    ///     The shipped fix loop, driven on the shipped SHAPE: a materialized DevTask subtree behind the join, so
    ///     <c>implement</c> is a template key here exactly as it is in the seed. A failed full validation re-attempts
    ///     the verification rather than blocking the run, and the verification it replaces was consumed — by the gate as
    ///     its evidence and by the apply node — so the apply report and the full check's own report are flagged.
    ///     <para>
    ///         The shape is the point. Routed to <c>implement</c> this run would have blocked on Configuration at the
    ///         first failure, because run seeding never writes a node run under a template key; a synthetic graph whose
    ///         <c>implement</c> is an ordinary node cannot show that.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AFailedFullValidationReAttemptsTheVerificationAndFlagsWhatConsumedIt()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        harness.Tools.Answer("fullvalidate", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Passing());

        var (projectId, _) = await harness.SeedDevelopmentProjectAsync().ConfigureAwait(false);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ShippedTailFixLoop, "Add the feature.", projectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", TwoIndependentTasks).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        // Round one: the verification lands, the gate approves it, the patches apply, the full check fails.
        _ = await harness.SaveAgentArtifactAsync(runId, "verify", "verification.md", "round one").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "verify").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var routed = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Single(static entry => entry.EventType == "node.retry.routed");
        AssertEx.Contains(AssertEx.NotNull(routed.DetailJson), "\"to\":\"verify\"");
        var fullvalidate = await harness.ReadNodeRunAsync(runId, "fullvalidate").ConfigureAwait(false);
        AssertEx.NotEqual(DevWorkflowNodeRunStatus.Blocked,
            fullvalidate.Status,
            $"the route found its target instead of blocking the run: {fullvalidate.FailureClass} {fullvalidate.TerminalReason}");
        AssertEx.Equal(expected: 2, (await harness.ReadNodeRunAsync(runId, "verify").ConfigureAwait(false)).Attempt);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending,
            (await harness.ReadNodeRunAsync(runId, "integrationapproval").ConfigureAwait(false)).Status,
            "the gate approved a verification that is being replaced, so it is asked again.");
        AssertEx.Equal(DevelopmentTaskStatus.Completed,
            (await harness.ReadDevelopmentTaskAsync(await TaskIdAsync(harness, runId, "implement#alpha").ConfigureAwait(false)).ConfigureAwait(false)).Status,
            "and the implementations are NOT reset: nothing re-opens a task whose patch is already in the repository.");

        // Round two: the new verification supersedes the one the gate and the apply node consumed.
        _ = await harness.SaveAgentArtifactAsync(runId, "verify", "verification.md", "round two").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "verify").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var artifacts = await harness.ReadArtifactsAsync(runId).ConfigureAwait(false);
        var verifications = artifacts.Where(static artifact => artifact.Name == "verification.md").OrderBy(static artifact => artifact.Version).ToList();
        AssertEx.Equal(expected: 2, verifications.Count, "the re-run versioned the same lineage rather than starting a new one.");

        var applyReport = artifacts.Where(artifact => artifact.ProducingNodeKey == "integrate").OrderBy(static artifact => artifact.Version).First();
        AssertEx.True(applyReport.IsStale, "the apply node consumed the verification that has since been replaced.");
        AssertEx.Equal(verifications[1].Id, applyReport.StaleBecauseArtifactId, "and the row names the version that replaced it.");
        AssertEx.Equal("superseded-input", applyReport.StaleReason);

        var fullReport = artifacts.Where(artifact => artifact.ProducingNodeKey == "fullvalidate").OrderBy(static artifact => artifact.Version).First();
        AssertEx.True(fullReport.IsStale, "and the full check's own first report was written from an apply report that has since been replaced.");

        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The route survives the host dying the instant after it. It commits as ONE transaction, so the restart finds
    ///     the whole subtree under the re-run node uniformly <c>Pending</c> — never the failed check back at
    ///     <c>Pending</c> while the verification, the answered gate and the executed apply beside it still read
    ///     <c>Succeeded</c>. Startup recovery reconciles neither shape: it only judges rows left <c>Queued</c> or
    ///     <c>Running</c>, so a lone <c>Pending</c> row under succeeded ancestors is re-dispatched as if fresh and the
    ///     run completes on evidence and an approval about an implementation that no longer exists.
    /// </summary>
    [Test]
    public async Task ARoutedRetryThatCommitted_SurvivesARestartWithItsWholeSubtreeReset()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        harness.Tools.Answer("fullvalidate", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Passing());

        var (projectId, _) = await harness.SeedDevelopmentProjectAsync().ConfigureAwait(false);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.ShippedTailFixLoop, "Add the feature.", projectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", TwoIndependentTasks).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        // Round one, through the gate and the apply, to the full check that fails and routes back to the verification.
        _ = await harness.SaveAgentArtifactAsync(runId, "verify", "verification.md", "round one").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "verify").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        await harness.RestartAsync().ConfigureAwait(false);

        foreach (var nodeKey in new[] { "verify", "integrationapproval", "integrate", "fullvalidate" })
        {
            AssertEx.Equal(DevWorkflowNodeRunStatus.Pending,
                (await harness.ReadNodeRunAsync(runId, nodeKey).ConfigureAwait(false)).Status,
                $"'{nodeKey}' is under the node being re-run, so a restart must find it reset with the rest of the subtree.");
        }

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "verify", "verification.md", "round two").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "verify").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 2,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "gate.decided"),
            "the gate was asked again after the route, so the approval the run completed on is about the verification it finished with.");
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A seeded row nobody has touched follows the template this build ships. Insert-if-absent alone left every
    ///     installation that already had the slug on the graph it was first seeded with, so a template fix reached only
    ///     brand-new databases — and the run started before the upgrade still renders from the graph IT pinned, which is
    ///     what makes rewriting the definition safe at all.
    /// </summary>
    [Test]
    public async Task AnUntouchedSeededDefinitionIsBroughtUpToTheShippedTemplate()
    {
        await using var harness = new DevWorkflowHarness();
        var (definitionId, runId) = await PlantOldSeedAndRunAsync(harness, DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug).ConfigureAwait(false);

        // Renamed first, by a name-only PUT: the label is the operator's, the graph is still one of ours, so the row
        // still qualifies for the catch-up — and the catch-up must not take the name back.
        await using (var renaming = harness.Services.CreateAsyncScope())
        {
            _ = await renaming.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                              .UpdateDefinitionAsync(new UpdateDevWorkflowDefinitionCommand(definitionId, ExpectedVersion: 1, "The team's feature flow"))
                              .ConfigureAwait(false);
        }

        await SeedAsync(harness).ConfigureAwait(false);

        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
        var definition = await store.GetDefinitionAsync(definitionId).ConfigureAwait(false);
        AssertEx.Equal(expected: 3, definition.Version, "the upgrade goes through the same update the definition PUT uses, so it versions the row.");
        AssertEx.Equal("The team's feature flow", definition.Name, "the graph is ours to fix; the name is the operator's to keep.");
        AssertEx.Equal(DevWorkflowDefinitionSource.Seeded, definition.Source, "and it is still the seeded row, not a replacement.");
        AssertEx.Equal("verify",
            DevWorkflowGraph.Parse(definition.GraphJson).Nodes["fullvalidate"].RetryTarget,
            "and the row now carries the retry edge the shipped template declares.");
        AssertEx.Equal(expected: 1,
            (await store.ListDefinitionsAsync(includeArchived: true).ConfigureAwait(false))
            .Count(entry => string.Equals(entry.SeedSlug, DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug, StringComparison.Ordinal)),
            "one row for the slug, rewritten rather than duplicated.");

        AssertEx.Equal(OldSeedGraph,
            (await store.GetRunAsync(runId).ConfigureAwait(false)).GraphJson,
            "the run pinned its graph at start, so rewriting the definition underneath it changes nothing about what it is running.");
    }

    /// <summary>
    ///     The catch-up is repeatable, which is the whole reason the signal is the row's CONTENT rather than its
    ///     version: the seeder's own upgrade writes a version, so a version-based rule would buy one catch-up per
    ///     installation and read every later template change as an operator's edit.
    /// </summary>
    [Test]
    public async Task ARowAlreadyUpgradedOnceIsUpgradedAgainByTheNextRevision()
    {
        await using var harness = new DevWorkflowHarness();
        var (definitionId, _) = await PlantOldSeedAndRunAsync(harness, DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug).ConfigureAwait(false);

        await SeedAsync(harness).ConfigureAwait(false);

        // Back to a prior revision at version 2 — the state a previous release's catch-up leaves, which a version-based
        // rule would refuse to touch ever again.
        await using (var rewinding = harness.Services.CreateAsyncScope())
        {
            var store = rewinding.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
            var current = await store.GetDefinitionAsync(definitionId).ConfigureAwait(false);
            _ = await store.UpdateDefinitionAsync(new UpdateDevWorkflowDefinitionCommand(definitionId, current.Version, GraphJson: OldSeedGraph, NodeCount: 11))
                           .ConfigureAwait(false);
        }

        await SeedAsync(harness).ConfigureAwait(false);

        await using var scope = harness.Services.CreateAsyncScope();
        var definition = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().GetDefinitionAsync(definitionId).ConfigureAwait(false);
        AssertEx.Equal("verify",
            DevWorkflowGraph.Parse(definition.GraphJson).Nodes["fullvalidate"].RetryTarget,
            "a row holding a revision this build knows it shipped follows the shipped template, whatever its version says.");
        AssertEx.Equal(expected: 4, definition.Version);
    }

    /// <summary>
    ///     And a row an operator has edited is never rewritten, however far it has drifted from the shipped template:
    ///     the edit is the answer, and a startup that silently reverted it would be the one bug this whole path must
    ///     not have. Untouched means the version a create wrote, since the definition PUT is the only thing that moves
    ///     it.
    /// </summary>
    [Test]
    public async Task AnOperatorEditedSeededDefinitionIsLeftExactlyAsItWas()
    {
        await using var harness = new DevWorkflowHarness();
        var (definitionId, _) = await PlantOldSeedAndRunAsync(harness, DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug).ConfigureAwait(false);

        const string Edited = """
                              {
                                "schemaVersion": 1,
                                "nodes": [{ "nodeKey": "approve", "nodeType": "HumanGate", "label": "Approve" }],
                                "edges": []
                              }
                              """;

        await using (var editing = harness.Services.CreateAsyncScope())
        {
            _ = await editing.ServiceProvider.GetRequiredService<IDevWorkflowStore>()
                             .UpdateDefinitionAsync(new UpdateDevWorkflowDefinitionCommand(definitionId, ExpectedVersion: 1, "The operator's own", Edited, NodeCount: 1))
                             .ConfigureAwait(false);
        }

        await SeedAsync(harness).ConfigureAwait(false);

        // A scope of its OWN: the one that wrote the row still tracks it, and would answer this from memory whether or
        // not the seeding had rewritten the database underneath it.
        await using var scope = harness.Services.CreateAsyncScope();
        var definition = await scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>().GetDefinitionAsync(definitionId).ConfigureAwait(false);
        AssertEx.Equal(Edited, definition.GraphJson, "an edited template is the operator's answer, not a row to revert.");
        AssertEx.Equal(expected: 2, definition.Version, "and nothing wrote it again.");
    }

    /// <summary>
    ///     What a previous build actually shipped, taken from the seeder's own kept revision rather than written out
    ///     again here: a hand-copied stand-in would hash to something no installation has and the upgrade would be
    ///     tested against a case that cannot occur.
    /// </summary>
    private const string OldSeedGraph = DevWorkflowDefinitionSeeder.FeatureDevelopmentGraphRevision1;

    /// <summary>A seeded row at the version a create wrote, and a run already pinned to the graph it holds.</summary>
    private static async Task<(Guid DefinitionId, Guid RunId)> PlantOldSeedAndRunAsync(DevWorkflowHarness harness, string seedSlug)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
        var definition = await store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand(Guid.NewGuid(),
                                        "An older Feature Development v1",
                                        OldSeedGraph,
                                        NodeCount: 11,
                                        DevWorkflowDefinitionSource.Seeded,
                                        seedSlug))
                                    .ConfigureAwait(false);
        AssertEx.Equal(expected: 1, definition.Version, "the signal this whole path reads: a create writes version 1.");

        var workItem = await store.CreateWorkItemAsync(new CreateDevWorkflowWorkItemCommand(Guid.NewGuid(), "Seeded work item", "Explain the inference path.")).ConfigureAwait(false);
        var run = await store.StartRunAsync(new StartDevWorkflowRunCommand(Guid.NewGuid(),
                                 workItem.Id,
                                 definition.Id,
                                 definition.Version,
                                 definition.GraphHash,
                                 definition.GraphJson))
                             .ConfigureAwait(false);
        return (definition.Id, run.Id);
    }

    /// <summary>Runs one apply node through the production pass and answers with its report.</summary>
    private static async Task<string> ApplyAsync(DevWorkflowHarness harness, Guid runId, string nodeKey)
    {
        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        var nodeRun = await harness.ReadNodeRunAsync(runId, nodeKey).ConfigureAwait(false);
        await using var scope = harness.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<DevWorkflowApplyCommands>()
                                .RunAsync(run, nodeRun, CancellationToken.None)
                                .ConfigureAwait(false);
        AssertEx.True(result.Passed, $"the lane's own patch had to land: {result.SanitizedReason}");
        return Encoding.UTF8.GetString(result.Report.Span);
    }

    private static Task SeedAsync(DevWorkflowHarness harness) =>
        new DevWorkflowDefinitionSeeder(harness.Services.GetRequiredService<IServiceScopeFactory>(),
                harness.Services.GetRequiredService<IOptions<DevWorkflowOptions>>(),
                harness.Services.GetRequiredService<ILogger<DevWorkflowDefinitionSeeder>>())
            .StartAsync(CancellationToken.None);

    /// <summary>
    ///     A run decomposed into two slices, both implemented and validated, waiting at the integration gate — the state
    ///     every test here starts from.
    /// </summary>
    private static async Task<(Guid RunId, Guid ProjectId)> ImplementTwoSlicesAsync(DevWorkflowHarness harness)
    {
        var (projectId, _) = await harness.SeedDevelopmentProjectAsync().ConfigureAwait(false);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.DecompositionIntoDevTasksAndIntegration, "Add the feature.", projectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", TwoIndependentTasks).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        return (runId, projectId);
    }

    private static async Task<Guid> TaskIdAsync(DevWorkflowHarness harness, Guid runId, string nodeKey)
    {
        var nodeRun = await harness.ReadNodeRunAsync(runId, nodeKey).ConfigureAwait(false);
        return nodeRun.DevelopmentTaskId ?? throw new AssertionException($"Node run '{nodeKey}' names no development task, so it implemented nothing to apply.");
    }

    /// <summary>The apply node's own report, which is a different document under a different kind than a validation one.</summary>
    private static async Task<string> ReadApplyReportAsync(DevWorkflowHarness harness, Guid runId)
    {
        var artifacts = await harness.ReadArtifactsAsync(runId).ConfigureAwait(false);
        var report = artifacts.SingleOrDefault(artifact => string.Equals(artifact.Name, "integrate-apply.json", StringComparison.Ordinal))
                     ?? throw new AssertionException($"Run {runId} has no apply report: {string.Join(", ", artifacts.Select(static artifact => artifact.Name))}");
        AssertEx.Equal(DevWorkflowArtifactKind.Report,
            report.Kind,
            "an apply report is not a validation report, and a reader that decoded it as one would call it unreadable evidence.");
        return await harness.ReadArtifactTextAsync(runId, report).ConfigureAwait(false);
    }
}
