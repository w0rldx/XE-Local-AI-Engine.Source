namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The agent lane, over the real store, the real package builder and the real one-slot invocation dispatcher, with
///     <see cref="FakeGraphWorkflowInvocation" /> standing in for the runner and nothing else.
///     <para>
///         Every test pins its own instructions text, which is what the fake scripts on and what keeps a shared host's
///         tests out of each other's way. A test whose turn PARKS takes a host of its own instead: a parked turn holds
///         the node-wide invocation slot, and on a shared host that is every sibling's slot too.
///     </para>
/// </summary>
public sealed class GraphWorkflowAgentExecutorTests
{
    [ClassDataSource<GraphWorkflowAgentHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowAgentHostFixture Host { get; init; }

    /// <summary>
    ///     An unattended run has no approval round-trip, so an approval-gated tool would surface a request nobody can
    ///     answer. It is stripped from the offer AND named in the log, because a silently narrower offer is how a node
    ///     comes to behave differently from the agent an operator configured.
    /// </summary>
    [Test]
    public async Task AnAgentTurn_IsUnattendedAndIsOfferedNoApprovalRequiredTool()
    {
        const string instructions = "unattended-and-stripped";
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await StartToTheAgentAsync(harness, Graph(instructions)).ConfigureAwait(false);

        _ = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        var package = harness.Invocations.PackageFor(instructions);
        AssertEx.True(package.IsUnattended, "a graph workflow run is unattended by construction.");
        AssertEx.ContainsSingle(package.AllowedTools, tool => tool.Name == FakeGraphWorkflowAgentRuntime.OfferedTool);
        AssertEx.Empty(package.AllowedTools.Where(static tool => tool.RequiresApproval), "nothing in an unattended offer may need an approval.");
        AssertEx.True(harness.Services.GetRequiredService<RecordingLogger<GraphWorkflowAgentExecutor>>()
                             .HasEntry(LogLevel.Warning, FakeGraphWorkflowAgentRuntime.ApprovalRequiredTool),
            "the stripped tool is named, so a narrower offer is visible rather than silent.");
    }

    /// <summary>
    ///     The effective model is bound as a concrete <c>ModelProfile</c> so the runner never silently falls back to the
    ///     node default — and the whole-turn deadline is the NODE's own, not the operator's chat timeout.
    /// </summary>
    [Test]
    public async Task TheEffectiveModelAndTheNodesOwnTimeout_ReachThePackage()
    {
        const string instructions = "effective-model-and-timeout";
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await StartToTheAgentAsync(harness, Graph(instructions, nodeExtras: """, "timeoutSeconds": 42""")).ConfigureAwait(false);

        _ = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        var package = harness.Invocations.PackageFor(instructions);
        AssertEx.Equal(GraphWorkflowModels.LocalDefault, package.ModelProfile, "no node model and no agent pin leaves the node's local default.");
        AssertEx.Equal(expected: 42, package.Timeouts.InvocationTimeoutSeconds, "the graph author's own budget bounds the turn, so the deadline stage stays a backstop.");
    }

    /// <summary>
    ///     The locality gate, and where it sits: a graph workflow run is unattended, so a cloud effective model is
    ///     refused BEFORE capacity is asked and before any invocation exists.
    /// </summary>
    [Test]
    public async Task ACloudEffectiveModel_IsRefusedBeforeCapacityAndBeforeAnyInvocation()
    {
        const string instructions = "cloud-refused";
        const string model = "graph-cloud-refused";
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await StartToTheAgentAsync(harness, Graph(instructions, $$"""
                                                                              , "model": "{{model}}"
                                                                              """)).ConfigureAwait(false);

        var analyze = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, analyze.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, analyze.FailureClass, "a cloud model is a configuration refusal, and a retry answers the same.");
        AssertEx.Contains(analyze.Error, "node-local");
        AssertEx.Empty(harness.Invocations.Packages.Where(package => Prompt(package).Contains(instructions, StringComparison.Ordinal)),
            "the refusal happens before any invocation exists.");
        AssertEx.Empty(Capacity(harness).ReservationsFor(model), "and before capacity is asked, so there is nothing to leak.");
    }

    /// <summary>
    ///     <c>honorModelProfile</c> is <see langword="false" /> exactly when the node names its own model. With a bare
    ///     <see langword="true" /> a node overriding a cloud-pinned agent to a local one would pass the locality gate on
    ///     its own choice while the resolver gated the offer against — and returned — the cloud pin.
    /// </summary>
    [Test]
    public async Task HonorModelProfile_IsFalseExactlyWhenTheNodeNamesItsOwnModel()
    {
        const string inherits = "honor-pin-inherited";
        const string overrides = "honor-pin-overridden";
        const string model = "graph-local-override";
        await using var harness = new GraphWorkflowHarness(Host);

        var inheritedRun = await StartToTheAgentAsync(harness, Graph(inherits)).ConfigureAwait(false);
        _ = await AdvanceUntilTerminalAsync(harness, inheritedRun).ConfigureAwait(false);

        var overriddenRun = await StartToTheAgentAsync(harness, Graph(overrides, $$"""
                                                                                   , "model": "{{model}}"
                                                                                   """)).ConfigureAwait(false);
        _ = await AdvanceUntilTerminalAsync(harness, overriddenRun).ConfigureAwait(false);

        AssertEx.True(Runtimes(harness).CallFor(GraphWorkflowModels.LocalDefault).HonorModelProfile,
            "a node that names no model leaves the agent's own pin in charge.");
        AssertEx.False(Runtimes(harness).CallFor(model).HonorModelProfile, "a node that names one outranks the pin, offer and all.");
    }

    /// <summary>
    ///     The C13 case itself: a node overriding a CLOUD-pinned agent to a local model runs on the local model, and
    ///     the offer is gated by it. Without the suppressed pin this turn would be gated against a cloud model the
    ///     locality gate has already refused to run on.
    /// </summary>
    [Test]
    public async Task ANodeOverridingACloudPinnedAgent_RunsOnItsOwnLocalModel()
    {
        const string instructions = "override-a-cloud-pin";
        const string model = "graph-local-over-a-pin";
        await using var harness = new GraphWorkflowHarness(Host);
        var agentDefinitionId = await SeedAgentAsync(harness, "graph-cloud-pinned-agent").ConfigureAwait(false);
        var graph = Graph(instructions, $$"""
                                          , "model": "{{model}}", "agentDefinitionId": "{{agentDefinitionId}}"
                                          """);

        var runId = await StartToTheAgentAsync(harness, graph).ConfigureAwait(false);
        var analyze = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, analyze.Status, "the node's own local model is what the locality gate judged.");
        AssertEx.Equal(model, harness.Invocations.PackageFor(instructions).ModelProfile, "never the resolved pin — always the effective model.");
        AssertEx.False(Runtimes(harness).CallFor(model).HonorModelProfile);
    }

    /// <summary>
    ///     Both capability flags are threaded from the resolver. The builder defaults each to <see langword="true" />,
    ///     so a package carrying <see langword="false" /> is the proof; the thinking model is the other half of the pair.
    /// </summary>
    [Test]
    [Arguments("capabilities-plain", "graph-local-plain", false)]
    [Arguments("capabilities-thinking", "graph-local-thinking-model", true)]
    public async Task TheModelsThinkingCapabilities_ReachThePackage(string instructions, string model, bool expected)
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await StartToTheAgentAsync(harness, Graph(instructions, $$"""
                                                                              , "model": "{{model}}"
                                                                              """)).ConfigureAwait(false);

        _ = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        var package = harness.Invocations.PackageFor(instructions);
        AssertEx.Equal(expected, package.SupportsThinking);
        AssertEx.Equal(expected, package.ReasoningBudgetEnforceable);
    }

    /// <summary>A completed turn's answer and what it cost, as the node run's own output document reports them.</summary>
    [Test]
    public async Task ACompletedTurn_YieldsItsTextAndItsUsage()
    {
        const string instructions = "completed-with-usage";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn(Text: "the analysis"));
        var runId = await StartToTheAgentAsync(harness, Graph(instructions)).ConfigureAwait(false);

        var analyze = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, analyze.Status);
        var output = Output(analyze);
        AssertEx.Equal("the analysis", output.GetProperty("text").GetString());
        AssertEx.Equal(expected: 33, output.GetProperty("usage").GetProperty("totalTokens").GetInt32());
        AssertEx.Equal("stop", output.GetProperty("usage").GetProperty("finishReason").GetString());
        AssertEx.Equal(GraphWorkflowModels.LocalDefault, output.GetProperty("usage").GetProperty("model").GetString());
        AssertEx.True(output.GetProperty("json").ValueKind == JsonValueKind.Null, "a node with no response schema parses nothing.");
    }

    /// <summary>A node declaring a response schema gets the parsed object beside the text it parsed.</summary>
    [Test]
    public async Task ASchemaBackedNode_ParsesItsAnswerIntoTheOutputDocument()
    {
        const string instructions = "schema-parses";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn(Text: """{"requiresReview":true}"""));
        var runId = await StartToTheAgentAsync(harness, Graph(instructions, Schema)).ConfigureAwait(false);

        var analyze = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, analyze.Status);
        AssertEx.True(Output(analyze).GetProperty("json").GetProperty("requiresReview").GetBoolean());
    }

    /// <summary>
    ///     An answer that is not the object the schema demands fails the node RETRYABLY — a re-ask under the same
    ///     grammar can land where one attempt did not — and names the finish reason, because a truncated answer is
    ///     still <c>Completed</c> and <c>length</c> is the common cause. There is no salvage path.
    /// </summary>
    [Test]
    public async Task AnAnswerThatIsNotTheSchemasObject_FailsRetryablyAndNamesTheFinishReason()
    {
        const string instructions = "schema-refuses-a-truncated-answer";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn(Text: """{"requiresReview":tr""", FinishReason: "length"));

        // Two attempts, because retryability is the assertion. The failing write's own class never stands still long
        // enough to read — the retry stage runs in the SAME tick that settles it — so the node.retried event, which
        // carries the failure it is re-attempting, is where that class survives.
        var runId = await StartToTheAgentAsync(harness, Graph(instructions, Schema, """, "maxAttempts": 2""")).ConfigureAwait(false);

        var analyze = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, analyze.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.AttemptsExhausted, analyze.FailureClass, "the second attempt is the one that used up the node's budget.");
        AssertEx.Contains(analyze.Error, "length", message: "and the reason names the finish reason, because a truncated answer still Completed.");

        var retried = AssertEx.NotNull((await harness.ReadEventsAsync(runId).ConfigureAwait(false))
                                       .FirstOrDefault(static entry => entry.EventType == GraphWorkflowEventTypes.NodeRetried),
            "an unparseable answer is retryable, so the run tried again.");
        AssertEx.Contains(retried.DetailJson, nameof(GraphWorkflowFailureClass.NodeFailed), message: "the class it re-attempted is the retryable one, never ValidationFailed.");
        AssertEx.Contains(retried.DetailJson, "length");
    }

    /// <summary>A turn whose provider failed reports that it failed and nothing else — the detail is in the node logs.</summary>
    [Test]
    public async Task AFailedTurn_FailsTheNodeWithoutRepeatingTheProvidersWords()
    {
        const string instructions = "provider-failed";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn(GraphWorkflowTurnOutcome.Fails));
        const string model = "graph-local-provider-failed";

        // One attempt, so one turn and one reservation. An Agent node left to itself declares THREE — the parser's
        // work-node default — and the retry stage would then run this failure twice more.
        var runId = await StartToTheAgentAsync(harness, Graph(instructions, $$"""
                                                                              , "model": "{{model}}"
                                                                              """, SingleAttempt)).ConfigureAwait(false);

        var analyze = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, analyze.Status);
        AssertEx.False(analyze.Error?.Contains("10.0.0.7", StringComparison.Ordinal) == true, "a row's reason is read by an operator, not by a diagnostician.");
        AssertEx.ContainsSingle(Capacity(harness).ReservationsFor(model), static reservation => reservation.Disposed, "a failing turn still releases its footprint.");
    }

    /// <summary>A turn that reported no terminal state at all is a failure rather than a row nothing settles.</summary>
    [Test]
    public async Task ATurnThatReportedNoTerminalState_FailsTheNode()
    {
        const string instructions = "silent-turn";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn(GraphWorkflowTurnOutcome.Silent));
        var runId = await StartToTheAgentAsync(harness, Graph(instructions)).ConfigureAwait(false);

        var analyze = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, analyze.Status);
        AssertEx.Contains(analyze.Error, "no result");
    }

    /// <summary>
    ///     An unforeseen exception inside the lane's task body settles the row with a sanitized reason rather than
    ///     faulting the task. A faulted task would rethrow out of the poll on every tick forever, about work long over.
    /// </summary>
    [Test]
    public async Task AnUnforeseenExceptionInTheTurn_SettlesTheRowInsteadOfFaultingTheTask()
    {
        const string instructions = "unforeseen-throw";
        const string model = "graph-local-unforeseen";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn(GraphWorkflowTurnOutcome.Throws));
        var runId = await StartToTheAgentAsync(harness, Graph(instructions, $$"""
                                                                              , "model": "{{model}}"
                                                                              """, SingleAttempt)).ConfigureAwait(false);

        var analyze = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, analyze.Status);
        AssertEx.False(analyze.Error?.Contains("could not reach its provider", StringComparison.Ordinal) == true, "the exception's own words never reach the row.");
        AssertEx.ContainsSingle(Capacity(harness).ReservationsFor(model), static reservation => reservation.Disposed, "the outermost finally runs on this path too.");
    }

    /// <summary>
    ///     A node that asks for its predecessors gets them inlined into its prompt; one that does not gets its
    ///     instructions and nothing else. There is no dereference tool, so inlining is the whole mechanism.
    /// </summary>
    [Test]
    [Arguments("upstream-included", "true", true)]
    [Arguments("upstream-excluded", "false", false)]
    public async Task IncludeUpstreamOutputs_DecidesWhetherThePredecessorsDocumentsReachThePrompt(string instructions, string include, bool expected)
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await StartToTheAgentAsync(harness,
                Graph(instructions, $$"""
                                      , "includeUpstreamOutputs": {{include}}
                                      """),
                """{"topic":"the overnight logs"}""")
            .ConfigureAwait(false);

        _ = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        var prompt = Prompt(harness.Invocations.PackageFor(instructions));
        AssertEx.Equal(expected, prompt.Contains("the overnight logs", StringComparison.Ordinal), "the run input travels through the Start node's output document.");
        AssertEx.Equal(expected, prompt.Contains("```json", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The inlined upstream is BUDGETED, and says so when it is cut. A prompt that quietly lost half its evidence
    ///     is how a node produces a confident wrong answer.
    /// </summary>
    [Test]
    public async Task InlinedUpstreamOverTheBudget_IsTruncatedWithAMarkerRatherThanSilently()
    {
        const string instructions = "upstream-truncated";

        // A private host: the budget itself is the thing under test, and it is host-level configuration. 1024 is its
        // floor, so the run input is sized just under it and the upstream document that WRAPS it just over.
        await using var harness = GraphWorkflowHarness.PrivateAgentHost(("GraphWorkflows:MaxRunInputBytes", "1024"));
        var runId = await StartToTheAgentAsync(harness, Graph(instructions), $$"""{"topic":"{{new string('a', count: 1000)}}"}""").ConfigureAwait(false);

        _ = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);

        var prompt = Prompt(harness.Invocations.PackageFor(instructions));
        AssertEx.Contains(prompt, "truncated,");
        AssertEx.Contains(prompt, "bytes omitted");
    }

    /// <summary>
    ///     One stop, start to finish. The row is <c>Queued</c> the tick it is dispatched and <c>Running</c> with its
    ///     invocation id only once the lease has landed; the stop asks the RUNNER as well as the token, exactly once,
    ///     and answers no on a repeat; and the settled row is <c>Cancelled</c> with its footprint released.
    /// </summary>
    [Test]
    public async Task StoppingATurn_AsksTheRunnerOnceAndSettlesTheRowCancelled()
    {
        const string instructions = "stop-a-parked-turn";
        const string model = "graph-local-stopped";

        // A private host: a parked turn holds the node-wide invocation slot, which on a shared host is every sibling's.
        await using var harness = GraphWorkflowHarness.PrivateAgentHost();
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn(GraphWorkflowTurnOutcome.Parks));
        var runId = await StartToTheAgentAsync(harness,
                Graph(instructions, $$"""
                                      , "model": "{{model}}"
                                      """))
            .ConfigureAwait(false);

        var queued = await harness.ReadNodeRunAsync(runId, "analyze").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Queued, queued.Status, "the dispatch tick never writes Running: the turn holds no node-wide slot yet.");
        AssertEx.Equal("awaiting-agent-slot", queued.Error);

        await harness.Invocations.WhenRunningAsync(instructions).WaitAsync(TestBudgets.Contended).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var running = await harness.ReadNodeRunAsync(runId, "analyze").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Running, running.Status, "the lease landed, so the row may honestly say so.");
        AssertEx.True(running.InvocationId.HasValue, "the row carries the correlation id of a turn nothing else survives.");
        var invocationId = running.InvocationId!.Value;
        AssertEx.Equal(invocationId, harness.Invocations.PackageFor(instructions).InvocationId, "the id was minted before the turn, so both sides agree on it.");

        var executor = Executor(harness);
        AssertEx.True(await executor.StopAsync(running.Id).ConfigureAwait(false), "the first ask is the one that actually cancels.");
        AssertEx.False(await executor.StopAsync(running.Id).ConfigureAwait(false), "and the repeat is not work, which is what keeps a drain from spinning.");
        AssertEx.Equal(expected: 1, harness.Invocations.Cancelled.Count(cancelled => cancelled == invocationId), "the runner is told exactly once.");

        var analyze = await AdvanceUntilTerminalAsync(harness, runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, analyze.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.Cancelled, analyze.FailureClass);
        AssertEx.ContainsSingle(Capacity(harness).ReservationsFor(model), static reservation => reservation.Disposed, "a cancelled turn releases its footprint too.");
    }

    /// <summary>
    ///     A turn cancelled while still parked on the node-wide lease ends with no state to map at all. The poll checks
    ///     for that BEFORE it awaits: awaiting a cancelled task would rethrow, the dispatcher would swallow it, and the
    ///     row would rethrow again on every tick forever.
    /// </summary>
    [Test]
    public async Task ATurnCancelledWhileParkedOnTheLease_SettlesCancelledWithoutRethrowing()
    {
        const string holder = "holds-the-only-slot";
        const string waiter = "parked-on-the-lease";
        await using var harness = GraphWorkflowHarness.PrivateAgentHost();
        harness.Invocations.Script(holder, new GraphWorkflowScriptedTurn(GraphWorkflowTurnOutcome.Parks));

        var holding = await StartToTheAgentAsync(harness, Graph(holder)).ConfigureAwait(false);
        await harness.Invocations.WhenRunningAsync(holder).WaitAsync(TestBudgets.Contended).ConfigureAwait(false);

        // The second turn starts, asks for the one slot the first one holds, and never gets it.
        var waiting = await StartToTheAgentAsync(harness, Graph(waiter)).ConfigureAwait(false);
        var parked = await harness.ReadNodeRunAsync(waiting, "analyze").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Queued, parked.Status);

        AssertEx.True(await Executor(harness).StopAsync(parked.Id).ConfigureAwait(false));

        var analyze = await AdvanceUntilTerminalAsync(harness, waiting).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, analyze.Status, "a turn that never ran was still cancelled, not failed.");
        AssertEx.Empty(harness.Invocations.Packages.Where(package => Prompt(package).Contains(waiter, StringComparison.Ordinal)),
            "it never reached the runner at all.");

        _ = await Executor(harness).StopAsync((await harness.ReadNodeRunAsync(holding, "analyze").ConfigureAwait(false)).Id).ConfigureAwait(false);
        _ = await AdvanceUntilTerminalAsync(harness, holding).ConfigureAwait(false);
    }

    /// <summary>
    ///     An entry whose row has moved on is dropped before anything is polled — and the runner is told, because a
    ///     superseded turn still holds the node-wide slot and a cancelled token alone does not unwind a provider stream.
    /// </summary>
    [Test]
    public async Task ForgetSuperseded_DropsAMovedOnEntryAndUnwindsItsTurn()
    {
        const string instructions = "superseded-entry";
        await using var harness = GraphWorkflowHarness.PrivateAgentHost();
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn(GraphWorkflowTurnOutcome.Parks));
        var runId = await StartToTheAgentAsync(harness, Graph(instructions)).ConfigureAwait(false);
        await harness.Invocations.WhenRunningAsync(instructions).WaitAsync(TestBudgets.Contended).ConfigureAwait(false);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "analyze").ConfigureAwait(false);
        var executor = Executor(harness);
        AssertEx.True(executor.IsInFlight(nodeRun.Id));

        await executor.ForgetSupersededAsync([
            nodeRun with
            {
                Attempt = nodeRun.Attempt + 1
            }
        ]).ConfigureAwait(false);

        AssertEx.False(executor.IsInFlight(nodeRun.Id), "the entry belongs to the attempt before, and is not an answer about this one.");
        await AssertEx.EventuallyAsync(() => harness.Invocations.Cancelled.Count > 0, TestBudgets.Contended, "a dropped turn is unwound rather than left holding the slot.")
                      .ConfigureAwait(false);
    }

    /// <summary>
    ///     The queue is honest about itself. Three parallel Agent nodes share ONE node-wide invocation slot however
    ///     wide the lane is, so one row runs and two say what they are waiting for rather than leaving a reader to
    ///     infer it from timing.
    /// </summary>
    [Test]
    public async Task ThreeParallelAgentNodes_ReadRunningQueuedQueued()
    {
        await using var harness = GraphWorkflowHarness.PrivateAgentHost();
        foreach (var branch in new[] { "Left.", "Middle.", "Right." })
        {
            harness.Invocations.Script(branch, new GraphWorkflowScriptedTurn(GraphWorkflowTurnOutcome.Parks));
        }

        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.AgentFanOut).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        await AssertEx.EventuallyAsync(() => harness.Invocations.ActiveInvocationCount == 1, TestBudgets.Contended, "exactly one turn holds the node's only slot.")
                      .ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var branches = (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false))
            .Where(static nodeRun => nodeRun.Kind == GraphWorkflowNodeKind.Agent)
            .ToList();
        AssertEx.Equal(expected: 1, branches.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Running), "one branch holds the slot.");
        AssertEx.Equal(expected: 2, branches.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Queued), "and the other two say so.");

        foreach (var branch in branches)
        {
            _ = await Executor(harness).StopAsync(branch.Id).ConfigureAwait(false);
        }
    }

    /// <summary>An Agent node declares three attempts when it names none. A test about ONE turn says so.</summary>
    private const string SingleAttempt = """, "maxAttempts": 1""";

    private const string Schema = """
                                  , "responseJsonSchema": { "type": "object", "properties": { "requiresReview": { "type": "boolean" } } }
                                  """;

    /// <summary>A linear <c>Start → Agent → End</c> graph whose agent node the caller configures.</summary>
    private static string Graph(string instructions, string? agentConfig = null, string? nodeExtras = null) =>
        $$"""
          {
            "schemaVersion": 1,
            "nodes": [
              { "key": "start", "kind": "Start" },
              { "key": "analyze", "kind": "Agent"{{nodeExtras}}, "config": { "instructions": "{{instructions}}"{{agentConfig}} } },
              { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
            ],
            "edges": [
              { "key": "e1", "from": "start", "to": "analyze" },
              { "key": "e2", "from": "analyze", "to": "done" }
            ]
          }
          """;

    /// <summary>Starts a run and ticks it up to and including the tick that dispatches the agent node.</summary>
    private static async Task<Guid> StartToTheAgentAsync(GraphWorkflowHarness harness, string graphJson, string? inputJson = null)
    {
        var runId = await harness.StartRunAsync(graphJson, inputJson).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        return runId;
    }

    /// <summary>
    ///     Ticks until the agent node run is terminal. Bounded by tick count rather than by a clock: every iteration is
    ///     a real round trip through the store, which is what gives the detached turn its chance to land.
    /// </summary>
    private static async Task<GraphWorkflowNodeRunSnapshot> AdvanceUntilTerminalAsync(GraphWorkflowHarness harness, Guid runId, int maxTicks = 60)
    {
        for (var tick = 0; tick < maxTicks; tick++)
        {
            var nodeRun = await harness.ReadNodeRunAsync(runId, "analyze").ConfigureAwait(false);
            if (GraphWorkflowStateMachine.IsTerminal(nodeRun.Status))
            {
                return nodeRun;
            }

            _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        }

        throw new AssertionException($"Run {runId} left its agent node unsettled after {maxTicks} ticks.");
    }

    private static async Task<Guid> SeedAgentAsync(GraphWorkflowHarness harness, string modelProfile)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var definition = await scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>()
                                    .AddAsync(new AgentDefinitionInput($"Graph agent {Guid.NewGuid():N}",
                                        Description: null,
                                        "raw instructions",
                                        modelProfile,
                                        ReasoningEffort: null,
                                        AgentDefinitionKind.Single,
                                        [],
                                        new Dictionary<string, bool>(StringComparer.Ordinal),
                                        OrchestrationTopologyJson: null))
                                    .ConfigureAwait(false);
        return definition.Id;
    }

    private static JsonElement Output(GraphWorkflowNodeRunSnapshot nodeRun)
    {
        using var document = JsonDocument.Parse(AssertEx.NotNull(nodeRun.OutputJson, "a settled agent node always carries its output document."));
        return document.RootElement.GetProperty("output").Clone();
    }

    private static string Prompt(RuntimePackage package) =>
        package.ConversationContext[0].Content;

    private static IGraphWorkflowNodeExecutor Executor(GraphWorkflowHarness harness) =>
        harness.Services.GetServices<IGraphWorkflowNodeExecutor>().Single(static executor => executor.Owns(GraphWorkflowNodeKind.Agent));

    private static FakeGraphWorkflowCapacity Capacity(GraphWorkflowHarness harness) =>
        (FakeGraphWorkflowCapacity)harness.Services.GetRequiredService<ICapacityService>();

    private static FakeGraphWorkflowAgentRuntime Runtimes(GraphWorkflowHarness harness) =>
        (FakeGraphWorkflowAgentRuntime)harness.Services.GetRequiredService<IAgentDefinitionResolver>();
}
