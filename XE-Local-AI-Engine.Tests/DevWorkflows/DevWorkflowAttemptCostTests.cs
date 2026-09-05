namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The per-attempt half of the cost record. A node run keeps one row for its whole life and its cost columns
///     describe the LAST attempt, so a node that failed twice and succeeded reports one attempt of three — unless the
///     earlier attempts are captured onto their own retry events before the reset empties the row. These are the tests
///     that a total is <c>final row + every retry snapshot</c> and that neither source can lose an attempt.
/// </summary>
public sealed class DevWorkflowAttemptCostTests
{
    private const string BoundAgentId = "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b";

    /// <summary>One agent node with the default three attempts: fail, fail, succeed fits inside it.</summary>
    private const string SingleAgent = $$"""
                                         {
                                           "schemaVersion": 1,
                                           "nodes": [{ "nodeKey": "research", "nodeType": "Agent", "label": "Research", "agentDefinitionId": "{{BoundAgentId}}" }],
                                           "edges": []
                                         }
                                         """;

    /// <summary>The ten members of §4.1 (plus model_readiness_ms) that ADD UP across attempts, as their JSON names.</summary>
    private static readonly string[] AdditiveMembers =
    [
        "inputTokens",
        "outputTokens",
        "reasoningTokens",
        "estimatedInputTokens",
        "providerCalls",
        "toolCalls",
        "toolSchemaTokens",
        "agentTurnMs",
        "workSessionSteps",
        "modelReadinessMs"
    ];

    /// <summary>
    ///     The five that do not: a route belongs to one settle, a model name is not a quantity, names do not sum, and
    ///     the two VRAM figures read the box at one load rather than counting what an attempt spent.
    /// </summary>
    private static readonly string[] NonAdditiveMembers =
        ["routeJson", "servedModelName", "toolNamesJson", "vramFreeAtLoadBytes", "vramAdmittedBytes"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     T8: a real failure the retry policy re-attempts leaves the failing attempt's whole cost vector on its
    ///     <c>node.retry.scheduled</c> event, beside the members the retry policy itself wrote — and the node run's own
    ///     columns are empty afterwards, because the reset that starts the next attempt ran.
    /// </summary>
    [Test]
    public async Task ReAttempt_WritesPerAttemptDetail()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await SeedStepCostAsync(harness, runId, "research", providerCalls: 3, estimatedInputTokens: 900, toolCalls: 2, toolSchemaTokens: 250).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var scheduled = await ReadRetryDetailAsync(harness, runId, index: 0).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, scheduled["attempt"]?.GetValue<int>(), "The retry policy's own members are untouched.");
        AssertEx.Equal(DevWorkflowFailureClasses.ProviderError, scheduled["failureClass"]?.GetValue<string>());
        AssertEx.True(scheduled.ContainsKey("reason"), "The retry policy's own members are untouched.");
        AssertEx.True(scheduled.ContainsKey("delayUntil"), "The retry policy's own members are untouched.");

        foreach (var member in AdditiveMembers)
        {
            AssertEx.True(scheduled.ContainsKey(member), $"The failing attempt's '{member}' has to survive the reset that is about to empty the row.");
        }

        foreach (var member in NonAdditiveMembers)
        {
            AssertEx.False(scheduled.ContainsKey(member), $"'{member}' does not add up across attempts and must not be carried as if it did.");
        }

        AssertEx.Equal(expected: 3, scheduled["providerCalls"]?.GetValue<int>(), "And the numbers are the failing attempt's own.");
        AssertEx.Equal(expected: 900L, scheduled["estimatedInputTokens"]?.GetValue<long>());
        AssertEx.Equal(expected: 2, scheduled["toolCalls"]?.GetValue<int>());
        AssertEx.Equal(expected: 250L, scheduled["toolSchemaTokens"]?.GetValue<long>());

        var reset = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, reset.Attempt, "The next attempt is under way.");
        AssertEx.Null(reset.ProviderCalls, "And the row it runs on was emptied, which is exactly why the event above had to be written.");
        AssertEx.Null(reset.EstimatedInputTokens);
        AssertEx.Null(reset.ToolCalls);
    }

    /// <summary>
    ///     A detail payload that is not a JSON object is forwarded exactly as it arrived. There is nothing to merge
    ///     into, and rewriting it would corrupt whatever a later writer meant by it.
    /// </summary>
    [Test]
    public async Task ReAttempt_WhenTheDetailIsNotAnObject_ForwardsItVerbatim()
    {
        const string RawDetail = "\"a bare string, not an object\"";
        var inner = Substitute.For<IDevWorkflowStore>();
        var nodeRunId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        inner.GetNodeRunAsync(nodeRunId, Arg.Any<CancellationToken>()).Returns(NodeRun(runId, nodeRunId));
        inner.TransitionNodeRunAsync(Arg.Any<TransitionDevWorkflowNodeRunCommand>(), Arg.Any<CancellationToken>())
             .Returns(new DevWorkflowMutationResult(runId, Sequence: 1, Version: 2, DevWorkflowRunStatus.Running, GraphRevision: 0));

        var store = new PublishingDevWorkflowStore(inner,
            Substitute.For<IDevWorkflowEventPublisher>(),
            new RecordingTelemetryScopeFactory(inner, new StubDevWorkflowNodeTelemetrySource { Answer = new DevWorkflowNodeTelemetry(InputTokens: 5) }),
            new DevWorkflowGraphCache(),

            // Its own admission pool. The real one is a container singleton, and this assertion is about what the
            // collector answers — not about which other suite happened to be holding a slot at the time.
            new DevWorkflowNodeTelemetryCollectionPool(slots: 1),
            NullLogger<PublishingDevWorkflowStore>.Instance);

        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(runId,
                          nodeRunId,
                          DevWorkflowVersions.Any,
                          DevWorkflowNodeRunStatus.Pending,
                          DetailJson: RawDetail,
                          IncrementAttempt: true,
                          ClearWorkSession: true))
                      .ConfigureAwait(false);

        _ = await inner.Received(1)
                       .TransitionNodeRunAsync(Arg.Is<TransitionDevWorkflowNodeRunCommand>(forwarded => forwarded.DetailJson == RawDetail),
                           Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     T10: fail, fail, succeed — the node row carries attempt three alone, the two retry events carry attempts one
    ///     and two, and <c>row + snapshot₁ + snapshot₂</c> equals what the three attempts actually spent, for every one
    ///     of the ten additive members. A reflection assertion pins the member list against the telemetry record
    ///     itself, so a column added to it later cannot quietly stop being carried.
    /// </summary>
    [Test]
    public async Task FailFailSuccess_TotalsEqualTheSumOfThreeAttempts()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await harness.StartRunAsync(SingleAgent).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await SeedStepCostAsync(harness, runId, "research", providerCalls: 2, estimatedInputTokens: 100, toolCalls: 1, toolSchemaTokens: 10).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await SeedStepCostAsync(harness, runId, "research", providerCalls: 4, estimatedInputTokens: 200, toolCalls: 3, toolSchemaTokens: 20).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research", AgentWorkSessionStatus.Failed).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await SeedStepCostAsync(harness, runId, "research", providerCalls: 8, estimatedInputTokens: 400, toolCalls: 5, toolSchemaTokens: 30).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "research").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var row = await harness.ReadNodeRunAsync(runId, "research").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, row.Status);
        AssertEx.Equal(expected: 3, row.Attempt);
        AssertEx.Equal(expected: 8, row.ProviderCalls, "The row is the LAST attempt, and nothing else.");
        AssertEx.Equal(expected: 400L, row.EstimatedInputTokens);

        var first = await ReadRetryDetailAsync(harness, runId, index: 0).ConfigureAwait(false);
        var second = await ReadRetryDetailAsync(harness, runId, index: 1).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, first["attempt"]?.GetValue<int>());
        AssertEx.Equal(expected: 2, second["attempt"]?.GetValue<int>());

        // row + snapshot₁ + snapshot₂, member by member, over all nine. The two sources cannot double-count: the reset
        // empties the row before the next attempt fills it.
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var member in AdditiveMembers)
        {
            totals[member] = FromRow(row, member) + Number(first, member) + Number(second, member);
        }

        AssertEx.Equal(expected: 14L, totals["providerCalls"], "Two plus four plus eight is what the node actually spent.");
        AssertEx.Equal(expected: 700L, totals["estimatedInputTokens"]);
        AssertEx.Equal(expected: 9L, totals["toolCalls"]);
        AssertEx.Equal(expected: 60L, totals["toolSchemaTokens"]);
        AssertEx.Equal(expected: 0L, totals["workSessionSteps"], "No step was taken on the fake sessions, and zero summed three times is still zero.");
        AssertEx.Equal(expected: 0L, totals["inputTokens"], "No chat-run envelope was written here, so the provider-reported half is absent rather than wrong.");
        AssertEx.Equal(expected: 0L, totals["outputTokens"]);
        AssertEx.Equal(expected: 0L, totals["reasoningTokens"]);
        AssertEx.Equal(expected: 0L, totals["agentTurnMs"]);

        AssertMergedMembersMatchTheTelemetryRecord(first);
    }

    /// <summary>
    ///     T10's second write path. A cross-node retry never crosses the same-node transition — it routes through the
    ///     store's own retry-route command — so the enrichment has to be on that method too, or every fix loop loses an
    ///     attempt from every total.
    /// </summary>
    [Test]
    public async Task ACrossNodeRetry_CarriesTheFailingAttemptsCostToo()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("lint", FakeDevWorkflowToolCommands.Passing());
        harness.Tools.Answer("test", FakeDevWorkflowToolCommands.Failing(), FakeDevWorkflowToolCommands.Passing());
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.FanOutFixLoop, developmentProjectId: Guid.NewGuid()).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await SeedStepCostAsync(harness, runId, "implement", providerCalls: 6, estimatedInputTokens: 512, toolCalls: 4, toolSchemaTokens: 64).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var implement = await harness.ReadNodeRunAsync(runId, "implement").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, implement.Attempt, "The fix loop re-ran the node the failing check was judging.");

        var events = await harness.ReadEventsAsync(runId).ConfigureAwait(false);
        var reset = events.Where(entry => entry.EventType == DevWorkflowEventTypes.NodeRetryScheduled && entry.NodeRunId == implement.Id)
                          .Select(entry => AssertEx.NotNull(JsonNode.Parse(AssertEx.NotNull(entry.DetailJson)) as JsonObject))
                          .Last();

        AssertEx.Equal(expected: 6, reset["providerCalls"]?.GetValue<int>(), "The reset the ROUTE wrote carries the attempt it replaced, exactly as a same-node re-attempt does.");
        AssertEx.Equal(expected: 512L, reset["estimatedInputTokens"]?.GetValue<long>());
        AssertEx.Equal(expected: 4, reset["toolCalls"]?.GetValue<int>());
        AssertMergedMembersMatchTheTelemetryRecord(reset);
        AssertEx.Null(implement.ProviderCalls, "And the row it re-runs on was emptied by the same reset.");
    }

    /// <summary>
    ///     The merged member list is the telemetry record minus its five non-additive members — asserted by
    ///     reflection, so a column added to §4.1 later is carried here too or it is not additive.
    /// </summary>
    private static void AssertMergedMembersMatchTheTelemetryRecord(JsonObject detail)
    {
        var expected = typeof(DevWorkflowNodeTelemetry).GetProperties()
                                                       .Select(static property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
                                                       .Where(name => !NonAdditiveMembers.Contains(name, StringComparer.Ordinal))
                                                       .Order(StringComparer.Ordinal);

        AssertEx.Equal(string.Join(", ", AdditiveMembers.Order(StringComparer.Ordinal)),
            string.Join(", ", expected),
            "The additive vector IS the telemetry record minus its five non-additive members — nothing enumerates it by hand.");

        foreach (var member in AdditiveMembers)
        {
            AssertEx.True(detail.ContainsKey(member), $"'{member}' is additive, so every retry snapshot has to carry it.");
        }
    }

    /// <summary>Writes one step's consumption row with known counts onto the node run's current session.</summary>
    private static Task SeedStepCostAsync(DevWorkflowHarness harness,
        Guid runId,
        string nodeKey,
        int providerCalls,
        long estimatedInputTokens,
        int toolCalls,
        long toolSchemaTokens) =>
        DevWorkflowNodeRunTelemetryTests.AppendStepConsumptionAsync(harness,
            runId,
            nodeKey,
            JsonSerializer.Serialize(new WorkSessionStepConsumptionDetail(providerCalls,
                    estimatedInputTokens,
                    toolCalls,
                    ProviderCallCap: 8,
                    AttachedBudgets: 1,
                    toolSchemaTokens),
                JsonOptions));

    private static async Task<JsonObject> ReadRetryDetailAsync(DevWorkflowHarness harness, Guid runId, int index)
    {
        var events = await harness.ReadEventsAsync(runId).ConfigureAwait(false);
        var retries = events.Where(static entry => entry.EventType == DevWorkflowEventTypes.NodeRetryScheduled).ToList();
        AssertEx.True(retries.Count > index, $"Expected at least {index + 1} retry event(s); the run wrote {retries.Count}.");
        return AssertEx.NotNull(JsonNode.Parse(AssertEx.NotNull(retries[index].DetailJson)) as JsonObject,
            "A retry detail has to be a JSON object — the merge and the recipe both read it as one.");
    }

    private static long Number(JsonObject detail, string member) =>
        detail[member] is { } value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<long>() : 0;

    private static long FromRow(DevWorkflowNodeRunSnapshot row, string member) =>
        member switch
        {
            "inputTokens" => row.InputTokens ?? 0,
            "outputTokens" => row.OutputTokens ?? 0,
            "reasoningTokens" => row.ReasoningTokens ?? 0,
            "estimatedInputTokens" => row.EstimatedInputTokens ?? 0,
            "providerCalls" => row.ProviderCalls ?? 0,
            "toolCalls" => row.ToolCalls ?? 0,
            "toolSchemaTokens" => row.ToolSchemaTokens ?? 0,
            "agentTurnMs" => row.AgentTurnMs ?? 0,
            "workSessionSteps" => row.WorkSessionSteps ?? 0,
            "modelReadinessMs" => row.ModelReadinessMs ?? 0,
            _ => throw new AssertionException($"'{member}' is not an additive telemetry column.")
        };

    private static DevWorkflowNodeRunSnapshot NodeRun(Guid runId, Guid nodeRunId) =>
        new(nodeRunId,
            runId,
            "research",
            DevWorkflowNodeType.Agent,
            Attempt: 1,
            MaxAttempts: 3,
            SessionResumes: 0,
            DevWorkflowNodeRunStatus.Running,
            QueueReason: null,
            PendingDecisionKind: null,
            Sequence: 1,
            WorkSessionId: Guid.NewGuid(),
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
            StartedAtUtc: 1,
            EndedAtUtc: null,
            CreatedAtUtc: 0);
}
