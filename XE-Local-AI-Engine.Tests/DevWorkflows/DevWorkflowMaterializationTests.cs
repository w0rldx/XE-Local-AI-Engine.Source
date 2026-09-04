namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Reflection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Dynamic materialization (§5.9): a decomposition reads its own task package and grows the run into it, cloning the
///     template subtree once per task in the same transaction that rewrites the run's pinned graph.
///     <para>
///         Every test takes a host of its own. The graph cache's parse count, the scripted sandbox and the scripted
///         agent are all container singletons, and these fixtures share node keys — a shared host would let one test's
///         script answer another's node run, and one test's parse another's assertion.
///     </para>
/// </summary>
public sealed class DevWorkflowMaterializationTests
{
    /// <summary>A project id on the work item, because a graph with tool nodes in it is only startable with one.</summary>
    private static readonly Guid DevelopmentProjectId = Guid.NewGuid();

    /// <summary>Two tasks, the second depending on the first — the smallest package that exercises every wiring rule.</summary>
    private const string TwoTasks = """
                                    [
                                      { "id": "alpha", "title": "Add the parser", "goal": "Parse the manifest.", "acceptanceCriteria": ["the manifest parses"] },
                                      { "id": "beta", "title": "Add the writer", "goal": "Write the manifest.", "dependsOn": ["alpha"] }
                                    ]
                                    """;

    /// <summary>
    ///     The same package for a template that roots in a <c>DevTask</c>: there a slice becomes a coder attempt, so it
    ///     has to name the files it changes or the materializer hands the whole package back before cloning anything.
    /// </summary>
    private const string TwoDevTasks = """
                                       [
                                         { "id": "alpha", "title": "Add the parser", "goal": "Parse the manifest.", "changes": ["src/Manifest/Parser.cs"], "acceptanceCriteria": ["the manifest parses"] },
                                         { "id": "beta", "title": "Add the writer", "goal": "Write the manifest.", "changes": ["src/Manifest/Writer.cs"], "dependsOn": ["alpha"] }
                                       ]
                                       """;

    /// <summary>Two slices neither of which waits for the other — the shape a fan-out actually has.</summary>
    private const string TwoIndependentTasks = """
                                               [
                                                 { "id": "alpha", "title": "Add the parser", "goal": "Parse the manifest." },
                                                 { "id": "beta", "title": "Add the writer", "goal": "Write the manifest." }
                                               ]
                                               """;

    /// <summary>
    ///     The happy path, whole: the template subtree is cloned per task, the clones are wired to each other, to the
    ///     decomposition and into the join, the join stops waiting on the node that decided what the work is, and the run
    ///     drives all of it to completion.
    ///     <para>
    ///         The parity assertion is the one that matters most and is the least obvious: a rewritten graph carrying a
    ///         node with no row HANGS — admission only ever iterates rows, so a node nothing has a row for is simply
    ///         never considered, and the run waits on it for ever without a single failure to read.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ADecompositionClonesItsTemplateSubtreeOncePerTaskAndWiresTheClonesIntoItsJoin()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness, TwoTasks).ConfigureAwait(false);

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, run.GraphRevision, "the run's pinned graph moved on exactly once, which is what invalidates the parsed copy.");
        AssertEx.Equal(expected: 1,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "graph.changed"),
            "one expansion, one commit marker — the catalog gained no token for it.");

        var nodeRuns = await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal("decompose, implement#alpha, implement#beta, join, validate#alpha, validate#beta",
            string.Join(", ", nodeRuns.Select(static nodeRun => nodeRun.NodeKey).OrderBy(static key => key, StringComparer.Ordinal)),
            "the template is a SUBTREE: both of its nodes are cloned, per task.");

        var decompose = await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false);
        foreach (var child in nodeRuns.Where(static nodeRun => nodeRun.NodeKey.Contains('#', StringComparison.Ordinal)))
        {
            AssertEx.Equal(decompose.Id, child.MaterializedFromNodeRunId, $"'{child.NodeKey}' names the decomposition it came from, which is how a reader groups it.");
            AssertEx.Equal(DevelopmentProjectId, child.DevelopmentProjectId, "a child implements a slice of the SAME project, which is what carries the trust decision.");
        }

        AssertEx.Equal(expected: 1, (await harness.ReadNodeRunAsync(runId, "implement#alpha").ConfigureAwait(false)).MaterializationIndex);
        AssertEx.Equal(expected: 2, (await harness.ReadNodeRunAsync(runId, "validate#beta").ConfigureAwait(false)).MaterializationIndex);

        var graph = DevWorkflowGraph.Parse(run.GraphJson);
        AssertEx.Equal("implement#alpha", AssertEx.NotNull(graph.Nodes["validate#alpha"].RetryTarget), "each clone's fix loop points at its OWN implementation.");
        AssertEx.True(graph.Edges.Any(static edge => edge is { From: "decompose", To: "join" }),
            "the decomposition KEEPS its own edge into the join: the clones' fresh edges are what hold the join, and this one is the "
            + "only path back from it that reaches the task package the children were cut from.");
        AssertEx.Equal("decompose→implement#alpha, decompose→join, implement#alpha→validate#alpha, implement#beta→validate#beta, implement→validate, "
                       + "validate#alpha→implement#beta, validate#alpha→join, validate#beta→join, validate→join",
            string.Join(", ", graph.Edges.Select(static edge => $"{edge.From}→{edge.To}").OrderBy(static edge => edge, StringComparer.Ordinal)),
            "roots hang off the decomposition, dependsOn chains one task behind another, and every leaf reaches the join — while the "
            + "template keeps the edges it was authored with, which admission ignores because nothing will ever have a row for them.");

        AssertEx.Equal(string.Join(", ", graph.Nodes.Keys.Where(key => !graph.TemplateKeys.Contains(key)).OrderBy(static key => key, StringComparer.Ordinal)),
            string.Join(", ", nodeRuns.Select(static nodeRun => nodeRun.NodeKey).OrderBy(static key => key, StringComparer.Ordinal)),
            "row/graph parity: a rewritten node with no row is never admitted and never fails — the run simply waits for ever.");

        await DriveToCompletionAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "join").ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     What a materialized child is TOLD, which is the seam the implementation lane reads: a non-blank
    ///     <c>requirements</c> — mandatory there, so writing it here is what keeps a child from standing itself down —
    ///     plus the title and acceptance criteria its task named.
    /// </summary>
    [Test]
    public async Task EachChildCarriesTheBriefItsTaskNamed()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness, TwoTasks).ConfigureAwait(false);

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var alpha = AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "implement#alpha").ConfigureAwait(false)).InputJson);
        AssertEx.Contains(alpha, "\"title\":\"Add the parser\"");
        AssertEx.Contains(alpha, "\"requirements\":\"Parse the manifest.\"");
        AssertEx.Contains(alpha, "\"acceptanceCriteriaJson\":\"[\\u0022the manifest parses\\u0022]\"");

        var beta = AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "implement#beta").ConfigureAwait(false)).InputJson);
        AssertEx.Contains(beta, "\"requirements\":\"Write the manifest.\"", message: "each child implements its OWN slice, not the whole feature N times.");
        AssertEx.Contains(beta, "\"acceptanceCriteriaJson\":null", message: "a task that named no criteria inherits the project's, which the implementation lane resolves.");
    }

    /// <summary>
    ///     A DevTask slice that names no file it will change is refused before it ever reaches a coder. That coder has
    ///     to export a NON-EMPTY patch to finish, so a "survey the code" or "capture the conventions" slice is refused,
    ///     re-attempted twice, refused twice more and then blocks the run in front of a human — three sessions and two
    ///     hours to learn what the package already said. Live, four runs went exactly that way.
    /// </summary>
    [Test]
    public async Task ADevTaskSliceThatNamesNoFileItWillChangeIsRefusedRatherThanSentToACoderThatCannotFinish()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness,
                              """[{"id":"survey","title":"Survey the style","goal":"Read the calculator and capture the style profile."}]""",
                              DevWorkflowGraphs.DecompositionIntoDevTasks)
                          .ConfigureAwait(false);

        // Two quiescent passes, as every other refusal here: the first hands the complaint back to the node, the second
        // — after the re-attempt saves nothing new — stands it down.
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var decompose = await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, decompose.Status, $"the row settled on {decompose.Status} rather than cloning a slice nobody can finish.");
        AssertEx.Equal(DevWorkflowFailureClasses.Configuration, decompose.FailureClass);
        var reason = AssertEx.NotNull(decompose.TerminalReason);
        AssertEx.Contains(reason, "'survey'", message: "the refusal names the task, or the re-attempt has nothing to correct.");
        AssertEx.Contains(reason, "names no file it will add or edit in 'changes'");
        AssertEx.Equal(expected: 2, (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count, "a refused package clones nothing.");
    }

    /// <summary>
    ///     A path a coder would be refused for touching is refused here instead. The workspace confinement is enforced
    ///     at the attempt either way; asking it of the package costs one call and saves the three attempts it would take
    ///     to discover that "/etc/passwd" is not a file in the workspace.
    /// </summary>
    [Test]
    public async Task ADevTaskSliceWhoseChangesLeaveTheWorkspaceIsRefused()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness,
                              """[{"id":"alpha","goal":"Add the method.","changes":["/etc/passwd"]}]""",
                              DevWorkflowGraphs.DecompositionIntoDevTasks)
                          .ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var decompose = await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, decompose.Status);
        AssertEx.Contains(AssertEx.NotNull(decompose.TerminalReason), "/etc/passwd", message: "and it names the entry, not merely the task.");
    }

    /// <summary>
    ///     The accepted shape, whole: a DevTask package that names its files clones per task and the files reach the
    ///     coder. They ride in <c>requirements</c> because that is the entire brief the implementation lane renders —
    ///     a field of its own would say the same thing through three more seams.
    /// </summary>
    [Test]
    public async Task ADevTaskSliceCarriesTheFilesItNamedIntoTheCodersBrief()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness,
                              """
                              [
                                { "id": "alpha", "title": "Add Square", "goal": "Add a Square method.", "changes": ["src/Calc/Calculator.cs", "tests/CalculatorSquareTests.cs"] },
                                { "id": "beta", "title": "Add Cube", "goal": "Add a Cube method.", "changes": ["src/Calc/Calculator.cs"] }
                              ]
                              """,
                              DevWorkflowGraphs.DecompositionIntoDevTasks)
                          .ConfigureAwait(false);

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var alpha = AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "implement#alpha").ConfigureAwait(false)).InputJson);
        AssertEx.Contains(alpha, "Add a Square method.", message: "the goal is still the whole of what the task asks for.");
        AssertEx.Contains(alpha, "Files this task will add or edit: src/Calc/Calculator.cs, tests/CalculatorSquareTests.cs");

        var beta = AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "implement#beta").ConfigureAwait(false)).InputJson);
        AssertEx.Contains(beta, "Files this task will add or edit: src/Calc/Calculator.cs", message: "each child is told its OWN files.");
        AssertEx.False(beta.Contains("CalculatorSquareTests", StringComparison.Ordinal), $"and only its own: {beta}");
    }

    /// <summary>
    ///     The rule follows the DevTask, not the template's root. A custom template is free to root itself in an Agent
    ///     that writes the brief and put the coder one node below it — and there the attempt that cannot finish on an
    ///     empty patch is exactly as real, so reading only the root would wave the package straight past it.
    /// </summary>
    [Test]
    public async Task ADevTaskBelowAnAgentRootStillRequiresChanges()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness,
                              """[{"id":"survey","title":"Survey the style","goal":"Read the calculator and capture the style profile."}]""",
                              DevWorkflowGraphs.DecompositionIntoAnAgentOverADevTask)
                          .ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var decompose = await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, decompose.Status, $"the row settled on {decompose.Status} rather than cloning a coder that cannot finish.");
        AssertEx.Contains(AssertEx.NotNull(decompose.TerminalReason), "names no file it will add or edit in 'changes'");
        AssertEx.Equal(expected: 2, (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count, "a refused package clones nothing.");
    }

    /// <summary>The same template with the files named: the rule is a gate on the package, not a refusal of the shape.</summary>
    [Test]
    public async Task ADevTaskBelowAnAgentRootMaterializesOnceItNamesItsChanges()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness,
                              """[{"id":"alpha","title":"Add Square","goal":"Add a Square method.","changes":["src/Calc/Calculator.cs"]}]""",
                              DevWorkflowGraphs.DecompositionIntoAnAgentOverADevTask)
                          .ConfigureAwait(false);

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false)).Status);
        AssertEx.Contains(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "implement#alpha").ConfigureAwait(false)).InputJson),
            "Files this task will add or edit: src/Calc/Calculator.cs",
            message: "and the coder below the Agent root is told the files, exactly as a DevTask-rooted clone is.");
    }

    /// <summary>
    ///     The rule is the DevTask lane's, so a template with no DevTask in it at all is untouched by it. An Agent clone
    ///     is an ordinary work session with no patch to export and nothing "changes" would be describing, and a package
    ///     written for one has never had to name a file — refusing it would break every template that is not the seeded
    ///     one.
    /// </summary>
    [Test]
    public async Task AnAgentRootedDecompositionStillMaterializesWithoutChanges()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness, TwoTasks).ConfigureAwait(false);

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false)).Status);
        AssertEx.Contains(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "implement#alpha").ConfigureAwait(false)).InputJson),
            "\"requirements\":\"Parse the manifest.\"",
            message: "and the brief is exactly what it was, with no file line invented for a package that named none.");
    }

    /// <summary>
    ///     R6, the staleness rule: the tick that materializes STOPS there. Everything below it in a tick judges node runs
    ///     against a parsed graph, and this one has just been replaced — so the assertion is that the next tick re-parses
    ///     and that nothing was admitted against the graph that no longer describes the run.
    /// </summary>
    [Test]
    public async Task TheTickThatMaterializesEndsThereAndTheNextOneReParses()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness, TwoTasks).ConfigureAwait(false);

        var parsesBefore = harness.Graphs.ParseCount;
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(parsesBefore, harness.Graphs.ParseCount, "the materializing tick used the graph it had already parsed.");
        AssertEx.True((await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false))
                      .Where(static nodeRun => nodeRun.NodeKey.Contains('#', StringComparison.Ordinal))
                      .All(static child => child.Status == DevWorkflowNodeRunStatus.Pending),
            "nothing was admitted in the tick that created it: admission would have judged it against the pre-rewrite graph.");

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(parsesBefore + 1, harness.Graphs.ParseCount, "the next tick re-parses on the bumped revision, which is what makes the rewrite take effect.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "implement#alpha").ConfigureAwait(false)).Status,
            "and then the first task's implementation starts.");
    }

    /// <summary>
    ///     Walkthrough #9: the expansion is idempotent. A tick replayed after a crash — or simply another tick over a
    ///     decomposition that has already grown — finds the commit marker and does not expand again.
    ///     <para>
    ///         The assertion that carries this is the DECOMPOSITION's own row, not the row count. Without the marker
    ///         the second pass re-reads the same package, re-derives the same clone keys, and is refused for taking
    ///         node keys the run already carries — which stands the decomposition down and re-attempts it. The counts
    ///         all still match at that point, so a test asserting only those passes while the run quietly re-runs the
    ///         node whose answer it had already used.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AReplayedMaterializationWritesNothingASecondTime()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness, TwoTasks).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        var rowsAfterFirst = (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count;

        await harness.RestartAsync().ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        var decompose = await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            decompose.Status,
            $"the replay left the decomposition alone; it is {decompose.Status}: {decompose.TerminalReason}");
        AssertEx.Equal(expected: 1, decompose.Attempt, "and did not spend an attempt re-running the node whose answer the run already used.");
        AssertEx.Equal(rowsAfterFirst, (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count, "a replay clones nothing again.");
        AssertEx.Equal(expected: 1, run.GraphRevision, "and bumps no second revision, so the graph a reader pinned is still the graph.");
        AssertEx.Equal(expected: 1, (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == "graph.changed"));
    }

    /// <summary>
    ///     Every capability invariant holds on the graph AS MATERIALIZED, not only on the definition. The rewrite is
    ///     re-parsed by the graph cache on the tick after expansion, so a rule that held at save and broke after the
    ///     clones were wired would fail a run mid-flight rather than an author at their keyboard.
    ///     <para>
    ///         The proof the rewrite preserves them is the virtual edge: what the invariants read from a materializing
    ///         node to its template root before expansion is the definition-time image of the real edge the
    ///         materializer wires afterwards, so the fixpoint gives the same answer on both graphs.
    ///     </para>
    /// </summary>
    [Test]
    public async Task MaterializedGraphStillValidates()
    {
        await using var harness = new DevWorkflowHarness();
        // TwoDevTasks rather than TwoTasks: this template roots in a DevTask, and a package whose tasks name no
        // 'changes' is refused there before anything is cloned — which would leave this asserting the definition.
        var runId = await DecomposeAsync(harness, TwoDevTasks, DevWorkflowGraphs.DecompositionIntoDevTasksAndIntegration).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, run.GraphRevision, "the graph really was rewritten, or this asserts the definition again.");

        var materialized = DevWorkflowGraph.Parse(run.GraphJson);

        AssertEx.Contains(materialized.Nodes.Keys, key => key.Contains('#', StringComparison.Ordinal), "the clones are in the graph being judged.");
        AssertEx.NotEmpty(materialized.Nodes.Values.Where(static node => node.ToolMode == DevWorkflowToolMode.Apply),
            "and so is the apply node whose validation ancestry GRAPH-C4-3 asks about.");
    }

    /// <summary>
    ///     "There is no follow-up work" is a legitimate answer, not malformed output (review F4). The join keeps the edge
    ///     it already had, fires on the decomposition itself, and the run completes — where the alternative reading
    ///     leaves it Pending for ever.
    /// </summary>
    [Test]
    public async Task ADecompositionThatFoundNoWorkCompletesTheRunThroughItsJoin()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness, "[]").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(expected: 3,
            (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count,
            "no children, and one row — and only one — standing for the validation that did not apply.");
        AssertEx.Equal(expected: 0, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).GraphRevision, "there was no rewrite to make: the graph already said this.");
        var events = await harness.ReadEventsAsync(runId).ConfigureAwait(false);
        AssertEx.Empty(events.Where(static entry => entry.EventType == "graph.changed"), "no graph changed, so nothing says one did.");
        var marker = events.Last(static entry => entry.EventType == "node.materialized");
        AssertEx.Contains(AssertEx.NotNull(marker.DetailJson),
            "\"graphRevision\":0",
            message: "the commit marker says no graph moved, or a consumer refetches and gets the same revision back.");
    }

    /// <summary>
    ///     Ruling D12: the zero-task path writes one already-succeeded row per validation node in the template, rather
    ///     than exempting the apply downstream from having to find one.
    ///     <para>
    ///         Without the row an apply node's runtime pre-check asks about a template key that has no row at all,
    ///         reads it as "nothing validated this", and blocks a run that did exactly what it was asked. The row is
    ///         seeded terminal rather than seeded and then transitioned: a <c>Pending</c> row at a template key is
    ///         admissible — its only inbound edge is dropped as template-sourced — so the tool lane would really run
    ///         the template's validation commands, and a crash between the two writes would do that.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ADecompositionThatFoundNoWorkWritesOneNotApplicableValidateRow()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness, "[]").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var validate = await harness.ReadNodeRunAsync(runId, "validate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, validate.Status, "the row stands for a check that did not need to run, so it is not pending work.");
        AssertEx.Contains(AssertEx.NotNull(validate.OutputJson),
            "\"verdict\":\"validation-not-applicable\"",
            message: "and it says WHY it succeeded, or a reader takes it for a check that passed.");
        AssertEx.Contains(validate.OutputJson!,
            "\"status\":\"succeeded\"",
            message: "the routing vocabulary's own value, so a conditional out-edge on this node fires as a real pass would.");
        AssertEx.Null(validate.MaterializationIndex, "it stands for ZERO clones, which is a different fact from being clone n.");
    }

    /// <summary>
    ///     And the reason the row exists: the apply behind the join is reached UNBLOCKED. The apply's runtime check asks
    ///     whether a validation succeeded on the path this run took, and "there was nothing to decompose" has to answer
    ///     that as a pass rather than as an absence.
    /// </summary>
    [Test]
    public async Task AZeroTaskDecompositionReachesItsApplyUnblocked()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        var runId = await IntegrationRunWithNoWorkAsync(harness).ConfigureAwait(false);

        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var integrate = await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false);
        AssertEx.NotEqual(DevWorkflowNodeRunStatus.Pending, integrate.Status, "the apply was dispatched at all, or this asserts nothing about the pre-check.");
        AssertEx.False((integrate.TerminalReason ?? string.Empty).Contains("GRAPH-C4-3", StringComparison.Ordinal),
            $"the apply must not be blocked for want of a validation that was written for it: {integrate.FailureClass} — {integrate.TerminalReason}");
    }

    /// <summary>
    ///     The counter-proof, and the rule the pre-check exists for: when the validation the apply's proof rested on is
    ///     no longer a success, the apply is blocked for a human rather than applying patches nothing has judged.
    ///     <para>
    ///         The row is moved directly, which is what the harness's own escape hatch is for: at this baseline every
    ///         reachable path to an apply keeps its validation succeeded, and the shape this models is the one the
    ///         follow-up round introduces — an operator's skip that an <c>All</c> join now carries on past.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AnApplyWhoseValidationNoLongerSucceededIsBlockedWithPolicy()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        var runId = await IntegrationRunWithNoWorkAsync(harness).ConfigureAwait(false);

        await harness.TransitionNodeRunAsync(runId, "validate", DevWorkflowNodeRunStatus.Failed).ConfigureAwait(false);
        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var integrate = await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, integrate.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.Policy,
            integrate.FailureClass,
            "a policy refusal needs a person; calling it a configuration fault sends them to fix a definition that is fine.");
        AssertEx.Contains(AssertEx.NotNull(integrate.TerminalReason), "GRAPH-C4-3", StringComparison.Ordinal);
    }

    /// <summary>
    ///     The pre-check's walk crosses an edge in every state EXCEPT <c>Dead</c> and <c>Pending</c> — the two that mean
    ///     the run did not come this way, or has not yet.
    ///     <para>
    ///         Asserted over the enum rather than over today's one-name list, because the failure this guards against
    ///         cannot be written as a run yet: a branch in flight adds a state for an operator's skip whose own
    ///         dependencies all succeeded, and the rows BEHIND such an edge are <c>Succeeded</c>. A walk that refused to
    ///         cross it would block an apply whose validation really did pass. This test goes red the moment that state
    ///         exists and the walk has not been told about it, which is the one line the merge has to add.
    ///     </para>
    /// </summary>
    [Test]
    public void TheProvenanceWalkCrossesEveryEdgeStateThatIsNotDeadOrPending()
    {
        var field = AssertEx.NotNull(typeof(DevWorkflowDispatcher).GetField("ProvenanceEdgeStates", BindingFlags.NonPublic | BindingFlags.Static),
            "the walk's edge-state set is gone or renamed.");
        var crossed = (DevWorkflowEdgeState[])AssertEx.NotNull(field.GetValue(null));
        var expected = Enum.GetValues<DevWorkflowEdgeState>()
                           .Where(static state => state is not (DevWorkflowEdgeState.Dead or DevWorkflowEdgeState.Pending))
                           .Order()
                           .ToArray();

        AssertEx.True(expected.SequenceEqual(crossed.Order()),
            $"a state that says the run took this edge has to be walked through, and Dead and Pending never may be. "
            + $"Expected [{string.Join(", ", expected)}], walked [{string.Join(", ", crossed)}].");
    }

    /// <summary>The shipped integration shape, decomposed into no work at all and standing at its integration gate.</summary>
    private static async Task<Guid> IntegrationRunWithNoWorkAsync(DevWorkflowHarness harness)
    {
        var (projectId, _) = await harness.SeedDevelopmentProjectAsync().ConfigureAwait(false);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.DecompositionIntoDevTasksAndIntegration, "Add the feature.", projectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", "[]").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        return runId;
    }

    /// <summary>
    ///     A package the runtime cannot use is handed back to the node that wrote it, with the complaint in the next
    ///     attempt's objective — §7.1's named exception to <c>Configuration</c> being non-retryable, and the cheapest
    ///     correction loop available, since the thing that produced the document is the thing that can fix it.
    /// </summary>
    [Test]
    public async Task AMalformedTaskPackageIsHandedBackToTheNodeThatWroteIt()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness, "not a task package at all").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var decompose = await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, decompose.Attempt, "the decomposition re-runs rather than the run stalling on output nothing can read.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, decompose.Status);
        AssertEx.Contains(harness.Agent.Objectives[^1], "not valid JSON", message: "and it is TOLD what was wrong, or it composes the same answer again.");

        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", TwoTasks).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 6, (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count, "the corrected package expands like any other.");
    }

    /// <summary>
    ///     And when the correction does not come, the node stands down for a human rather than looping: a decomposition
    ///     left Succeeded over output nothing can use would let the run complete having decomposed nothing at all.
    /// </summary>
    [Test]
    public async Task ADecompositionWhoseOutputStaysUnusableStandsDownForAHuman()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness, "not a task package at all").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var decompose = await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, decompose.Status, $"the row settled on {decompose.Status} rather than re-attempting for ever.");
        AssertEx.Equal(DevWorkflowFailureClasses.Configuration, decompose.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(decompose.TerminalReason), "not valid JSON");
        AssertEx.Equal(DevWorkflowDecisionKind.Abandon, decompose.PendingDecisionKind, "a human is offered the intervention answers, which is what unwedges it.");
        AssertEx.Equal(DevWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The rest of §5.9's rejections, each with the sentence a model and a human are both given. They are refusals of
    ///     a WELL-FORMED package, so the reason has to say what about it cannot be used — "invalid" would send the next
    ///     attempt back with nothing to change.
    /// </summary>
    [Test]
    public async Task EveryWayAWellFormedPackageIsStillRefused_NamesWhatIsWrongWithIt()
    {
        // MaxNodeRunsPerRun is the run-wide bound and the graph's own maxChildren is 4, so this host can stand both up:
        // five tasks trip the template's cap, and three tasks (six clones over the run's two rows) trip the run's.
        await using var harness = new DevWorkflowHarness(("DevWorkflows:MaxNodeRunsPerRun", "6"));
        var refusals = new (string Package, string Expected)[]
        {
            ("""[{"id":"a","goal":"one"},{"id":"b","goal":"two"},{"id":"c","goal":"three"},{"id":"d","goal":"four"},{"id":"e","goal":"five"}]""",
                "more than the 4 this decomposition allows"),
            ("""[{"id":"a","goal":"one"},{"id":"b","goal":"two"},{"id":"c","goal":"three"}]""", "past the 6 node runs it may carry"),
            ("""[{"id":"a","goal":"one"},{"id":"a","goal":"again"}]""", "names 'a' twice"),
            ("""[{"id":"a","title":"no goal"}]""", "names no 'goal'"),
            ("""[{"id":"a","goal":"one","dependsOn":["ghost"]}]""", "depends on 'ghost', which the package does not declare"),
            ("""[{"id":"a","goal":"one","dependsOn":["b"]},{"id":"b","goal":"two","dependsOn":["a"]}]""", "in a cycle"),
            ("""{"tasks":"not an array"}""", "must be an array of tasks"),

            // Not a schema error: the parser reads `allowedPaths` and the artifact keeps it. Refused because NOTHING
            // enforces it — a decomposition relying on it for parallel-child isolation would get no restriction at all,
            // and a silent nothing is the one answer this module will not give.
            ("""[{"id":"a","goal":"one","allowedPaths":["src/parser/**"]}]""", "does not enforce")
        };

        foreach (var (package, expected) in refusals)
        {
            var runId = await DecomposeAsync(harness, package).ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
            await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

            var decompose = await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false);
            AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, decompose.Status, $"'{expected}' left the run on {decompose.Status}.");
            AssertEx.Contains(AssertEx.NotNull(decompose.TerminalReason), expected);
            AssertEx.Equal(expected: 2, (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count, "a refused package clones nothing.");
        }
    }

    /// <summary>
    ///     A package with a HOLE in it — a JSON <c>null</c> where a task should be — is refused, not thrown over.
    ///     <para>
    ///         `[null]` is valid JSON and deserializes to a list holding a null element, which the non-nullable
    ///         annotation says cannot exist. Every reader past the parse dereferences the entry, so the hole reaches
    ///         one of them as a <c>NullReferenceException</c> out of the tick — and the decomposition it happens over
    ///         has already SUCCEEDED, so the next tick reads the same artifact and throws again. That is the wedge
    ///         class this module refuses: the run has nothing moving, nothing failing, and nothing to read.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ATaskPackageWithAHoleWhereATaskShouldBeIsRefusedRatherThanWedgingTheRun()
    {
        await using var harness = new DevWorkflowHarness();
        string[] packages =
        [
            "[null]",
            """[{"id":"alpha","goal":"one"},null]""",
            """{"tasks":[null]}"""
        ];

        foreach (var package in packages)
        {
            var runId = await DecomposeAsync(harness, package).ConfigureAwait(false);

            // Two quiescent passes: the first hands the complaint back to the node, the second — after the re-attempt
            // saves nothing new — stands it down. A throw out of the tick fails this line rather than the assertions.
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
            await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

            var decompose = await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false);
            AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, decompose.Status, $"'{package}' left the run on {decompose.Status} instead of standing the decomposition down.");
            AssertEx.Equal(DevWorkflowFailureClasses.Configuration, decompose.FailureClass);
            AssertEx.Contains(AssertEx.NotNull(decompose.TerminalReason), "where a task should be", message: "and the node is told WHICH entry is the hole, or the re-attempt has nothing to correct.");
            AssertEx.Equal(expected: 2, (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count, "a refused package clones nothing.");
        }
    }

    /// <summary>
    ///     A collision on a NON-root clone is refused as cleanly as one on the root. The template's descendants are
    ///     cloned under the same "{nodeKey}#{taskId}" layout, so a graph that happens to declare a node by one of those
    ///     names collides just as hard — and reaching the store with it is not a refusal, it is a throw out of the tick
    ///     that leaves the run with nothing moving and nothing to read.
    /// </summary>
    [Test]
    public async Task ATaskWhoseCloneWouldTakeANonRootNodeKeyIsRefusedRatherThanWedging()
    {
        const string CollidingGraph = """
                                      {
                                        "schemaVersion": 1,
                                        "nodes": [
                                          { "nodeKey": "decompose", "nodeType": "Agent", "label": "Decompose",
                                            "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b",
                                            "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                          { "nodeKey": "implement", "nodeType": "Agent", "label": "Implement",
                                            "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                          { "nodeKey": "validate", "nodeType": "Tool" },
                                          { "nodeKey": "join", "nodeType": "Join" },
                                          { "nodeKey": "validate#alpha", "nodeType": "Tool", "label": "A node an author happened to name this" }
                                        ],
                                        "edges": [
                                          { "from": "decompose", "to": "join" },
                                          { "from": "implement", "to": "validate" },
                                          { "from": "validate", "to": "join" },
                                          { "from": "join", "to": "validate#alpha" }
                                        ]
                                      }
                                      """;

        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness, """[{"id":"alpha","goal":"Do the half nobody named."}]""", CollidingGraph).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var decompose = await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, decompose.Status, $"the row settled on {decompose.Status} rather than the run wedging on a refused insert.");
        AssertEx.Contains(AssertEx.NotNull(decompose.TerminalReason), "validate#alpha");
        AssertEx.Equal(expected: 3,
            (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count,
            "the run still carries only its own rows: decompose, the join, and the node whose name was taken.");
    }

    /// <summary>
    ///     Two tasks of ONE package can collide with each other, and that is refused as cleanly as a collision with an
    ///     existing node.
    ///     <para>
    ///         <c>"{nodeKey}#{taskId}"</c> is not injective. A template carrying both <c>a</c> and <c>a#b</c> generates
    ///         <c>a#b#c</c> for task <c>b#c</c> and again for task <c>c</c> — neither of which any existing node holds,
    ///         so a check against the graph alone waves both through and the store answers the second with a refused
    ///         insert on its unique <c>(run_id, node_key)</c>. That throws out of the tick, which is the wedge this
    ///         whole rejection path exists to avoid.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TwoTasksOfOnePackageThatWouldGenerateTheSameCloneKeyAreRefusedRatherThanWedging()
    {
        const string AmbiguousTemplate = """
                                         {
                                           "schemaVersion": 1,
                                           "nodes": [
                                             { "nodeKey": "decompose", "nodeType": "Agent", "label": "Decompose",
                                               "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b",
                                               "materialization": { "templateNodeKey": "a", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                             { "nodeKey": "a", "nodeType": "Agent", "label": "First",
                                               "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                             { "nodeKey": "a#b", "nodeType": "Agent", "label": "Second",
                                               "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                             { "nodeKey": "join", "nodeType": "Join" }
                                           ],
                                           "edges": [
                                             { "from": "decompose", "to": "join" },
                                             { "from": "a", "to": "a#b" },
                                             { "from": "a#b", "to": "join" }
                                           ]
                                         }
                                         """;

        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness, """[{"id":"b#c","goal":"one"},{"id":"c","goal":"two"}]""", AmbiguousTemplate).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var decompose = await harness.ReadNodeRunAsync(runId, "decompose").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, decompose.Status, $"the row settled on {decompose.Status} rather than the run wedging on a refused insert.");
        var reason = AssertEx.NotNull(decompose.TerminalReason);
        AssertEx.Contains(reason, "a#b#c", message: "the refusal names the key both tasks want.");
        AssertEx.Contains(reason, "'b#c' and 'c'", message: "and both tasks that want it, because either one is the one to rename.");
        AssertEx.Equal(expected: 2, (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count, "a refused package clones nothing.");
    }

    /// <summary>
    ///     The seam that makes any of this reachable: a work session has no word for a task package, so the NODE declares
    ///     the kind it produces and the promotion writes it. Without it every decomposition's own output lands as an
    ///     ordinary report and §5.9 step 1 finds nothing to read.
    /// </summary>
    [Test]
    public async Task ADecomposingNodesOwnOutputIsPromotedAsTheKindItDeclares()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await DecomposeAsync(harness, TwoTasks).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var artifact = (await harness.ReadArtifactsAsync(runId).ConfigureAwait(false)).Single(static entry => entry.ProducingNodeKey == "decompose");

        AssertEx.Equal(DevWorkflowArtifactKind.TaskPackage, artifact.Kind, "the node said what it produces; nothing else could have known.");
    }

    /// <summary>
    ///     N1: what the node behind the join gets to READ. The materializer used to delete the decomposition's own edge
    ///     into the join, which took the decomposition off every path back from it — so the verification agent inherited
    ///     the clones' validation reports and nothing else, and said so itself in the live run before returning "not
    ///     yet". Kept, that edge carries the task package; the seed's <c>planapproval → verify</c> edge carries the plan
    ///     the walk cannot reach past the decomposition that consumed it.
    /// </summary>
    [Test]
    public async Task TheNodeBehindAMaterializedJoinInheritsThePlanTheTaskPackageAndEveryChildsReport()
    {
        await using var harness = new DevWorkflowHarness();
        var runId = await VerifiableDecompositionAsync(harness, TwoIndependentTasks).ConfigureAwait(false);

        await DriveToCompletionAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "verify").ConfigureAwait(false)).Status,
            "the verification ran at all: the join waited for both slices and then let it through.");

        var artifacts = await harness.ReadArtifactsAsync(runId).ConfigureAwait(false);
        var consumed = await harness.ReadConsumedArtifactIdsAsync(runId, "verify").ConfigureAwait(false);
        var read = artifacts.Where(artifact => consumed.Contains(artifact.Id))
                            .Select(static artifact => $"{artifact.ProducingNodeKey}/{artifact.Name}")
                            .OrderBy(static entry => entry, StringComparer.Ordinal);
        AssertEx.Equal("decompose/tasks.json, plan/plan.md, validate#alpha/validate#alpha-validation.json, validate#beta/validate#beta-validation.json",
            string.Join(", ", read),
            "the approved plan, the package it was cut into, and what each slice's validation actually judged — the three things a "
            + "verification is asked to weigh against each other.");
    }

    /// <summary>
    ///     N4: skipping one slice's validation must not settle the run's tail over a sibling that is still working.
    ///     <para>
    ///         Settling the join the moment one branch was skipped took the integration stage over a slice the run had
    ///         not finished. What the state machine rules is asserted in both halves here: the sibling is untouched and
    ///         the join WAITS for it, and once it lands the join SUCCEEDS — per C1 an operator's skip over healthy
    ///         ancestors is Waived rather than Dead, and an <c>All</c> join fires on the branch that did land.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SkippingOneSlicesValidationLeavesItsSiblingAloneAndTheJoinWaitsThenFiresOnIt()
    {
        await using var harness = new DevWorkflowHarness();
        harness.Tools.Answer("validate#alpha", FakeDevWorkflowToolCommands.Failing());
        var runId = await VerifiableDecompositionAsync(harness, TwoIndependentTasks).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "implement#alpha").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var failed = await harness.ReadNodeRunAsync(runId, "validate#alpha").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, failed.Status, $"the scripted failure left it on {failed.Status}, with no answer to give an operator.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "implement#beta").ConfigureAwait(false)).Status,
            "the second slice is still being implemented, which is the whole point of the moment being tested.");

        await harness.DecideAsync(runId, "validate#alpha", DevWorkflowDecisionKind.Skip).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(runId, "implement#beta").ConfigureAwait(false)).Status,
            "skipping one slice's validation reaches its own descendants and nothing else — the sibling was never downstream of it.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending,
            (await harness.ReadNodeRunAsync(runId, "join").ConfigureAwait(false)).Status,
            "and the join WAITS: its other branch is still live, and answering before that lands would settle the integration stage "
            + "over a slice the run has not finished.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, (await harness.ReadNodeRunAsync(runId, "verify").ConfigureAwait(false)).Status);

        await harness.SettleAgentAsync(runId, "implement#beta").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "validate#beta").ConfigureAwait(false)).Status,
            "the sibling ran to its own answer.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "join").ConfigureAwait(false)).Status,
            "and only THEN does the join answer, with the answer C1 ruled: an operator's skip over ancestors that all succeeded is "
            + "WAIVED rather than dead, so an All join fires on the branch that did land instead of throwing it away.");
        AssertEx.False((await harness.ReadNodeRunAsync(runId, "verify").ConfigureAwait(false)).Status is DevWorkflowNodeRunStatus.Pending or DevWorkflowNodeRunStatus.Skipped,
            "and the verification is asked to judge what the run DID produce, rather than being skipped with it.");
        AssertEx.Contains(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "validate#alpha").ConfigureAwait(false)).TerminalReason),
            "Skipped by an operator",
            message: "the excused slice says who excused it, which is what the verification's skipped-steps block reads.");
    }

    /// <summary>
    ///     Starts a run on the shape a verification sits behind and takes it to the point where an approved plan and a
    ///     read task package are both on the run.
    /// </summary>
    private static async Task<Guid> VerifiableDecompositionAsync(DevWorkflowHarness harness, string package)
    {
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.DecompositionWithVerification, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "plan", "plan.md", "1. Parse it. 2. Write it.").ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "plan").ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await harness.DecideAsync(runId, "planapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", package).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        return runId;
    }

    /// <summary>Starts a decomposing run and takes it to the point where its package is written and its session done.</summary>
    private static async Task<Guid> DecomposeAsync(DevWorkflowHarness harness, string package, string? graphJson = null)
    {
        var runId = await harness.StartRunAsync(graphJson ?? DevWorkflowGraphs.DecompositionSubtree, developmentProjectId: DevelopmentProjectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", package).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        return runId;
    }

    /// <summary>
    ///     Drives the expanded run the way its lanes would: the sandbox lane answers on its own, and every agent session
    ///     the graph starts is landed as its model finishing.
    /// </summary>
    private static async Task DriveToCompletionAsync(DevWorkflowHarness harness, Guid runId, int maxRounds = 8)
    {
        for (var round = 1; round <= maxRounds; round++)
        {
            await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
            if (DevWorkflowStateMachine.IsTerminal((await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status))
            {
                return;
            }

            foreach (var nodeRun in (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false))
                     .Where(static nodeRun => nodeRun is { Status: DevWorkflowNodeRunStatus.Running, NodeType: DevWorkflowNodeType.Agent, WorkSessionId: not null }))
            {
                _ = await harness.Agent.SettleAsync(nodeRun.WorkSessionId!.Value, AgentWorkSessionStatus.Completed).ConfigureAwait(false);
            }
        }

        throw new AssertionException($"Run {runId} had not finished after {maxRounds} rounds.");
    }
}
