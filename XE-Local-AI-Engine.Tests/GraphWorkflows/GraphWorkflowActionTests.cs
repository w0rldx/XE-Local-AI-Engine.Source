namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A Tool node naming an ACTION, a custom tool flagged for unattended use (ADR 0021): refused without the
///     definition's flag at save and at start, and run exactly once, never re-fired by the retry stage or a restart.
/// </summary>
/// <remarks>
///     Which tools ARE actions is decided by the real invocation service and pinned in
///     <see cref="ToolInvocationServiceTests" />; here the scripted service lists one, so what is under test is the
///     gate, the definition flag and the single attempt.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowActionTests
{
    [ClassDataSource<GraphWorkflowToolHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowToolHostFixture Host { get; init; }

    [Test]
    public async Task Parse_GivesACustomToolNodeOneAttempt_WhateverItDeclares()
    {
        var action = GraphWorkflowGraph.Parse(Graph("custom__lamp", """, "maxAttempts": 5"""));
        var builtin = GraphWorkflowGraph.Parse(Graph("list_files", """, "maxAttempts": 5"""));

        AssertEx.Equal(expected: 1, action.Nodes["act"].MaxAttempts, "every reader of a pinned run graph sees one attempt, so nothing re-fires the side effect.");
        AssertEx.Equal(expected: 5, builtin.Nodes["act"].MaxAttempts, "a built-in tool keeps what its author declared.");
        AssertEx.Equal(expected: 1, GraphWorkflowGraph.Parse(Graph("custom__lamp")).Nodes["act"].MaxAttempts, "and the work-node default of three is overridden too.");
    }

    [Test]
    public async Task Create_WithAnActionNode_IsRefusedKeyedByTheNode()
    {
        const string tool = "custom__probe_create";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool { IsAction = true });
        await using var scope = harness.Services.CreateAsyncScope();

        var refusal = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() =>
            scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>().CreateAsync($"Action {Guid.NewGuid():N}", description: null, Graph(tool)));

        var error = AssertEx.NotNull(refusal.Result.Errors.SingleOrDefault(), refusal.Message);
        AssertEx.Equal("act", error.Key, "keyed by node, so the editor draws it on the node naming the action.");
        AssertEx.Contains(error.Message, "Allow actions", message: "and it says what to switch on.");
    }

    [Test]
    public async Task AFlaggedDefinition_RunsTheActionOnce_AndHandsTheDefinitionIdToTheCall()
    {
        const string tool = "custom__probe_run";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool { IsAction = true });
        var definitionId = await FlaggedDefinitionAsync(harness, tool);

        var runId = await harness.StartRunOfAsync(definitionId);
        await AdvanceUntilRunEndsAsync(harness, runId);

        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);
        AssertEx.Equal(expected: 1, harness.Tools.CallCountFor(tool));
        AssertEx.Equal(definitionId, harness.Tools.CallFor(tool).Context.DefinitionId, "the audit row names the definition, so the call must carry it.");
    }

    /// <summary>
    ///     A shared graph arrives unable to act rather than refused: the import creates it without the flag, the start
    ///     refuses the action node, and once the operator flags the definition the same graph runs.
    /// </summary>
    [Test]
    public async Task AnImportedActionGraph_IsCreatedUnflagged_RefusedAtStart_AndRunsOnceFlagged()
    {
        const string tool = "custom__probe_import";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool { IsAction = true });
        using var client = harness.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/graph-workflows/definitions/import")
        {
            Content = new StringContent($$"""{"format":"xe-graph-workflow","formatVersion":1,"name":"Shared {{Guid.NewGuid():N}}","graph":{{Graph(tool)}}}""",
                Encoding.UTF8,
                "application/json")
        };
        harness.Factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode, body);
        using var created = JsonDocument.Parse(body);
        AssertEx.False(created.RootElement.GetProperty("allowsActions").GetBoolean(), "an import never brings authority with it.");
        var definitionId = created.RootElement.GetProperty("id").GetGuid();

        var refusal = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() => harness.StartRunOfAsync(definitionId));
        AssertEx.Equal("act", AssertEx.NotNull(refusal.Result.Errors.SingleOrDefault(), refusal.Message).Key);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();
            var current = await definitions.GetAsync(definitionId);
            _ = await definitions.UpdateAsync(definitionId, current.Version, name: null, description: null, graphJson: null, allowsActions: true);
        }

        var runId = await harness.StartRunOfAsync(definitionId);
        await AdvanceUntilRunEndsAsync(harness, runId);

        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);
        AssertEx.Equal(expected: 1, harness.Tools.CallCountFor(tool));
    }

    /// <summary>Turning the flag off is never refused, so revoking authority always works; the next start refuses.</summary>
    [Test]
    public async Task Start_AfterTheFlagIsTurnedOff_IsRefusedKeyedByTheNode()
    {
        const string tool = "custom__probe_revoked";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool { IsAction = true });
        var definitionId = await FlaggedDefinitionAsync(harness, tool);
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();
            var current = await definitions.GetAsync(definitionId);
            AssertEx.False((await definitions.UpdateAsync(definitionId, current.Version, name: null, description: null, graphJson: null, allowsActions: false)).AllowsActions);
        }

        var refusal = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() => harness.StartRunOfAsync(definitionId));

        AssertEx.Equal("act", AssertEx.NotNull(refusal.Result.Errors.SingleOrDefault(), refusal.Message).Key);
        AssertEx.Equal(expected: 0, harness.Tools.CallCountFor(tool));
    }

    /// <summary>A failed action is never re-attempted, whatever its node declares: the failing write is <c>AttemptsExhausted</c>.</summary>
    [Test]
    [Arguments(ToolInvocationOutcomeKind.Faulted, "custom__probe_faulted")]
    [Arguments(ToolInvocationOutcomeKind.Timeout, "custom__probe_timeout")]
    public async Task AFailedAction_IsNeverRetried(ToolInvocationOutcomeKind kind, string tool)
    {
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool { IsAction = true, Kind = kind, Reason = "action-failed" });
        var runId = await harness.StartRunOfAsync(await FlaggedDefinitionAsync(harness, tool, """, "maxAttempts": 3"""));

        await AdvanceUntilRunEndsAsync(harness, runId);

        var act = await harness.ReadNodeRunAsync(runId, "act");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, act.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.AttemptsExhausted, act.FailureClass);
        AssertEx.Equal(expected: 1, act.Attempt);
        AssertEx.Equal(expected: 1, harness.Tools.CallCountFor(tool), "the side effect fired once.");
        AssertEx.Empty((await harness.ReadEventsAsync(runId)).Where(static entry => entry.EventType == "node.retried"));
        AssertEx.Equal(GraphWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId)).Status);
    }

    /// <summary>A failed action keeps what its target answered in the node's output; the node's error stays the structural reason.</summary>
    [Test]
    public async Task AFailedAction_KeepsTheExecutorTextInItsOutput_AndTheStructuralReasonAsItsError()
    {
        const string tool = "custom__probe_failed_text";
        const string text = "the lamp answered 503";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool { IsAction = true, Kind = ToolInvocationOutcomeKind.Faulted, Reason = "action-failed", FailedResult = text });
        var runId = await harness.StartRunOfAsync(await FlaggedDefinitionAsync(harness, tool));

        await AdvanceUntilRunEndsAsync(harness, runId);

        var act = await harness.ReadNodeRunAsync(runId, "act");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, act.Status);
        AssertEx.Equal("action-failed", act.Error, "the reason stays structural; the text never moves into it.");
        using var document = JsonDocument.Parse(AssertEx.NotNull(act.OutputJson, "a failed action node carries its output document."));
        AssertEx.Equal(GraphWorkflowNodeOutputStatuses.Failed, document.RootElement.GetProperty("status").GetString());
        AssertEx.Equal(text, document.RootElement.GetProperty("output").GetProperty("result").GetString());
    }

    /// <summary>A failed action's text over the document cap falls back to the empty envelope, and the row still fails with its reason.</summary>
    [Test]
    public async Task AFailedActionTextOverTheCap_FallsBackToTheEmptyEnvelope_AndStillFails()
    {
        const string tool = "custom__probe_failed_long";
        await using var harness = new GraphWorkflowHarness(Host);
        var overTheCap = new string('a', harness.CurrentOptions().MaxOutputJsonBytes + 1);
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool { IsAction = true, Kind = ToolInvocationOutcomeKind.Faulted, Reason = "action-failed", FailedResult = overTheCap });
        var runId = await harness.StartRunOfAsync(await FlaggedDefinitionAsync(harness, tool));

        await AdvanceUntilRunEndsAsync(harness, runId);

        var act = await harness.ReadNodeRunAsync(runId, "act");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, act.Status);
        AssertEx.Equal("action-failed", act.Error);
        using var document = JsonDocument.Parse(AssertEx.NotNull(act.OutputJson, "the empty envelope still fits under the cap."));
        AssertEx.Equal(GraphWorkflowNodeOutputStatuses.Failed, document.RootElement.GetProperty("status").GetString());
        AssertEx.Equal(JsonValueKind.Object, document.RootElement.GetProperty("output").ValueKind);
        AssertEx.Empty(document.RootElement.GetProperty("output").EnumerateObject());
    }

    /// <summary>
    ///     An action in flight at a restart may already have reached its target, so it is failed, not re-attempted, and
    ///     the run fails naming the node. A private host: recovery sweeps every in-flight row in the database.
    /// </summary>
    [Test]
    public async Task AnInterruptedAction_FailsTheRunNamingTheNode_AndIsNotReAttempted()
    {
        const string tool = "custom__probe_restart";
        await using var harness = GraphWorkflowHarness.PrivateToolHost();
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool { IsAction = true });
        var runId = await harness.StartRunOfAsync(await FlaggedDefinitionAsync(harness, tool, """, "maxAttempts": 3"""));

        // Out of Pending, then Start; the action is then Pending and stood Running through the store, as a crash would leave it.
        _ = await harness.AdvanceAsync(runId);
        _ = await harness.AdvanceAsync(runId);
        await harness.TransitionNodeRunAsync(runId, "act", GraphWorkflowNodeRunStatus.Running);

        await new GraphWorkflowStartupReconciler(harness.Services.GetRequiredService<IServiceScopeFactory>(),
                harness.Services.GetRequiredService<ILogger<GraphWorkflowStartupReconciler>>())
            .StartAsync(CancellationToken.None);
        _ = harness.CreateReplacementDispatcher();
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        var run = await harness.ReadRunAsync(runId);
        var act = await harness.ReadNodeRunAsync(runId, "act");
        AssertEx.Equal(GraphWorkflowRunStatus.Failed, run.Status);
        var failed = (await harness.ReadEventsAsync(runId)).Single(static entry => entry.EventType == "run.failed");
        using var detail = JsonDocument.Parse(AssertEx.NotNull(failed.DetailJson));
        AssertEx.Contains(detail.RootElement.GetProperty("reason").GetString(), "'act'", message: "the run names the node that may or may not have acted.");
        AssertEx.Equal(GraphWorkflowFailureClass.Interrupted, act.FailureClass);
        AssertEx.Equal(expected: 1, act.Attempt);
        AssertEx.Equal(expected: 0, harness.Tools.CallCountFor(tool), "nothing re-fired it.");
        AssertEx.Empty((await harness.ReadEventsAsync(runId)).Where(static entry => entry.EventType == "node.retried"));
    }

    /// <summary>
    ///     Ticks until the run ends. A tick that finds the call still in flight writes nothing, which a quiescent loop
    ///     would misread as a finished run.
    /// </summary>
    private static Task AdvanceUntilRunEndsAsync(GraphWorkflowHarness harness, Guid runId) =>
        harness.AdvanceUntilAsync(runId,
            async () => GraphWorkflowStateMachine.IsTerminal((await harness.ReadRunAsync(runId)).Status),
            "the action run never ended.");

    /// <summary>
    ///     A definition naming <paramref name="tool" /> with actions allowed. Create never takes the flag, so it is
    ///     created over a declared plain tool and then updated with the flag and the action graph together.
    /// </summary>
    private static async Task<Guid> FlaggedDefinitionAsync(GraphWorkflowHarness harness, string tool, string? nodeExtras = null)
    {
        const string plain = "probe_action_placeholder";
        harness.Tools.Declare(plain);
        await using var scope = harness.Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();
        var created = await definitions.CreateAsync($"Action {Guid.NewGuid():N}", description: null, Graph(plain));
        AssertEx.False(created.AllowsActions, "create never allows actions.");
        var updated = await definitions.UpdateAsync(created.Id, created.Version, name: null, description: null, Graph(tool, nodeExtras), allowsActions: true);
        AssertEx.True(updated.AllowsActions);
        return created.Id;
    }

    private static string Graph(string toolName, string? nodeExtras = null) =>
        $$"""
          {
            "schemaVersion": 1,
            "nodes": [
              { "key": "start", "kind": "Start" },
              { "key": "act", "kind": "Tool"{{nodeExtras}}, "config": { "toolName": "{{toolName}}" } },
              { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
            ],
            "edges": [
              { "key": "e1", "from": "start", "to": "act" },
              { "key": "e2", "from": "act", "to": "done" }
            ]
          }
          """;
}
