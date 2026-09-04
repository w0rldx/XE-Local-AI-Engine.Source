namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Cost telemetry on a node run: that collecting it changes nothing about how a run behaves, that it is written for
///     every status an attempt can stop on rather than for a list of call sites, and that the tool names survive the
///     scope they were collected in.
/// </summary>
public sealed class DevWorkflowNodeRunTelemetryTests
{
    /// <summary>The agent definition the harness's seeded catalog binds, so the node run actually owns a work session.</summary>
    private const string BoundAgentId = "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b";

    /// <summary>
    ///     An agent that owns a session, a human gate behind it, and a join with no out-edges — every status arm in one
    ///     shape: a <c>Succeeded</c> node WITH successors (so a route document can be non-empty), a
    ///     <c>WaitingForApproval</c> pause after real work, and a terminal node that routes nowhere.
    /// </summary>
    private const string AgentThenGate = $$"""
                                           {
                                             "schemaVersion": 1,
                                             "nodes": [
                                               { "nodeKey": "research", "nodeType": "Agent", "label": "Research", "agentDefinitionId": "{{BoundAgentId}}" },
                                               { "nodeKey": "approve", "nodeType": "HumanGate", "label": "Approve" },
                                               { "nodeKey": "done", "nodeType": "Join" }
                                             ],
                                             "edges": [
                                               { "from": "research", "to": "approve" },
                                               { "from": "approve", "to": "done" }
                                             ]
                                           }
                                           """;

    private static readonly JsonSerializerOptions ConsumptionJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     T1: the run behaves identically with the collector registered and with one that answers nothing. Compared on
    ///     the event trail, on every node run's verdict, and on the work item the run drives — the whole record of what
    ///     the runtime decided.
    ///     <para>
    ///         <c>Skipped</c> rows are covered by the second graph and their expectation is stated: they are terminal
    ///         with no session and no output, so they carry a route and all-null counters in BOTH arms.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Telemetry_DoesNotAlterTransitionCommands()
    {
        var withCollector = await DriveBothGraphsAsync(stub: null).ConfigureAwait(false);
        var withoutCollector = await DriveBothGraphsAsync(new StubDevWorkflowNodeTelemetrySource()).ConfigureAwait(false);

        AssertEx.Equal(withoutCollector,
            withCollector,
            "Telemetry is write-only metadata: it may not change a transition, a route or a work-item status.");
    }

    /// <summary>
    ///     T7: a real <c>Succeeded</c> settle on a node WITH out-edges names at least one satisfied successor. This is
    ///     the test that fails if the decorator asks its route question of the pre-write row, which still reads
    ///     <c>Running</c> and would make every route document empty. Paired: a <c>Blocked</c> row gets columns and a
    ///     null route, because a node that has not finished has routed nowhere yet.
    /// </summary>
    [Test]
    public async Task Succeeded_Settle_WritesNonEmptyRoute()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(AgentThenGate).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var research = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, research.Status);
        var route = ReadRoute(research);
        AssertEx.Equal("approve", string.Join(",", route.Satisfied), "A succeeded node with an out-edge names the successor its edge satisfied.");
        AssertEx.Empty(route.Dead, "Nothing else left this node.");
        AssertEx.False(route.Truncated, "One successor is not a truncation.");

        var gate = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, gate.Status);
        AssertEx.Null(gate.RouteJson, "A node run waiting on a human has not finished, so it has routed nowhere yet.");

        await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var answered = await harness.ReadNodeRunAsync(runId, "approve").ConfigureAwait(false);
        var gateRoute = ReadRoute(answered);
        AssertEx.Equal("done", string.Join(",", gateRoute.Satisfied), "The answered gate routes on the document its own answer wrote.");
        AssertEx.Equal(DevWorkflowDecisionKind.Approve.ToString(), gateRoute.GateAnswer, "And the answer rides along as its own token.");

        var join = await harness.ReadNodeRunAsync(runId, "done").ConfigureAwait(false);
        var joinRoute = ReadRoute(join);
        AssertEx.Empty(joinRoute.Satisfied, "A node with no out-edges routes nowhere, and says so with an empty document rather than a null one.");
        AssertEx.Empty(joinRoute.Dead, "A node with no out-edges routes nowhere, and says so with an empty document rather than a null one.");
    }

    /// <summary>
    ///     T3: a collector that throws, and one that hangs past the decorator's deadline, both leave the settle exactly
    ///     as it would have been — same terminal status, same failure class, and all twelve columns null. The
    ///     enrichment runs before the store call and never inside its transaction, so losing it loses a measurement and
    ///     nothing else.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Telemetry_CollectorThrows_NodeStillSettles(bool faults)
    {
        var stub = faults
            ? new StubDevWorkflowNodeTelemetrySource { Fault = new InvalidOperationException("The envelope read fell over.") }
            : new StubDevWorkflowNodeTelemetrySource { Delay = TimeSpan.FromSeconds(30) };

        await using var harness = NewHarness(stub);
        var runId = await harness.StartRunAsync(AgentThenGate).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var research = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, research.Status, "A broken collector may not change the verdict.");
        AssertEx.Null(research.FailureClass, "Nor invent a failure.");
        AssertEx.True(stub.Calls > 0, "The collector has to have been asked, or this proves nothing.");
        AssertEmptyTelemetry(research, "A collection that threw or timed out writes nothing at all.");
    }

    /// <summary>
    ///     T6: the coverage guard. Every status an attempt can stop on — <c>Succeeded</c>, <c>Failed</c>,
    ///     <c>Skipped</c>, <c>Cancelled</c>, <c>Blocked</c>, <c>WaitingForApproval</c> — is driven through the REAL
    ///     executors, including the retry policy's own block and fail paths, and every row that reached one of them
    ///     carries telemetry wherever a work session existed. Because the write is in the decorator, a call site added
    ///     later is covered by construction: this asserts the gate's status set, not a site list.
    /// </summary>
    [Test]
    public async Task Telemetry_CoversEveryTerminalOrBlockedWrite()
    {
        var seen = new Dictionary<DevWorkflowNodeRunStatus, string>();

        await using (var harness = new DevWorkflowHarness())
        {
            var runId = await harness.StartRunAsync(AgentThenGate).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
            await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
            AssertTelemetryGate(await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false), seen);

            // The gate is left unanswered and the run cancelled, so the drain settles a live human wait to Cancelled.
            await harness.TransitionRunAsync(runId, DevWorkflowRunStatus.Cancelling).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
            AssertTelemetryGate(await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false), seen);
        }

        // Blocked, Failed and Skipped, all through the retry policy's own settle paths on a real dead branch.
        foreach (var decision in new[] { DevWorkflowDecisionKind.Abandon, DevWorkflowDecisionKind.Skip })
        {
            await using var harness = new DevWorkflowHarness();
            var runId = await harness.StartRunAsync(DevWorkflowGraphs.AnyJoinOverADeadBranch, developmentProjectId: Guid.NewGuid()).ConfigureAwait(false);
            await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
            AssertTelemetryGate(await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false), seen);

            await harness.DecideAsync(runId, "anydoomed", decision).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
            AssertTelemetryGate(await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false), seen);
        }

        foreach (var status in new[]
                 {
                     DevWorkflowNodeRunStatus.Succeeded,
                     DevWorkflowNodeRunStatus.Failed,
                     DevWorkflowNodeRunStatus.Skipped,
                     DevWorkflowNodeRunStatus.Cancelled,
                     DevWorkflowNodeRunStatus.Blocked,
                     DevWorkflowNodeRunStatus.WaitingForApproval
                 })
        {
            AssertEx.True(seen.ContainsKey(status), $"No node run reached {status}, so the gate was never proved for it.");
        }
    }

    /// <summary>
    ///     T9, the durability guard. The cap scope a step collects its tool names under is disposed when the step ends,
    ///     and the node run settles later, in another scope. So the names must already be PERSISTED by then — this test
    ///     pins that by asserting the disposed scope answers nothing first, and only then that the settled node run
    ///     names both tools.
    /// </summary>
    [Test]
    public async Task Telemetry_CollectsToolNames_AfterBudgetScopeDisposed()
    {
        var cap = ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 10);
        string detailJson;
        using (cap)
        {
            using var scope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
            {
                MaxProviderCallsPerInvocation = 50
            });
            var budget = ProviderCallBudget.Current!;
            budget.RegisterProviderRound(estimatedInputTokens: 120, toolSchemaTokens: 40);
            budget.RecordToolCallRequested("read_document");
            budget.RecordToolCallRequested("search_web");
            budget.RecordToolCallCompleted(TimeSpan.FromMilliseconds(5), resultBytes: 10, failed: false);
            budget.RecordToolCallCompleted(TimeSpan.FromMilliseconds(5), resultBytes: 10, failed: false);

            var consumption = AssertEx.NotNull(cap.CaptureConsumption(), "Read while the scope is alive — that is the contract the supervisor keeps.");
            AssertEx.Equal("read_document,search_web",
                string.Join(",", consumption.ToolNames ?? []),
                "The names are collected beside the counts, ordinal-sorted.");
            AssertEx.Equal(expected: 40L, consumption.ToolSchemaTokens, "Schema tokens shipped across rounds ride out with them.");
            detailJson = SerializeConsumption(consumption);
        }

        AssertEx.Null(cap.CaptureConsumption(),
            "A disposed scope answers nothing — which is exactly why the names have to be persisted on the step row instead.");

        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(AgentThenGate).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await AppendStepConsumptionAsync(harness, runId, "research", detailJson).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var research = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal("""["read_document","search_web"]""",
            AssertEx.NotNull(research.ToolNamesJson, "The names have to reach the column, long after the budget that held them was disposed."));
        AssertEx.Equal(expected: 2, research.ToolCalls, "The counts come off the same row.");
        AssertEx.Equal(expected: 1, research.ProviderCalls);
        AssertEx.Equal(expected: 120L, research.EstimatedInputTokens);
        AssertEx.Equal(expected: 40L, research.ToolSchemaTokens);
        AssertEx.Equal(expected: 0, research.WorkSessionSteps, "The session took no step here, and zero is a measurement rather than an absence.");
    }

    /// <summary>
    ///     The bounds, all six of them: the budget's own set stops at sixteen distinct names, each NAME is clamped to
    ///     128 characters with a trailing marker before it is ever kept, a name the caller could not resolve against
    ///     the offered tools is not kept at all, the collector's union re-caps at sixteen across steps, the serialized
    ///     column stays inside 1024 characters and closes with an ellipsis when it had to drop names, and a step row
    ///     written before the field existed reads back as no list at all rather than as an empty one.
    ///     <para>
    ///         The per-name clamp and the resolution gate are the two that matter for the carriers BESIDE the column:
    ///         the persisted step detail and the work-session event detail take the budget's set verbatim, and only a
    ///         bound applied here bounds them.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ToolNames_AreBoundedAtEverySeam()
    {
        using (var cap = ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 40))
        {
            using var scope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
            {
                MaxProviderCallsPerInvocation = 50
            });
            var budget = ProviderCallBudget.Current!;
            for (var index = 0; index < 30; index++)
            {
                budget.RecordToolCallRequested("tool" + index.ToString("D2", CultureInfo.InvariantCulture));
            }

            budget.RegisterProviderRound(estimatedInputTokens: 1);
            var capped = AssertEx.NotNull(cap.CaptureConsumption()).ToolNames ?? [];
            AssertEx.Equal(expected: 16, capped.Count, "The cap is enforced IN the budget, so a runaway tool loop cannot grow the set.");
            AssertEx.True(capped.All(static name => name.Length <= ProviderCallBudget.MaxToolNameLength),
                "Every kept name is inside the per-name bound, whatever the model asked for.");
        }

        // The per-name bound, on its own scope so the sixteen-name cap above cannot be what drops the long one.
        using (var cap = ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 4))
        {
            using var scope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
            {
                MaxProviderCallsPerInvocation = 8
            });
            var budget = ProviderCallBudget.Current!;
            budget.RecordToolCallRequested(new string('x', 400));

            // What the caller passes for a name that resolved against NO offered tool: the call counts, the name does not.
            budget.RecordToolCallRequested(toolName: null);
            budget.RegisterProviderRound(estimatedInputTokens: 1);

            var consumption = AssertEx.NotNull(cap.CaptureConsumption());
            var kept = consumption.ToolNames ?? [];
            AssertEx.Equal(expected: 1, kept.Count, "An unresolved name is not a tool this run reached for, so only the resolved one is kept.");
            AssertEx.Equal(expected: ProviderCallBudget.MaxToolNameLength, kept[0].Length, "A long name is clamped at the source, not by whichever reader happens to clamp.");
            AssertEx.True(kept[0].EndsWith('…'), "And it says it was clamped, or a prefix reads as the whole identifier.");
            AssertEx.Equal(expected: 2,
                budget.CaptureEfficiencySnapshot().ToolCallsRequested,
                "Both calls are still COUNTED — dropping a name is not dropping the call it names.");
        }

        // A legacy row: the shape the supervisor wrote before the names existed.
        var legacy = JsonSerializer.Deserialize<WorkSessionStepConsumptionDetail>(
            """{"providerCalls":2,"estimatedInputTokens":10,"toolCallsCompleted":1,"providerCallCap":8,"attachedBudgets":1}""",
            ConsumptionJsonOptions);
        var legacyDetail = AssertEx.NotNull(legacy, "The legacy shape still has to parse.");
        AssertEx.Null(legacyDetail.ToolNames, "A row that predates the field has no list, which is not the same as an empty one.");
        AssertEx.Equal(expected: 0L, legacyDetail.ToolSchemaTokens, "And no schema tokens either, rather than a parse failure.");

        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(AgentThenGate).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        // Two steps, eleven long names each: past the sixteen-name cap and past the column's 1024 characters.
        await AppendStepConsumptionAsync(harness, runId, "research", DetailWithNames(LongNames(prefix: "a", count: 11))).ConfigureAwait(false);
        await AppendStepConsumptionAsync(harness, runId, "research", DetailWithNames(LongNames(prefix: "b", count: 11))).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var json = AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false)).ToolNamesJson);
        AssertEx.True(json.Length <= 1024, $"tool_names_json is bounded at 1024 characters; this one was {json.Length}.");
        var names = AssertEx.NotNull(JsonSerializer.Deserialize<List<string>>(json, ConsumptionJsonOptions));
        AssertEx.True(names.Count <= 16, "The union re-caps at sixteen, so two capped steps cannot make thirty-two.");
        AssertEx.Equal("…", names[^1], "A trimmed list has to say so, or a short list reads as the whole set.");
    }

    /// <summary>
    ///     A DevTask node run has no step rows to read, so its tool-name column is null — which the runbook states
    ///     means "there were no step rows", never "this node called no tools".
    /// </summary>
    [Test]
    public async Task ADevTaskNodeRun_WritesNoToolNames()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.AnyJoinOverADeadBranch, developmentProjectId: Guid.NewGuid()).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var tool = await harness.ReadNodeRunAsync(runId, "anysurvivor").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, tool.Status);
        AssertEx.Null(tool.ToolNamesJson, "A node run with no work session has no step rows to read names off.");
        AssertEx.Null(tool.ToolCalls, "And no counts either — a structural row's cost columns are absent, not zero.");
        AssertEx.NotNull(tool.RouteJson, "It still records where it routed, which is the one thing every terminal row can answer.");
    }

    /// <summary>
    ///     The served model name is clamped to the column's 256 characters BY CONSTRUCTION, like both sibling text
    ///     columns. <c>agent_execution_logs.model_name</c> declares no length of its own and SQLite enforces none, so a
    ///     name copied verbatim would silently overrun a bound the schema declares — and the collector is where the
    ///     bound can still be kept.
    /// </summary>
    [Test]
    public async Task ServedModelName_IsClampedToTheColumnsBound()
    {
        var conversationId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var sessions = Substitute.For<IAgentWorkSessionStore>();
        sessions.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(Session(sessionId, conversationId));
        sessions.ListEventsAsync(sessionId, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns([]);

        var logs = Substitute.For<IAgentExecutionLogStore>();
        logs.ListRunEnvelopesAsync(conversationId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Envelope(conversationId, new string('m', 300))]);

        var collected = await new DevWorkflowNodeTelemetrySource(sessions, logs)
                              .CollectAsync(NodeRunWithSession(sessionId), DevWorkflowNodeRunStatus.Succeeded, CancellationToken.None)
                              .ConfigureAwait(false);

        var served = AssertEx.NotNull(AssertEx.NotNull(collected).ServedModelName);
        AssertEx.Equal(expected: 256, served.Length, "served_model_name is declared at 256 characters, so the collector may not hand the column more.");
        AssertEx.Equal(new string('m', 256), served, "And it is the FRONT of the name that is kept, not a hash of it.");
    }

    private static AgentWorkSessionSnapshot Session(Guid sessionId, Guid conversationId) =>
        new(sessionId,
            "Research",
            "Objective",
            AgentWorkSessionKind.Research,
            AgentWorkSessionStatus.Completed,
            Guid.NewGuid(),
            conversationId,
            CurrentTaskId: null,
            StepCount: 1,
            LastCheckpointId: null,
            LastSequence: 1,
            ConfigVersion: 1,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            Version: 1);

    private static AgentRunEnvelopeRecord Envelope(Guid conversationId, string modelName) =>
        new(Guid.NewGuid(),
            SchemaVersion: 1,
            Guid.NewGuid(),
            conversationId,
            MessageId: null,
            InvocationId: null,
            RequestId: null,
            modelName,
            "local",
            "Completed",
            Success: true,
            FailureCategory: null,
            DurationMs: 10,
            PromptTokens: 1,
            CompletionTokens: 2,
            ReasoningTokens: null,
            TotalTokens: 3,
            ContentChunkCount: null,
            ReasoningChunkCount: null,
            TraceId: null,
            StartedAtUtc: null,
            CreatedAtUtc: 0);

    private static DevWorkflowNodeRunSnapshot NodeRunWithSession(Guid sessionId) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            "research",
            DevWorkflowNodeType.Agent,
            Attempt: 1,
            MaxAttempts: 3,
            SessionResumes: 0,
            DevWorkflowNodeRunStatus.Running,
            QueueReason: null,
            PendingDecisionKind: null,
            Sequence: 1,
            sessionId,
            WorkSessionAvailable: true,
            AgentDefinitionId: null,
            DevelopmentProjectId: null,
            DevelopmentTaskId: null,
            InputJson: null,
            OutputJson: null,
            PolicyResolutionJson: null,
            MaterializedFromNodeRunId: null,
            MaterializationIndex: null,
            FailureClass: null,
            TerminalReason: null,
            QueuedAtUtc: null,
            StartedAtUtc: null,
            EndedAtUtc: null,
            CreatedAtUtc: 0);

    /// <summary>Every observable the two arms of T1 compare: the events, the rows and the work item.</summary>
    private static async Task<string> DriveBothGraphsAsync(StubDevWorkflowNodeTelemetrySource? stub)
    {
        var lines = new List<string>();

        await using (var harness = stub is null ? new DevWorkflowHarness() : NewHarness(stub))
        {
            var runId = await harness.StartRunAsync(AgentThenGate).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
            await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
            await harness.DecideAsync(runId, "approve", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
            await RecordAsync(harness, runId, lines).ConfigureAwait(false);
        }

        await using (var harness = stub is null ? new DevWorkflowHarness() : NewHarness(stub))
        {
            var runId = await harness.StartRunAsync(DevWorkflowGraphs.AnyJoinOverADeadBranch, developmentProjectId: Guid.NewGuid()).ConfigureAwait(false);
            await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
            await harness.DecideAsync(runId, "anydoomed", DevWorkflowDecisionKind.Skip).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
            await RecordAsync(harness, runId, lines).ConfigureAwait(false);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static async Task RecordAsync(DevWorkflowHarness harness, Guid runId, List<string> lines)
    {
        // The event Outcome is compared here rather than in the shared trail helper: it belongs to THIS tuple (plan
        // section 5) and it is the field a later enrichment would reach for, but the rest of the namespace asserts on
        // the trail as a list of types.
        foreach (var entry in await harness.ReadEventsAsync(runId).ConfigureAwait(false))
        {
            lines.Add($"event {entry.EventType} outcome={entry.Outcome}");
        }

        foreach (var nodeRun in (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).OrderBy(static row => row.NodeKey, StringComparer.Ordinal))
        {
            lines.Add($"{nodeRun.NodeKey} {nodeRun.Status} attempt={nodeRun.Attempt} failure={nodeRun.FailureClass} reason={nodeRun.TerminalReason}");
        }

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        lines.Add($"run {run.Status} failure={run.FailureClass}");
        lines.Add($"workItem {(await harness.ReadWorkItemAsync(runId).ConfigureAwait(false)).Status}");
    }

    /// <summary>
    ///     The gate itself, asserted over whatever rows a run reached: a settled row that owned a work session carries
    ///     telemetry, a terminal row carries a route, and a live pause does not.
    /// </summary>
    private static void AssertTelemetryGate(IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns, Dictionary<DevWorkflowNodeRunStatus, string> seen)
    {
        foreach (var nodeRun in nodeRuns)
        {
            var settles = DevWorkflowStateMachine.IsTerminal(nodeRun.Status)
                          || nodeRun.Status is DevWorkflowNodeRunStatus.Blocked or DevWorkflowNodeRunStatus.WaitingForApproval;
            if (!settles)
            {
                continue;
            }

            seen[nodeRun.Status] = nodeRun.NodeKey;
            if (nodeRun.WorkSessionId is not null)
            {
                AssertEx.True(nodeRun.WorkSessionSteps is not null,
                    $"'{nodeRun.NodeKey}' settled as {nodeRun.Status} owning a work session, so its cost must have been recorded.");
            }

            if (DevWorkflowStateMachine.IsTerminal(nodeRun.Status))
            {
                AssertEx.NotNull(nodeRun.RouteJson, $"'{nodeRun.NodeKey}' is terminal, so where it routed is answerable.");
            }
            else
            {
                AssertEx.Null(nodeRun.RouteJson, $"'{nodeRun.NodeKey}' is {nodeRun.Status}, which is a pause, not an answer.");
            }
        }
    }

    internal static void AssertEmptyTelemetry(DevWorkflowNodeRunSnapshot nodeRun, string because)
    {
        AssertEx.Null(nodeRun.InputTokens, because);
        AssertEx.Null(nodeRun.OutputTokens, because);
        AssertEx.Null(nodeRun.ReasoningTokens, because);
        AssertEx.Null(nodeRun.EstimatedInputTokens, because);
        AssertEx.Null(nodeRun.ProviderCalls, because);
        AssertEx.Null(nodeRun.ToolCalls, because);
        AssertEx.Null(nodeRun.ToolSchemaTokens, because);
        AssertEx.Null(nodeRun.ToolNamesJson, because);
        AssertEx.Null(nodeRun.AgentTurnMs, because);
        AssertEx.Null(nodeRun.ServedModelName, because);
        AssertEx.Null(nodeRun.RouteJson, because);
        AssertEx.Null(nodeRun.WorkSessionSteps, because);
    }

    private static DevWorkflowHarness NewHarness(StubDevWorkflowNodeTelemetrySource stub) =>
        new(services =>
        {
            services.RemoveAll<IDevWorkflowNodeTelemetrySource>();
            services.AddScoped<IDevWorkflowNodeTelemetrySource>(_ => stub);
        });

    /// <summary>Writes one step's consumption row onto the node run's session, exactly as the supervisor would.</summary>
    internal static async Task AppendStepConsumptionAsync(DevWorkflowHarness harness, Guid runId, string nodeKey, string detailJson)
    {
        var sessionId = await harness.ReadSessionIdAsync(runId, nodeKey).ConfigureAwait(false);
        await using var scope = harness.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                       .AppendEventAsync(new AppendWorkSessionEventCommand(sessionId,
                           WorkSessionVersions.Any,
                           WorkSessionEventTypes.StepEnded,
                           Guid.NewGuid(),
                           "Completed",
                           detailJson))
                       .ConfigureAwait(false);
    }

    private static string SerializeConsumption(ProviderCallConsumption consumption) =>
        JsonSerializer.Serialize(new WorkSessionStepConsumptionDetail(consumption.ProviderCalls,
                consumption.EstimatedInputTokens,
                consumption.ToolCallsCompleted,
                consumption.ProviderCallCap,
                consumption.AttachedBudgets,
                consumption.ToolSchemaTokens,
                consumption.ToolNames),
            ConsumptionJsonOptions);

    private static string DetailWithNames(IReadOnlyList<string> names) =>
        JsonSerializer.Serialize(new WorkSessionStepConsumptionDetail(ProviderCalls: 1,
                EstimatedInputTokens: 1,
                ToolCallsCompleted: names.Count,
                ProviderCallCap: 8,
                AttachedBudgets: 1,
                ToolSchemaTokens: 1,
                names),
            ConsumptionJsonOptions);

    /// <summary>Names long enough that sixteen of them overrun the column, so the clamp is exercised rather than argued.</summary>
    private static IReadOnlyList<string> LongNames(string prefix, int count) =>
        [.. Enumerable.Range(1, count).Select(index => prefix + index.ToString("D2", CultureInfo.InvariantCulture) + new string('x', count: 80))];

    private static DevWorkflowRoute ReadRoute(DevWorkflowNodeRunSnapshot nodeRun) =>
        AssertEx.NotNull(JsonSerializer.Deserialize<DevWorkflowRoute>(AssertEx.NotNull(nodeRun.RouteJson, $"'{nodeRun.NodeKey}' recorded no route."),
                ConsumptionJsonOptions),
            "The route document has to parse — the measurement recipe reads it.");
}
