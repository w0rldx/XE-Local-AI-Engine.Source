namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     C4: the integration stage. A fan-out's patches reach the repository through Dev Mode's own apply gate, one after
///     another, and ONLY after an operator answered the workflow's own human gate (Y3).
///     <para>
///         What is real here is everything the workflow owns: the parse rule that puts the gate in front, the decision
///         row, the enumeration of which tasks this run implemented, the sequencing, the failure classes and the report.
///         What is scripted is the host mutation — <see cref="FakeDevelopmentTaskChain.ApplyAsync" /> runs the store's
///         own apply ledger commands and skips the evidence verification and the git apply, both of which need a real
///         repository and two real model attempts. The evidence chain itself is asserted where it is real and was left
///         untouched by this phase: <c>DevelopmentValidationReviewAndApplyTests</c> and
///         <c>TrustedDevelopmentHostApplyPortHardeningTests</c> in the Development suite.
///     </para>
///     <para>
///         Every test takes a host of its OWN: the scripted chain is a container singleton whose history they read.
///     </para>
/// </summary>
public sealed class DevWorkflowIntegrationApplyTests
{
    /// <summary>Two slices that do not depend on each other, so both are implemented and both have a patch to apply.</summary>
    private const string TwoIndependentTasks = """
                                               [
                                                 { "id": "alpha", "title": "Add the parser", "goal": "Parse the manifest." },
                                                 { "id": "beta", "title": "Add the writer", "goal": "Write the manifest." }
                                               ]
                                               """;

    /// <summary>
    ///     The named C4 gate: two task patches apply sequentially, and only after the gate approves.
    ///     <para>
    ///         The "only after" half is asserted BEFORE the decision as well as after it, because it is the half that
    ///         cannot be inferred from the end state: a run that applied at the join and then approached the gate would
    ///         finish looking exactly like this one.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TwoTaskPatchesApplyInOrderAndOnlyAfterTheGateApproves()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        var (runId, projectId) = await ImplementTwoSlicesAsync(harness).ConfigureAwait(false);

        var gate = await harness.ReadNodeRunAsync(runId, "integrationapproval").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, gate.Status, $"the run stopped at {gate.Status} instead of asking: {gate.TerminalReason}");
        AssertEx.Empty(harness.Chain.Applied, "nothing may reach the repository before the operator has answered.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, (await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false)).Status);

        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var alpha = await TaskIdAsync(harness, runId, "implement#alpha").ConfigureAwait(false);
        var beta = await TaskIdAsync(harness, runId, "implement#beta").ConfigureAwait(false);
        AssertEx.Equal($"{alpha:N}, {beta:N}",
            string.Join(", ", harness.Chain.Applied.Select(static taskId => taskId.ToString("N"))),
            "both patches, one after the other, in the order the decomposition put the slices in.");

        var integrate = await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, integrate.Status, AssertEx.NotNull(integrate.TerminalReason ?? integrate.OutputJson));
        AssertEx.Equal(DevelopmentTaskStatus.Completed, (await harness.ReadDevelopmentTaskAsync(alpha).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.Completed, (await harness.ReadDevelopmentTaskAsync(beta).ConfigureAwait(false)).Status);
        AssertEx.Equal(expected: 3,
            (await harness.ListDevelopmentTasksAsync(projectId).ConfigureAwait(false)).Count,
            "and the operator's own task was not swept into the integration: this run implemented two.");

        // The report is the operator's answer to "what went in", so it names the tasks rather than counting them.
        var report = await ReadApplyReportAsync(harness, runId).ConfigureAwait(false);
        AssertEx.Contains(report, alpha.ToString("D"));
        AssertEx.Contains(report, beta.ToString("D"));
        AssertEx.Contains(report, "\"outcome\":\"applied\"");

        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "fullvalidate").ConfigureAwait(false)).Status,
            "and the integrated result was validated after the applies, not instead of them.");
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        // A replayed pass — what a crash between the apply and the row's own write leaves behind — applies NOTHING a
        // second time. Driven through the production path rather than the dispatcher, because a completed run is
        // terminal and no tick will ever look at it again, which is exactly why the guard cannot be observed from one.
        await using var scope = harness.Services.CreateAsyncScope();
        var replay = await scope.ServiceProvider.GetRequiredService<DevWorkflowApplyCommands>()
                                .RunAsync(await harness.ReadRunAsync(runId).ConfigureAwait(false), integrate, CancellationToken.None)
                                .ConfigureAwait(false);
        AssertEx.True(replay.Passed, "a run whose patches are already in is not a failure to put them in.");
        AssertEx.Contains(Encoding.UTF8.GetString(replay.Report.Span), "already-applied");
        AssertEx.Equal(expected: 2, harness.Chain.Applied.Count, "and the gate was not asked a second time about a task that is already applied.");
    }

    /// <summary>
    ///     The other half of Y3: a refused gate applies NOTHING. The run ends where it was refused rather than
    ///     completing through a skipped apply, and the implemented tasks are left exactly as they were — waiting to be
    ///     applied by somebody who decides to.
    /// </summary>
    [Test]
    public async Task ARefusedIntegrationGateAppliesNothingAndEndsTheRun()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        var (runId, _) = await ImplementTwoSlicesAsync(harness).ConfigureAwait(false);

        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Reject).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Empty(harness.Chain.Applied, "a refused approval is the whole point of the gate.");
        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Cancelled, run.Status, "a gate answer no branch accepts ends the run rather than completing it.");
        AssertEx.Equal(DevWorkflowFailureClasses.GateRejected, run.FailureClass);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Cancelled,
            (await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false)).Status,
            "the apply node was ended by the refusal's drain without ever being admitted.");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply,
            (await harness.ReadDevelopmentTaskAsync(await TaskIdAsync(harness, runId, "implement#alpha").ConfigureAwait(false)).ConfigureAwait(false)).Status,
            "and the work is still there, still waiting for somebody to decide.");
    }

    /// <summary>
    ///     The v2 boundary, pinned because it is the shape a real two-slice run meets today. The apply gate takes the
    ///     FIRST patch and refuses the second: an approved subject names the base commit it was reviewed against, and
    ///     the first apply is sitting in that tree. §5.6.3 names concurrent-patch merge as v2, and this is what that
    ///     costs at runtime — one patch in, the node standing down for a human with both facts on the record, rather
    ///     than a second patch applied onto a tree nobody judged.
    /// </summary>
    [Test]
    public async Task AnApplyTheGateRefusesStopsTheSequenceAndSaysWhatLanded()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        harness.Chain.AllowApplies(count: 1);
        var (runId, _) = await ImplementTwoSlicesAsync(harness).ConfigureAwait(false);

        await harness.DecideAsync(runId, "integrationapproval", DevWorkflowDecisionKind.Approve).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var alpha = await TaskIdAsync(harness, runId, "implement#alpha").ConfigureAwait(false);
        var beta = await TaskIdAsync(harness, runId, "implement#beta").ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.Completed, (await harness.ReadDevelopmentTaskAsync(alpha).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.Blocked,
            (await harness.ReadDevelopmentTaskAsync(beta).ConfigureAwait(false)).Status,
            "the refused patch is NOT applied — Dev Mode's own gate stands the task down and says why, and the sequence stops there.");

        var integrate = await harness.ReadNodeRunAsync(runId, "integrate").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, integrate.Status, "a refusal on evidence is a human's answer, not another attempt's.");
        AssertEx.Equal(DevWorkflowFailureClasses.Policy, integrate.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(integrate.TerminalReason), "not at the exact base");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending,
            (await harness.ReadNodeRunAsync(runId, "fullvalidate").ConfigureAwait(false)).Status,
            "and nothing validates a half-integrated repository as though it were finished.");

        var report = await ReadApplyReportAsync(harness, runId).ConfigureAwait(false);
        AssertEx.Contains(report, "\"outcome\":\"applied\"");
        AssertEx.Contains(report, "\"outcome\":\"blocked\"");
        AssertEx.Contains(report, "\"tasksApplied\":1");
    }

    /// <summary>
    ///     The seeded <c>feature-development-v1</c> parses under every rule this runtime has — one entry node, an
    ///     acyclic graph, a template subtree nothing points into, an ancestor retry target, an <c>All</c> join over the
    ///     fan-out, no duplicate edge, and an apply node behind a human gate. Seeding runs the same parse, so a template
    ///     that would fail at run start fails at startup; this asserts it does neither.
    /// </summary>
    [Test]
    public async Task TheSeededFeatureTemplateParsesAndIsSeededOnceOnly()
    {
        await using var harness = new DevWorkflowHarness();

        await SeedAsync(harness).ConfigureAwait(false);
        await SeedAsync(harness).ConfigureAwait(false);

        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
        var definitions = await store.ListDefinitionsAsync(includeArchived: true).ConfigureAwait(false);
        var seeded = definitions.Where(definition => string.Equals(definition.SeedSlug, DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug, StringComparison.Ordinal))
                                .ToList();
        AssertEx.Equal(expected: 1, seeded.Count, "seeding is idempotent on the slug, so a second startup adds nothing.");

        var definition = await store.GetDefinitionAsync(seeded[0].Id).ConfigureAwait(false);
        var graph = DevWorkflowGraph.Parse(definition.GraphJson);
        AssertEx.Equal(expected: 11, graph.Nodes.Count);
        AssertEx.Equal(expected: 11, seeded[0].NodeCount);
        AssertEx.Equal("research", string.Join(", ", graph.EntryNodeKeys.Where(key => !graph.TemplateKeys.Contains(key))));
        AssertEx.Equal("implement, validate", string.Join(", ", graph.TemplateKeys.OrderBy(static key => key, StringComparer.Ordinal)));
        AssertEx.Equal(DevWorkflowToolMode.Apply, graph.Nodes["integrate"].ToolMode);
        AssertEx.Equal(DevWorkflowToolMode.Validate, graph.Nodes["fullvalidate"].ToolMode);
        AssertEx.Equal(DevWorkflowJoinPolicy.All, graph.Nodes["join"].JoinPolicy, "an Any join over a materialized fan-out is refused at parse when it expands to one child.");
        AssertEx.True(graph.Nodes["implement"].NodeTimeoutSeconds > 0, "every DevTask node in a shipped template declares its own bound.");
        AssertEx.Equal("implement", graph.Nodes["validate"].RetryTarget);
    }

    private static Task SeedAsync(DevWorkflowHarness harness) =>
        new DevWorkflowDefinitionSeeder(harness.Services.GetRequiredService<IServiceScopeFactory>(),
                harness.Services.GetRequiredService<IOptions<DevWorkflowOptions>>(),
                harness.Services.GetRequiredService<ILogger<DevWorkflowDefinitionSeeder>>())
            .StartAsync(CancellationToken.None);

    /// <summary>
    ///     A run decomposed into two slices, both implemented and validated, waiting at the integration gate — the state
    ///     every test here starts from.
    /// </summary>
    private static async Task<(Guid RunId, Guid ProjectId)> ImplementTwoSlicesAsync(DevWorkflowHarness harness)
    {
        var (projectId, _) = await harness.SeedDevelopmentProjectAsync().ConfigureAwait(false);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.DecompositionIntoDevTasksAndIntegration, "Add the feature.", projectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", TwoIndependentTasks).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);
        return (runId, projectId);
    }

    private static async Task<Guid> TaskIdAsync(DevWorkflowHarness harness, Guid runId, string nodeKey)
    {
        var nodeRun = await harness.ReadNodeRunAsync(runId, nodeKey).ConfigureAwait(false);
        return nodeRun.DevelopmentTaskId ?? throw new AssertionException($"Node run '{nodeKey}' names no development task, so it implemented nothing to apply.");
    }

    /// <summary>The apply node's own report, which is a different document under a different kind than a validation one.</summary>
    private static async Task<string> ReadApplyReportAsync(DevWorkflowHarness harness, Guid runId)
    {
        var artifacts = await harness.ReadArtifactsAsync(runId).ConfigureAwait(false);
        var report = artifacts.SingleOrDefault(artifact => string.Equals(artifact.Name, "integrate-apply.json", StringComparison.Ordinal))
                     ?? throw new AssertionException($"Run {runId} has no apply report: {string.Join(", ", artifacts.Select(static artifact => artifact.Name))}");
        AssertEx.Equal(DevWorkflowArtifactKind.Report,
            report.Kind,
            "an apply report is not a validation report, and a reader that decoded it as one would call it unreadable evidence.");
        return await harness.ReadArtifactTextAsync(runId, report).ConfigureAwait(false);
    }
}
