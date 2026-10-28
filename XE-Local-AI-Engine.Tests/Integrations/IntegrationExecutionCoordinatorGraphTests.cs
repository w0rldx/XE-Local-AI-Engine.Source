namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;
using Harness = IntegrationCoordinatorHarness;

/// <summary>
///     The coordinator's <c>Graph</c> target branch (ADR 0008): the run it starts, the events it maps,
///     the terminal it writes, and the cancel it forwards. Driven through <c>ProcessOneAsync</c> over a scripted run.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class IntegrationExecutionCoordinatorGraphTests
{
    [Test]
    public async Task Run_ForAGraphTarget_StartsTheRunAsTheIntegrationPrincipal_AndNeverTouchesTheAgentPath()
    {
        using var harness = CompletedRun();
        var executionId = harness.SeedAccepted();
        var row = harness.Row(executionId);

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var runs = harness.GraphRuns;
        AssertEx.Equal(expected: 1, runs.StartCount);
        AssertEx.Equal(harness.GraphDefinitionId, runs.StartedDefinitionId);
        AssertEx.Equal(executionId, runs.StartedRequestId, "the execution id is the idempotent request id, so a dispatch retry replays the same run.");
        AssertEx.Equal(Harness.SeedText, runs.StartedInputJson, "the persisted seed turn is the run input, verbatim.");
        var origin = AssertEx.NotNull(runs.StartedOrigin);
        AssertEx.Equal(GraphWorkflowTriggerSource.Integration, origin.Source);
        AssertEx.Equal(row.PrincipalId, origin.PrincipalId);
        AssertEx.Equal(row.KeyPrefix, origin.KeyPrefix);

        var done = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Completed, done.Status);
        AssertEx.Equal(runs.RunId, done.GraphWorkflowRunId);
        AssertEx.Equal(expected: 0, harness.RunCount, "a graph target never calls the invocation runner.");
        AssertEx.False(harness.LeaseRequested, "a graph target takes no invocation lease; the graph dispatcher does, per node.");
        var audit = harness.Executions.Audits.Single();
        AssertEx.Equal(harness.GraphDefinitionId, audit.TargetAgentDefinitionId, "the audit row's target column carries the graph definition.");
        AssertEx.Equal(runs.RunId, audit.InvocationId);
    }

    [Test]
    public async Task Run_ForAGraphTarget_MapsNodeStartAndFinishToToolEvents_AndTheEndOutputToExternalOutput_InOrder()
    {
        using var harness = CompletedRun();
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var events = EventsOf(harness, executionId);
        AssertEx.Equal(string.Join(',',
                IntegrationStreamEventTypes.ExecutionStarted,
                IntegrationStreamEventTypes.ToolStarted,
                IntegrationStreamEventTypes.ToolCompleted,
                IntegrationStreamEventTypes.ToolStarted,
                IntegrationStreamEventTypes.ToolCompleted,
                IntegrationStreamEventTypes.ExternalOutput,
                IntegrationStreamEventTypes.ExecutionCompleted),
            string.Join(',', events.Select(static row => row.EventType)),
            "run.* events are not frames of their own; node.skipped is silent; the End output precedes the terminal.");

        using var started = JsonDocument.Parse(AssertEx.NotNull(events[3].DetailJson));
        AssertEx.Equal("lights", started.RootElement.GetProperty("nodeKey").GetString());
        AssertEx.Equal("Tool", started.RootElement.GetProperty("kind").GetString());
        AssertEx.Equal("Running", started.RootElement.GetProperty("status").GetString());

        using var finished = JsonDocument.Parse(AssertEx.NotNull(events[4].DetailJson));
        AssertEx.Equal("Succeeded", finished.RootElement.GetProperty("status").GetString());
        AssertEx.True(finished.RootElement.GetProperty("ok").GetBoolean());
        AssertEx.False(finished.RootElement.TryGetProperty("output", out _), "a node frame never carries the node's output.");

        using var output = JsonDocument.Parse(AssertEx.NotNull(events[5].DetailJson));
        AssertEx.Equal("application/json", output.RootElement.GetProperty("contentType").GetString());
        AssertEx.Equal("on", output.RootElement.GetProperty("payload").GetProperty("state").GetString());
        AssertEx.Equal(expected: 1, harness.Row(executionId).OutputCount);
    }

    [Test]
    public async Task Run_WhenTheGraphRunFails_TerminalizesFailedWithTheRunsFailureClass_AndDeliversNoOutput()
    {
        using var harness = new Harness();
        harness.TargetGraph();
        var runs = harness.GraphRuns;
        runs.Emit(GraphWorkflowEventTypes.NodeStarted, "lights");
        runs.Emit(GraphWorkflowEventTypes.NodeFailed, "lights");
        runs.Emit(GraphWorkflowEventTypes.RunFailed, nodeKey: null);
        runs.Status = GraphWorkflowRunStatus.Failed;
        runs.FailureClass = GraphWorkflowFailureClass.NodeFailed;
        runs.OutputJson = """{"never":"delivered"}""";
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status);
        AssertEx.Equal(IntegrationFailureCategories.InternalFailure, row.FailureCategory);
        AssertEx.Contains(AssertEx.NotNull(row.FailureSummary), nameof(GraphWorkflowFailureClass.NodeFailed));
        var events = EventsOf(harness, executionId);
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionFailed, events[^1].EventType);
        AssertEx.False(events.Any(static row => row.EventType == IntegrationStreamEventTypes.ExternalOutput));
        using var failed = JsonDocument.Parse(AssertEx.NotNull(events[^2].DetailJson));
        AssertEx.False(failed.RootElement.GetProperty("ok").GetBoolean());
        AssertEx.Equal("Failed", failed.RootElement.GetProperty("status").GetString());
    }

    [Test]
    public async Task Run_WhenTheDefinitionRefusesTheIntegrationSource_FailsBeforeRunningWithTheStructuralReason()
    {
        using var harness = new Harness();
        harness.TargetGraph();
        harness.GraphRuns.StartFailure = new GraphWorkflowRunConflictException("Graph workflow definition 'Lights' does not accept runs started by Integration.");
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status);
        AssertEx.Equal(IntegrationFailureCategories.TriggerUnavailable, row.FailureCategory, "the failure vocabulary is closed; the reason rides the summary.");
        AssertEx.Equal(IntegrationExecutionCoordinator.GraphTriggerSourceRefusedSummary, row.FailureSummary);
        AssertEx.True(AssertEx.NotNull(row.FailureSummary).StartsWith("graph-trigger-source-refused", StringComparison.Ordinal));
        AssertEx.Null(row.GraphWorkflowRunId, "no run was started.");
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionFailed, string.Join(',', EventsOf(harness, executionId).Select(static e => e.EventType)));
    }

    [Test]
    public async Task Run_WhenTheCallerCancels_CancelsTheGraphRun_AndTerminalizesCancelled()
    {
        using var harness = new Harness();
        harness.TargetGraph();
        var runs = harness.GraphRuns;
        runs.Emit(GraphWorkflowEventTypes.NodeStarted, "lights");
        var executionId = harness.SeedAccepted();
        runs.OnPoll = fake =>
        {
            // The cancel endpoint's signal, landing while the run is in flight.
            fake.OnPoll = null;
            _ = harness.Cancellations.Signal(executionId);
        };

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(expected: 1, runs.CancelCount, "the cancel is forwarded to the run exactly once.");
        AssertEx.Equal(IntegrationExecutionStatus.Cancelled, harness.Row(executionId).Status);
        var events = EventsOf(harness, executionId);
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionCancelled, events[^1].EventType);
        using var cancelled = JsonDocument.Parse(AssertEx.NotNull(events[^2].DetailJson));
        AssertEx.Equal("Cancelled", cancelled.RootElement.GetProperty("status").GetString());
    }

    [Test]
    public async Task Run_WhenShutdownInterruptsTheFollow_CancelsTheGraphRunOnce_AndTerminalizesFailedShutdown()
    {
        using var harness = new Harness();
        harness.TargetGraph();
        var runs = harness.GraphRuns;
        runs.Emit(GraphWorkflowEventTypes.NodeStarted, "lights");
        var executionId = harness.SeedAccepted();
        using var stopping = new CancellationTokenSource();
        runs.OnPoll = fake =>
        {
            fake.OnPoll = null;
            stopping.Cancel();
        };

        await harness.Coordinator.ProcessOneAsync(executionId, stopping.Token);

        AssertEx.Equal(expected: 1, runs.CancelCount, "a run the caller is told failed must not keep firing actions.");
        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status);
        AssertEx.Equal(IntegrationFailureCategories.Shutdown, row.FailureCategory);
    }

    [Test]
    public async Task Run_WhenTheFollowThrowsUnexpectedly_CancelsTheGraphRunOnce_AndTerminalizesFailed()
    {
        using var harness = new Harness();
        harness.TargetGraph();
        var runs = harness.GraphRuns;
        var executionId = harness.SeedAccepted();
        runs.OnPoll = _ => throw new InvalidOperationException("The run store is unreachable.");

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(expected: 1, runs.CancelCount);
        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status);
        AssertEx.Equal(IntegrationFailureCategories.InternalFailure, row.FailureCategory);
    }

    [Test]
    public async Task StartAsync_CancelsTheGraphRunOfAnInterruptedExecution_AndStillFailsItAsRestart()
    {
        using var harness = new Harness();
        var runs = harness.GraphRuns;
        var interrupted = harness.SeedAccepted(status: IntegrationExecutionStatus.Running, graphWorkflowRunId: runs.RunId);

        await harness.Coordinator.StartAsync(CancellationToken.None);
        await harness.Coordinator.StopAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, runs.CancelCount, "the reconciler may have resumed the run's nodes; the sweep cancels it.");
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, runs.Status);
        var row = harness.Row(interrupted);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status);
        AssertEx.Equal(IntegrationFailureCategories.Restart, row.FailureCategory);
    }

    [Test]
    public async Task StartAsync_ForAnInterruptedExecutionWithoutAGraphRun_CancelsNothing()
    {
        using var harness = new Harness();
        var interrupted = harness.SeedAccepted(status: IntegrationExecutionStatus.Running);

        await harness.Coordinator.StartAsync(CancellationToken.None);
        await harness.Coordinator.StopAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, harness.GraphRuns.CancelCount);
        AssertEx.Equal(IntegrationFailureCategories.Restart, harness.Row(interrupted).FailureCategory);
    }

    [Test]
    public async Task StartAsync_WhenTheGraphRunIsAlreadyTerminal_TheConflictDoesNotBreakTheSweep()
    {
        using var harness = new Harness();
        var runs = harness.GraphRuns;
        runs.Status = GraphWorkflowRunStatus.Completed;
        var withRun = harness.SeedAccepted(status: IntegrationExecutionStatus.Running, graphWorkflowRunId: runs.RunId);
        var withoutRun = harness.SeedAccepted(status: IntegrationExecutionStatus.Running);

        await harness.Coordinator.StartAsync(CancellationToken.None);
        await harness.Coordinator.StopAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, runs.CancelCount);
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, runs.Status, "a terminal run answers the conflict and stays as it was.");
        AssertEx.Equal(IntegrationFailureCategories.Restart, harness.Row(withRun).FailureCategory);
        AssertEx.Equal(IntegrationFailureCategories.Restart, harness.Row(withoutRun).FailureCategory);
    }

    /// <summary>A run that has already finished by the first poll: Start, a Tool, a skipped branch and End, with an End output.</summary>
    private static Harness CompletedRun()
    {
        var harness = new Harness();
        harness.TargetGraph();
        var runs = harness.GraphRuns;
        runs.Emit(GraphWorkflowEventTypes.RunStarted, nodeKey: null);
        runs.Emit(GraphWorkflowEventTypes.NodeStarted, "start");
        runs.Emit(GraphWorkflowEventTypes.NodeCompleted, "start");
        runs.Emit(GraphWorkflowEventTypes.NodeSkipped, "end");
        runs.Emit(GraphWorkflowEventTypes.NodeStarted, "lights");
        runs.Emit(GraphWorkflowEventTypes.NodeCompleted, "lights");
        runs.Emit(GraphWorkflowEventTypes.RunCompleted, nodeKey: null);
        runs.Status = GraphWorkflowRunStatus.Completed;
        runs.OutputJson = """{"state":"on"}""";
        return harness;
    }

    private static IntegrationExecutionEventSnapshot[] EventsOf(Harness harness, Guid executionId) =>
        [.. harness.Executions.Events.Where(row => row.ExecutionId == executionId).OrderBy(static row => row.Sequence)];
}
