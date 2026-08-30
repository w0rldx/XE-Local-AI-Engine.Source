namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
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
        AssertEx.False(graph.Edges.Any(static edge => edge is { From: "decompose", To: "join" }),
            "the join now waits on the children; left as it was it would fire the moment the decomposition succeeded.");
        AssertEx.Equal("decompose→implement#alpha, implement#alpha→validate#alpha, implement#beta→validate#beta, implement→validate, "
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
        AssertEx.Equal(expected: 2, (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Count, "no children, and no rows invented for a template nothing cloned.");
        AssertEx.Equal(expected: 0, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).GraphRevision, "there was no rewrite to make: the graph already said this.");
        var marker = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Single(static entry => entry.EventType == "graph.changed");
        AssertEx.Contains(AssertEx.NotNull(marker.DetailJson),
            "\"revisionBumped\":false",
            message: "the one graph.changed that changes no graph says so, or a consumer refetches and gets the same revision back.");
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
            ("""{"tasks":"not an array"}""", "must be an array of tasks")
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
