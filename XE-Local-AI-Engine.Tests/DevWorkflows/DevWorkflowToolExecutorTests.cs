namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The sandbox lane over real rows: what a tool node-run costs, what it leaves behind, and what happens to it when
///     the run is asked to stop.
///     <para>
///         Every test here takes a host of its OWN. The scripted sandbox is a container singleton keyed by node key, and
///         these fixtures deliberately share node keys, so a shared host would let one test's script answer another's
///         node run.
///     </para>
/// </summary>
public sealed class DevWorkflowToolExecutorTests
{
    /// <summary>A project id on the work item, because a graph with tool nodes in it is only startable with one.</summary>
    private static readonly Guid DevelopmentProjectId = Guid.NewGuid();

    /// <summary>One tool node that asks for a second before it tries again — the shortest delay the field can express.</summary>
    private const string DelayedRetryToolGraph = """
                                                 {
                                                   "schemaVersion": 1,
                                                   "nodes": [{ "nodeKey": "validate", "nodeType": "Tool", "retryDelaySeconds": 1 }],
                                                   "edges": []
                                                 }
                                                 """;

    /// <summary>
    ///     The lane hands out no more slots than it has, and the node run that missed one says WHY it is waiting rather
    ///     than sitting in an unexplained queue.
    /// </summary>
    [Test]
    public async Task TheSandboxLaneQueuesTheNodeRunItHasNoSlotFor()
    {
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxParallelToolNodes", "1"));
        var held = harness.Tools.Hold("first");
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.TwoParallelTools, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await held.Started.ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "first").ConfigureAwait(false)).Status);
        var second = await harness.ReadNodeRunAsync(runId, "second").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Queued, second.Status, "the lane has one slot and the first node run is holding it.");
        AssertEx.Equal("awaiting-sandbox-slot", second.QueueReason);
        AssertEx.Equal(expected: 1, harness.Tools.Ran.Count, "a node run without a slot must not have started its commands.");
        AssertEx.Null(second.FailureClass, "a full lane is queueing, not failure.");

        held.Release();
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status, "and the slot the first gave back ran the second.");
        AssertEx.Equal("first, second", string.Join(", ", harness.Tools.Ran));
    }

    /// <summary>
    ///     The Slice B1 shape without the sandbox: a passing tool node succeeds, keeps its report as a run artifact, and
    ///     writes the output document a conditional edge routes on.
    /// </summary>
    [Test]
    public async Task APassingToolNodeSucceedsAndKeepsItsReport()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, nodeRun.Status);
        AssertEx.Null(nodeRun.FailureClass);
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        var output = AssertEx.NotNull(nodeRun.OutputJson);
        AssertEx.Contains(output, "\"passed\":true");
        AssertEx.Contains(output, "\"status\":\"succeeded\"");
        AssertEx.Contains(output, "\"testsPassed\":12");

        var artifact = (await harness.ReadArtifactsAsync(runId).ConfigureAwait(false)).Single();
        AssertEx.Equal(DevWorkflowArtifactKind.ValidationReport, artifact.Kind);
        AssertEx.Equal("validate-validation.json", artifact.Name);
        AssertEx.Equal("application/json", artifact.MediaType);
        AssertEx.Equal("""{"passed":true}""",
            await harness.ReadArtifactTextAsync(runId, artifact).ConfigureAwait(false),
            "the report the substrate produced is the report the run keeps.");
    }

    /// <summary>
    ///     A failing verdict is a RESULT, not an error: it is retryable, so the node tries again until its own cap is
    ///     spent and only then asks a human — and every attempt's report survives, because the reports ARE the evidence
    ///     the fix loop and the operator both read.
    /// </summary>
    [Test]
    public async Task AFailingVerdictRetriesToItsCapAndKeepsEveryAttemptsReport()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("validate", FakeDevWorkflowToolCommands.Failing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, nodeRun.Status, "an exhausted node asks a human rather than failing the run behind its back.");
        AssertEx.Equal(expected: 3, nodeRun.Attempt, "three attempts is what the node allows, and all three were spent.");
        AssertEx.Equal("ToolCommandFailed", nodeRun.FailureClass, "commands that ran and reported failure are the fix loop's fuel, not an error.");
        AssertEx.Contains(AssertEx.NotNull(nodeRun.TerminalReason), "3 failing");
        AssertEx.Contains(AssertEx.NotNull(nodeRun.TerminalReason), "as many attempts as this node allows");
        AssertEx.Equal(DevWorkflowDecisionKind.Abandon, nodeRun.PendingDecisionKind, "a blocked row names the answer it is waiting for.");

        var output = AssertEx.NotNull(nodeRun.OutputJson);
        AssertEx.Contains(output, "\"passed\":false");
        AssertEx.Contains(output, "\"failureCode\":\"tests_failed\"");
        AssertEx.Contains(output, "\"testsFailed\":3");

        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);

        var reports = await harness.ReadArtifactsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 3, reports.Count, "each attempt keeps its own report; that IS the per-attempt evidence.");
        AssertEx.Equal(expected: 1, reports.Count(static report => report.IsLatest), "all three are versions of one lineage, so exactly one is current.");
    }

    /// <summary>
    ///     The B3 gate on the sandbox lane: a retryable verdict is tried again, and the second attempt passes. It also
    ///     pins the entry condition that made a Tool retry unsafe until now — the second attempt prepares a workspace of
    ///     its OWN, because the provider reuses a preserved worktree and the base commit recorded in its manifest, so an
    ///     identity constant across attempts would re-validate attempt one's commit in attempt one's tree.
    /// </summary>
    [Test]
    public async Task AToolNodeRetriesAVerdictAndItsSecondAttemptPreparesItsOwnWorkspace()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("validate", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, nodeRun.Status, "the second attempt passed, so the node did.");
        AssertEx.Equal(expected: 2, nodeRun.Attempt);
        AssertEx.Null(nodeRun.FailureClass, "a re-attempt that succeeded must not still report the failure it retried.");
        AssertEx.Equal("validate, validate", string.Join(", ", harness.Tools.Ran), "the commands really did run twice.");
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        var trail = await harness.ReadEventTrailAsync(runId).ConfigureAwait(false);
        AssertEx.Contains(trail, "node.retry.scheduled");

        // The two attempts' workspaces, as the lane would ask the provider for them.
        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        var first = nodeRun with
        {
            Attempt = 1
        };
        var project = Project(DevelopmentProjectId);
        var binding = new DevelopmentRepositoryBinding(DevelopmentProjectId, project.SelectedFolderId!.Value, "repo", "/tmp/repo", project.RepositoryIdentityHash);
        var node = DevWorkflowGraph.Parse(DevWorkflowGraphs.SingleTool).Nodes["validate"];

        var attemptOne = DevWorkflowToolCommands.Synthesize(project, node, run, first, binding);
        var attemptTwo = DevWorkflowToolCommands.Synthesize(project, node, run, nodeRun, binding);
        AssertEx.NotEqual(attemptOne.TaskId, attemptTwo.TaskId, "the workspace a retry prepares is not the one the attempt before it left behind.");
        AssertEx.NotEqual(attemptOne.AttemptId, attemptTwo.AttemptId);
        AssertEx.NotEqual(nodeRun.Id, attemptTwo.TaskId, "the node-run id is constant across attempts, which is exactly what it must not key.");
        AssertEx.Equal(attemptOne.TaskId,
            DevWorkflowToolCommands.Synthesize(project, node, run, first, binding).TaskId,
            "and it is derived rather than minted, so a replayed poll re-prepares the workspace its attempt already has.");
    }

    /// <summary>
    ///     Automatic re-attempts and an operator's <c>Retry</c> spend ONE budget, because they are the same thing: a
    ///     re-attempt of this run. So the run's own retrying is what exhausts it, and the person who then tries to
    ///     override is told the run has nothing left rather than being handed an attempt the budget never had.
    /// </summary>
    [Test]
    public async Task TheRunWideBudgetBoundsTheRuntimesOwnRetriesAndTheOperatorsAlike()
    {
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxTotalAttempts", "1"));
        harness.Tools.Answer("validate", FakeDevWorkflowToolCommands.Failing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, nodeRun.Status);
        AssertEx.Equal(expected: 2, nodeRun.Attempt, "one re-attempt is what the run allowed, and the node's own cap of three never came into it.");
        AssertEx.Equal(DevWorkflowFailureClasses.BudgetExhausted, nodeRun.FailureClass, "the run ran out of re-attempts, which is a different fact from the verdict.");
        AssertEx.Equal("validate, validate", string.Join(", ", harness.Tools.Ran));

        var refused = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() => harness.WithRunServiceAsync(service => service.DecideAsync(runId,
                              nodeRun.Id,
                              Guid.NewGuid(),
                              DevWorkflowDecisionKind.Retry,
                              comment: null,
                              payloadJson: null,
                              "operator")))
                          .ConfigureAwait(false);
        AssertEx.Contains(refused.Message, "as many re-attempts as this run", message: "the automatic retry already spent what the operator is asking for.");
    }

    /// <summary>
    ///     A node may ask for a pause before it tries again, and the pause is honoured: the re-attempt is scheduled
    ///     immediately — the row is <c>Pending</c> and the log says when it may go — but nothing admits it until then.
    /// </summary>
    [Test]
    public async Task ANodeThatAsksForARetryDelayIsNotReAdmittedUntilItHasPassed()
    {
        // A clock this test moves, so the wait costs a method call rather than a real second.
        var clock = new ManualTimeProvider();
        await using var harness = new DevWorkflowHarness(services => services.AddSingleton<TimeProvider>(clock));
        harness.Tools.Answer("validate", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(DelayedRetryToolGraph, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var waiting = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, waiting.Status, "the re-attempt is scheduled; what it is waiting for is a clock, not a slot.");
        AssertEx.Equal(expected: 2, waiting.Attempt);
        AssertEx.Null(waiting.QueueReason, "no queue reason names a wait on time, and inventing one would put a token in the row nothing can read.");
        AssertEx.Equal(expected: 1, harness.Tools.Ran.Count, "the second attempt has not started, because its delay has not passed.");

        var scheduled = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Last(static entry => entry.EventType == "node.retry.scheduled");
        AssertEx.Contains(AssertEx.NotNull(scheduled.DetailJson), "\"delayUntil\":", message: "the log says when the re-attempt may go.");

        clock.Advance(TimeSpan.FromSeconds(1));
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false)).Status);
        AssertEx.Equal(expected: 2, harness.Tools.Ran.Count, "and once it had, the same node ran again.");
    }

    /// <summary>
    ///     The two classes no retry can answer stand the node run down for a human instead of failing it, and they leave
    ///     no report — nothing ran, so there is nothing to report but the reason on the row.
    /// </summary>
    [Test]
    [Arguments("Policy", "The attempt changed a dependency manifest.")]
    [Arguments("Configuration", "This repository's command profile does not define 'dotnet_build'.")]
    public async Task ARefusalNoRetryCanAnswerBlocksTheNodeRunForAHuman(string failureClass, string reason)
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("validate", FakeDevWorkflowToolCommands.Refusing(failureClass, reason));
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, nodeRun.Status);
        AssertEx.Equal(failureClass, nodeRun.FailureClass);
        AssertEx.Equal(reason, nodeRun.TerminalReason);
        AssertEx.Equal(DevWorkflowDecisionKind.Abandon, nodeRun.PendingDecisionKind, "a blocked row names the answer it is waiting for.");
        AssertEx.Empty(await harness.ReadArtifactsAsync(runId).ConfigureAwait(false));
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A prepared workspace carrying a committed credential is recorded on the run rather than written where it is
    ///     found — which is the whole reason the secrets seam exists, because that write used to resolve a Dev Mode task
    ///     row a node run does not have.
    /// </summary>
    [Test]
    public async Task ACommittedCredentialInTheWorkspaceIsRecordedOnTheRun()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("validate",
            FakeDevWorkflowToolCommands.Refusing("Policy", "The workspace carries a committed credential.", ".env", "config/secrets.json"));
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var detected = (await harness.ReadEventsAsync(runId).ConfigureAwait(false))
            .Single(static entry => entry.EventType == "workspace.secrets.detected");
        AssertEx.Contains(AssertEx.NotNull(detected.DetailJson), ".env");
        AssertEx.Contains(AssertEx.NotNull(detected.DetailJson), "config/secrets.json");
    }

    /// <summary>
    ///     Cancelling asks the in-flight commands to stop and settles the row off what they came to, not off a guess. The
    ///     run reaches <c>Cancelled</c> only after that row has, which is what stops the lane's slot leaking.
    /// </summary>
    [Test]
    public async Task CancellingStopsInFlightCommandsAndSettlesTheRowBeforeTheRun()
    {
        await using var harness = new DevWorkflowHarness();
        var held = harness.Tools.Hold("validate");
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await held.Started.ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Cancelling).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Cancelled, nodeRun.Status, "the cancel reached the commands, and the poll wrote what they came to.");
        AssertEx.Equal("Cancelled", nodeRun.FailureClass);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        // Two run.cancelled entries by design: the first is the operator's intent, the last is the drain settling it.
        // The row the drain was waiting on has to land BETWEEN them.
        AssertEx.Equal("run.created, node.materialized, run.started, node.queued, node.started, run.cancelled, node.cancelled, run.cancelled",
            await harness.ReadEventTrailAsync(runId).ConfigureAwait(false),
            "the run must not reach its terminal before the row whose lane slot it was holding.");
    }

    /// <summary>
    ///     A pause lets a build finish. It holds no model slot and cannot be resumed halfway, so killing it would throw
    ///     away minutes of work to save seconds — the run simply reads <c>Pausing</c> until the commands land.
    /// </summary>
    [Test]
    public async Task PausingLetsInFlightCommandsFinishRatherThanKillingThem()
    {
        await using var harness = new DevWorkflowHarness();
        var held = harness.Tools.Hold("validate");
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await held.Started.ConfigureAwait(false);

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Pausing).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false)).Status,
            "a pause does not cancel a build; it waits for it.");
        AssertEx.Equal(DevWorkflowRunStatus.Pausing, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        held.Release();
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Paused, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A settle that throws must not cost the result it was settling.
    ///     <para>
    ///         The pass is consumed from the lane's registry only once its settle has COMMITTED. Consuming it first
    ///         would spend a finished build on a write that may fail — an over-budget blob, a lost version race — and
    ///         the next poll, finding no entry, would record "the host stopped" about a pass that had in fact finished
    ///         perfectly: a false <c>Interrupted</c> in the audit log AND the evidence gone.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AThrowWhileSettlingCostsTheRetryAndNotTheResult()
    {
        await using var harness = new DevWorkflowHarness(static services =>
        {
            services.RemoveAll<IDevWorkflowArtifactBlobStore>();
            services.AddSingleton<IDevWorkflowArtifactBlobStore>(static provider =>
                new BlobStoreThatRefusesItsFirstWrite(ActivatorUtilities.CreateInstance<ManagedDevWorkflowArtifactBlobStore>(provider)));
        });

        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        // Two explicit ticks rather than "advance until quiescent": the tick that starts the run and the tick that
        // admits the node. Stopping here is what puts the throw in the settle rather than somewhere a loop swallows it.
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        var dispatched = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, dispatched.Status);
        await harness.ToolLane.WaitForCompletionAsync(dispatched.Id).ConfigureAwait(false);

        // The report write refuses once. In production the loop's own guard logs such a throw and re-signals the run.
        // Here the tick is driven directly, so the throw surfaces where that guard would have caught it.
        _ = await AssertEx.ThrowsAsync<IOException>(() => harness.AdvanceAsync(runId)).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false)).Status,
            "the row is untouched by a settle that did not commit.");

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, nodeRun.Status, "the TRUE outcome, re-derived from the pass the lane still held.");
        AssertEx.Null(nodeRun.FailureClass);
        AssertEx.Equal(expected: 1, harness.Tools.Ran.Count, "and the build was not run a second time to get it.");

        var artifact = (await harness.ReadArtifactsAsync(runId).ConfigureAwait(false)).Single();
        AssertEx.Equal("""{"passed":true}""",
            await harness.ReadArtifactTextAsync(runId, artifact).ConfigureAwait(false),
            "the evidence survived the failed write.");
        AssertEx.False((await harness.ReadEventTrailAsync(runId).ConfigureAwait(false)).Contains("node.interrupted", StringComparison.Ordinal),
            "nothing may claim the host stopped under a pass that finished.");
    }

    /// <summary>
    ///     A drain over a tool row the lane is driving but whose row never caught up still terminalizes.
    ///     <para>
    ///         Outside a drain the next admission repairs such a row. Inside one nothing admits, so before the poll
    ///         learned to take it the drain re-cancelled an already-cancelled pass on every tick and the run sat in
    ///         <c>Cancelling</c> until the host restarted, with the lane slot held.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ADrainSettlesAToolRowTheLaneIsDrivingButTheRowNeverCaughtUpWith()
    {
        await using var harness = new DevWorkflowHarness();
        var held = harness.Tools.Hold("validate");
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await held.Started.ConfigureAwait(false);

        // Stands the row exactly where a failed Queued→Running write leaves it: the slot taken, the registry entry
        // made, the row still Queued. The store does not judge transitions, so it can be put there directly.
        await harness.TransitionNodeRunAsync(runId, "validate", DevWorkflowNodeRunStatus.Queued).ConfigureAwait(false);
        var nodeRunId = (await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false)).Id;
        AssertEx.True(harness.ToolLane.IsInFlight(nodeRunId), "the lane is still driving the pass this row belongs to.");

        await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Cancelling).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Cancelled, (await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status,
            "the drain finished rather than waiting on a row nothing would ever move.");
        AssertEx.False(harness.ToolLane.IsInFlight(nodeRunId), "and the lane slot went back.");
    }

    /// <summary>
    ///     With Development Mode switched off there is no workspace provider, no repository binding and no sandbox, so
    ///     the node cannot run as configured. It says that, rather than surfacing a container failure from inside a
    ///     detached task as an unexplained <c>Internal</c>.
    /// </summary>
    [Test]
    public async Task AToolNodeWithDevelopmentModeOffSaysSoRatherThanFailingInternally()
    {
        // The REAL optional-resolve path: with the seam faked there would be nothing left to resolve optionally.
        await using var harness = DevWorkflowHarness.WithARealSandbox(("Development:Enabled", "false"));
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, nodeRun.Status);
        AssertEx.Equal("Configuration", nodeRun.FailureClass, "no retry turns Development Mode back on.");
        AssertEx.Contains(AssertEx.NotNull(nodeRun.TerminalReason), "Development Mode is switched off on this node");
        AssertEx.Equal(DevWorkflowDecisionKind.Abandon, nodeRun.PendingDecisionKind);
    }

    /// <summary>
    ///     A Development project, for the one assertion that has to call the lane's snapshot synthesis directly. Only the
    ///     identity fields matter to it; the rest are the shape the record demands.
    /// </summary>
    private static DevelopmentProjectSnapshot Project(Guid projectId) =>
        new(projectId,
            "Objective",
            Guid.NewGuid(),
            "identity-hash",
            "main",
            DevelopmentProjectStatus.Active,
            DevelopmentEgressPolicy.LocalOnly,
            CoderModelId: null,
            ReviewerModelId: null,
            MaxTokens: null,
            MaxDurationSeconds: null,
            ConfigurationVersion: 1,
            TrustedRepositoryAcknowledged: true,
            TrustedRepositoryPolicyVersion: 1,
            TrustedRepositoryAcknowledgedAtUtc: 0,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            Version: 1,
            CommandProfileJson: null);

    /// <summary>
    ///     Refuses its first write and then behaves. A decorator over the real store rather than a stub, so everything
    ///     after the refusal — the digest, the size, the round trip — is still the production path.
    /// </summary>
    private sealed class BlobStoreThatRefusesItsFirstWrite(IDevWorkflowArtifactBlobStore inner) : IDevWorkflowArtifactBlobStore
    {
        private int _refused;

        public Task<DevWorkflowArtifactBlobWriteResult> WriteAsync(Guid runId,
            Guid artifactId,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default) =>
            Interlocked.Exchange(ref _refused, value: 1) == 0
                ? throw new IOException("The artifact could not be written this time.")
                : inner.WriteAsync(runId, artifactId, content, cancellationToken);

        public Task<DevWorkflowArtifactBlobReadResult> ReadAsync(Guid runId,
            Guid artifactId,
            string expectedHash,
            long expectedByteCount,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(runId, artifactId, expectedHash, expectedByteCount, cancellationToken);

        public void Delete(Guid runId, Guid artifactId) =>
            inner.Delete(runId, artifactId);

        public void DeleteRun(Guid runId) =>
            inner.DeleteRun(runId);
    }
}
