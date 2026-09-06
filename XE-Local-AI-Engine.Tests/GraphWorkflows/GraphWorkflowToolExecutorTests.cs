namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What a <c>Tool</c> node RUNS: the arguments it composes, the document it produces, and how each outcome the
///     invocation service can answer with becomes a row.
///     <para>
///         Two hosts, and the split is deliberate. Anything about what a real tool call does — that a node reaches one
///         at all, and that a tool's own guard is what contains its arguments — runs against the REAL invocation
///         service on the plain <see cref="GraphWorkflowHostFixture" />. Anything about an outcome the real service
///         cannot be asked for on demand — a fault, a timeout, an oversized answer — runs against the scripted one on
///         <see cref="GraphWorkflowToolHostFixture" />. The envelope itself is faked nowhere: which tools may run is
///         <see cref="ToolInvocationServiceTests" />' subject, at the one place it is enforced.
///     </para>
///     <para>
///         Every scripted test names its own tool, which is what keeps a shared host's tests out of each other's way.
///         The lane's own contract — slots, stop, forget, restart — is <see cref="GraphWorkflowToolLaneTests" />.
///     </para>
/// </summary>
public sealed class GraphWorkflowToolExecutorTests
{
    [ClassDataSource<GraphWorkflowToolHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowToolHostFixture ScriptedHost { get; init; }

    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture RealHost { get; init; }

    /// <summary>
    ///     A binding OVERWRITES a literal of the same name — a literal is the default the author typed, a binding is
    ///     what the run computed — and the resolved element is inserted verbatim, so a bound number reaches the tool's
    ///     schema as a number rather than as a quoted one.
    /// </summary>
    [Test]
    public async Task ABindingBeatsALiteralOfTheSameName_AndKeepsTheBoundValuesType()
    {
        const string tool = "probe_merge";
        await using var harness = new GraphWorkflowHarness(ScriptedHost);
        harness.Tools.Declare(tool);
        var runId = await harness.StartRunAsync(Graph(tool,
                                     """
                                     , "arguments": { "path": "typed-by-the-author.md", "limit": 1, "recursive": true }
                                     , "argumentBindings": { "path": "run.input.path", "limit": "run.input.limit" }
                                     """),
                                 """{"path":"computed-by-the-run.md","limit":7}""")
                                 .ConfigureAwait(false);

        _ = await SettleToolAsync(harness, runId).ConfigureAwait(false);

        var arguments = harness.Tools.CallFor(tool).ArgumentsJson;
        AssertEx.Contains(arguments, "\"path\":\"computed-by-the-run.md\"", message: "the binding wins over the literal it shadows.");
        AssertEx.Contains(arguments, "\"limit\":7", message: "and it arrives as the NUMBER the document carried, not as a string.");
        AssertEx.Contains(arguments, "\"recursive\":true", message: "a literal nothing binds is still sent.");
    }

    /// <summary>
    ///     A binding whose path the input document does not carry refuses the call before it is made. The reason names
    ///     the argument and the path and NEVER the document, which carries whatever an upstream node wrote.
    /// </summary>
    [Test]
    public async Task ABindingThePathDoesNotResolve_FailsValidationFailedNamingTheArgumentAndThePath()
    {
        const string tool = "probe_unbound";
        await using var harness = new GraphWorkflowHarness(ScriptedHost);
        harness.Tools.Declare(tool);
        var runId = await harness.StartRunAsync(Graph(tool, """, "argumentBindings": { "path": "run.input.absent" }"""),
                                 """{"secret":"never-repeat-this"}""")
                                 .ConfigureAwait(false);

        var call = await SettleToolAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, call.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, call.FailureClass, "an unresolvable binding resolves the same way next time, so it is never retried.");
        var reason = AssertEx.NotNull(call.Error, "a refused node run says why.");
        AssertEx.Contains(reason, "'path'");
        AssertEx.Contains(reason, "run.input.absent");
        AssertEx.False(reason.Contains("never-repeat-this", StringComparison.Ordinal),
            "the document is not quoted back: it carries whatever an upstream node wrote.");
        AssertEx.Equal(expected: 0, harness.Tools.CallCountFor(tool), "the tool was never invoked at all.");
    }

    /// <summary>
    ///     A tool that answers with text lands as a STRING under <c>output.result</c>, inside the common envelope the
    ///     single document writer composes — camelCase, with the status, the attempt and the branch beside it.
    /// </summary>
    [Test]
    public async Task AToolAnsweringWithText_LandsAsAStringInsideTheCommonEnvelope()
    {
        const string tool = "probe_text";
        await using var harness = new GraphWorkflowHarness(ScriptedHost);
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool(Result: "42 files, none of them interesting"));
        var runId = await harness.StartRunAsync(Graph(tool)).ConfigureAwait(false);

        var call = await SettleToolAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, call.Status);
        var document = AssertEx.NotNull(call.OutputJson, "a settled tool node always carries its output document.");
        AssertEx.Contains(document, "\"status\":\"succeeded\"", message: "camelCase, through the single writer, like every other kind's document.");
        AssertEx.Contains(document, "\"attempt\":1");
        AssertEx.Contains(document, "\"branch\":null");
        AssertEx.Contains(document, "\"result\":\"42 files, none of them interesting\"");
    }

    /// <summary>
    ///     A tool that answers with a JSON object keeps it as JSON, which is the whole reason for the try-parse: a
    ///     <c>Condition</c> passes its predecessor's output through verbatim, so an edge behind one can dot-path into
    ///     the tool's answer and route the run on it.
    /// </summary>
    [Test]
    public async Task AToolAnsweringWithJson_KeepsItStructuredForADownstreamConditionToRouteOn()
    {
        // The graph fixture names this tool, and only this test uses that fixture.
        await using var harness = new GraphWorkflowHarness(ScriptedHost);
        harness.Tools.Script("probe_json", new GraphWorkflowScriptedTool(Result: """{"ok":true,"hits":3}"""));
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.ToolThenCondition).ConfigureAwait(false);

        await harness.AdvanceUntilAsync(runId,
                async () => (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status == GraphWorkflowRunStatus.Completed,
                "the tool node's answer never routed the run to an end.")
            .ConfigureAwait(false);

        AssertEx.Contains(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false)).OutputJson, "the tool node carries a document."),
            "\"result\":{\"ok\":true,\"hits\":3}",
            message: "embedded as JSON rather than as a quoted string, or nothing downstream could read into it.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "okend").ConfigureAwait(false)).Status,
            "the condition read output.result.ok off the pass-through and fired the ok edge.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "badend").ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The outcomes a re-attempt could never answer differently, mapped to the row each becomes. All three refusals
    ///     share <c>ValidationFailed</c> and are therefore never retried, so what the row says a tick later is what the
    ///     lane wrote.
    /// </summary>
    [Test]
    [Arguments("probe_unknown", ToolInvocationOutcomeKind.UnknownTool, GraphWorkflowNodeRunStatus.Failed, GraphWorkflowFailureClass.ValidationFailed)]
    [Arguments("probe_refused", ToolInvocationOutcomeKind.NotInvocable, GraphWorkflowNodeRunStatus.Failed, GraphWorkflowFailureClass.ValidationFailed)]
    [Arguments("probe_badargs", ToolInvocationOutcomeKind.InvalidArguments, GraphWorkflowNodeRunStatus.Failed, GraphWorkflowFailureClass.ValidationFailed)]
    [Arguments("probe_stopped", ToolInvocationOutcomeKind.Cancelled, GraphWorkflowNodeRunStatus.Cancelled, GraphWorkflowFailureClass.Cancelled)]
    public async Task AnOutcomeNothingWillRetry_BecomesTheStatusAndFailureClassItsRowSaysItDoes(string tool,
        ToolInvocationOutcomeKind kind,
        GraphWorkflowNodeRunStatus status,
        GraphWorkflowFailureClass failureClass)
    {
        const string reason = "the fake refused, and said so structurally";
        await using var harness = new GraphWorkflowHarness(ScriptedHost);
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool(kind, Reason: reason));
        var runId = await harness.StartRunAsync(Graph(tool)).ConfigureAwait(false);

        var call = await SettleToolAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(status, call.Status);
        AssertEx.Equal(failureClass, call.FailureClass);
        AssertEx.Equal(reason, call.Error, "the service's own reason is repeated verbatim: it is structural by contract.");

        // "Never re-attempted" is the half a first-terminal read cannot see, so it is asserted rather than implied: one
        // more tick leaves the row exactly where the lane put it, on the attempt it was put there with.
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        var later = await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false);
        AssertEx.Equal(status, later.Status);
        AssertEx.Equal(expected: 1, later.Attempt);
        AssertEx.Equal(expected: 1, harness.Tools.CallCountFor(tool), "and the tool was asked once, whatever it answered.");
    }

    /// <summary>
    ///     The two outcomes a second attempt COULD answer differently, and the one place the class each was written
    ///     with survives.
    ///     <para>
    ///         The settling write and the re-attempt happen in the same tick — the tick polls, then retries what it
    ///         just failed — so no reader ever sees the failed row standing. The <c>node.retried</c> event carries the
    ///         class of the attempt being replaced, because the move back to <c>Pending</c> clears the row's failure
    ///         fields: a re-attempt must not report the previous try's outcome while it runs.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments("probe_broken", ToolInvocationOutcomeKind.Faulted, GraphWorkflowFailureClass.NodeFailed)]
    [Arguments("probe_slow", ToolInvocationOutcomeKind.Timeout, GraphWorkflowFailureClass.Timeout)]
    public async Task ARetryableOutcome_IsRecordedWithItsOwnClassOnTheAttemptThatFailed(string tool,
        ToolInvocationOutcomeKind kind,
        GraphWorkflowFailureClass failureClass)
    {
        await using var harness = new GraphWorkflowHarness(ScriptedHost);
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool(kind, Reason: "the fake could not answer"));

        // The shipped three attempts, deliberately: at maxAttempts 1 every retryable class reports AttemptsExhausted
        // on its only try, and the two would be indistinguishable.
        var runId = await harness.StartRunAsync(Graph(tool)).ConfigureAwait(false);

        await harness.AdvanceUntilAsync(runId,
                async () => (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status == GraphWorkflowRunStatus.Failed,
                "the tool node never spent its attempt budget.")
            .ConfigureAwait(false);

        var retried = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).First(static entry => entry.EventType == "node.retried");
        AssertEx.Contains(retried.DetailJson, failureClass.ToString(), message: "the class the lane wrote is what the retry replaced.");

        var call = await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false);
        AssertEx.Equal(expected: 3, call.Attempt, "a Tool node gets three attempts by default, and a retryable failure spends them all.");
        AssertEx.Equal(GraphWorkflowFailureClass.AttemptsExhausted,
            call.FailureClass,
            "the attempt that uses up the budget says the budget is why nothing will try again.");
        AssertEx.Equal(expected: 3, harness.Tools.CallCountFor(tool), "each attempt is a real second call, not a re-read of the first answer.");
    }

    /// <summary>
    ///     A knowledge-base search may legitimately answer with fifty thousand characters, so the cap is a reachable
    ///     outcome rather than a theoretical one. The refusal names the node AND the tool, because "which node" alone
    ///     does not tell an operator what to shrink.
    /// </summary>
    [Test]
    public async Task AResultOverTheDocumentCap_FailsOutputTooLargeNamingTheNodeAndTheTool()
    {
        const string tool = "probe_verbose";

        // A private host: the cap is host-level configuration, and 1024 is the validator's floor — the smallest cap a
        // real node can be configured with.
        await using var harness = GraphWorkflowHarness.PrivateToolHost(("GraphWorkflows:MaxOutputJsonBytes", "1024"));
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool(Result: new string('a', count: 4096)));
        var runId = await harness.StartRunAsync(Graph(tool)).ConfigureAwait(false);

        var call = await SettleToolAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, call.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.OutputTooLarge, call.FailureClass, "the same call composes the same bytes, so it is not retried.");
        AssertEx.Contains(call.Error, "'call'");
        AssertEx.Contains(call.Error, tool);
    }

    /// <summary>
    ///     The real service, the real catalog, the real executable: a <c>Tool</c> node reaches an in-process built-in
    ///     and its arguments arrive intact.
    /// </summary>
    [Test]
    public async Task ARealBuiltInTool_RunsInProcessAndAnswersWithItsOwnText()
    {
        await using var harness = new GraphWorkflowHarness(RealHost);
        var runId = await harness.StartRunAsync(Graph("Calculate", """, "arguments": { "expression": "2+2" }""")).ConfigureAwait(false);

        var call = await SettleToolAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, call.Status, call.Error);
        AssertEx.Contains(Result(call).GetString(), "4", message: "the node's literal arguments reached the real executable.");
    }

    /// <summary>
    ///     <b>C17, the model-controlled arguments.</b> A binding reads the node's input document, whose upstream map
    ///     carries whatever the node above wrote — so an agent's generated text can choose <c>read_file</c>'s path.
    ///     That is the design, not a hole: D6 reasons about WHICH tool may run and never about who authors its
    ///     arguments, and containment is each tool's own guard.
    ///     <para>
    ///         What comes back is <c>WorkspacePathGuard</c>'s own refusal, verbatim, as a perfectly successful
    ///         invocation: the node run SUCCEEDS carrying the sentence that says nothing was read. A node whose
    ///         arguments escaped the workspace would fail here instead of recording a refusal, which is the difference
    ///         this pins.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ABindingReachingOutsideTheWorkspace_ComesBackAsTheToolsOwnRefusalRatherThanAFile()
    {
        await using var harness = new GraphWorkflowHarness(RealHost);
        var runId = await harness.StartRunAsync(Graph("read_file", """, "argumentBindings": { "path": "run.input.path" }"""),
                                 """{"path":"../../etc/passwd"}""")
                                 .ConfigureAwait(false);

        var call = await SettleToolAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, call.Status, call.Error);
        AssertEx.Equal("read_file rejected: the path traverses above the workspace root and was rejected.",
            Result(call).GetString(),
            "the tool's own guard answered the traversal; nothing outside the workspace was read.");
    }

    /// <summary>A linear <c>Start → Tool → End</c> graph whose tool node the caller configures.</summary>
    private static string Graph(string toolName, string? toolConfig = null, string? nodeExtras = null) =>
        $$"""
          {
            "schemaVersion": 1,
            "nodes": [
              { "key": "start", "kind": "Start" },
              { "key": "call", "kind": "Tool"{{nodeExtras}}, "config": { "toolName": "{{toolName}}"{{toolConfig}} } },
              { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
            ],
            "edges": [
              { "key": "e1", "from": "start", "to": "call" },
              { "key": "e2", "from": "call", "to": "done" }
            ]
          }
          """;

    /// <summary>
    ///     Ticks until the tool node run is terminal, and answers the row it settled as.
    ///     <para>
    ///         The FIRST terminal, deliberately: a retryable failure is re-attempted a tick or two later, and a test
    ///         asking what an outcome mapped to would then be reading the retry stage's answer instead of the lane's.
    ///     </para>
    /// </summary>
    private static async Task<GraphWorkflowNodeRunSnapshot> SettleToolAsync(GraphWorkflowHarness harness, Guid runId)
    {
        await harness.AdvanceUntilAsync(runId,
                async () => GraphWorkflowStateMachine.IsTerminal((await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false)).Status),
                $"Run {runId} left its tool node unsettled.")
            .ConfigureAwait(false);
        return await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false);
    }

    /// <summary>The <c>output.result</c> of a settled tool node.</summary>
    private static JsonElement Result(GraphWorkflowNodeRunSnapshot nodeRun)
    {
        using var document = JsonDocument.Parse(AssertEx.NotNull(nodeRun.OutputJson, "a settled tool node always carries its output document."));
        return document.RootElement.GetProperty("output").GetProperty("result").Clone();
    }
}
