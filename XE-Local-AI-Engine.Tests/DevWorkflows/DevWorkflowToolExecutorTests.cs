namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
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
    ///     A failing verdict is a RESULT, not an error: the node run fails with the class the fix loop reads, and its
    ///     report survives, because the report is the evidence a retry would be based on.
    /// </summary>
    [Test]
    public async Task AFailingVerdictFailsTheNodeRunAndStillKeepsItsReport()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("validate", FakeDevWorkflowToolCommands.Failing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.SingleTool, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Failed, nodeRun.Status);
        AssertEx.Equal("ToolCommandFailed", nodeRun.FailureClass, "commands that ran and reported failure are the fix loop's fuel, not an error.");
        AssertEx.Contains(AssertEx.NotNull(nodeRun.TerminalReason), "3 failing");

        var output = AssertEx.NotNull(nodeRun.OutputJson);
        AssertEx.Contains(output, "\"passed\":false");
        AssertEx.Contains(output, "\"failureCode\":\"tests_failed\"");
        AssertEx.Contains(output, "\"testsFailed\":3");

        AssertEx.Equal(DevWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowWorkItemStatus.Blocked, (await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(expected: 1, (await harness.ReadArtifactsAsync(runId).ConfigureAwait(false)).Count, "a failed validation keeps its report; that IS the evidence.");
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
}
