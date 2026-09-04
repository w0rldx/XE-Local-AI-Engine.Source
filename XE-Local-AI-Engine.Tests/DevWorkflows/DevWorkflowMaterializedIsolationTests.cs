namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     C3: two materialized tasks get isolated workspaces, and nothing serializes them for being siblings.
///     <para>
///         The isolation itself is not this module's code. <c>DevelopmentWorkspaceProvider</c> partitions its worktree,
///         its runtime directory and its sandbox attach key by <c>(ProjectId, TaskId)</c>, so what has to be proven here
///         is that a fan-out really does hand it two DIFFERENT task ids for two children of one project — the half that
///         would silently collapse if a child fell back to the project's own task. The other end of that claim, that two
///         task ids produce two independent worktrees, is pinned against the real provider and a real repository in
///         <c>DevelopmentWorkspaceAndCoderTests.WorkspaceProvider_GivesTwoTasksInOneProjectSeparateWorkspaces</c>; the
///         two meet at <see cref="DevelopmentExecutionSnapshot.TaskId" />, which is the only key either side uses.
///     </para>
///     <para>
///         Every test takes a host of its OWN: the scripted development chain and the scripted agent are container
///         singletons whose history these fixtures read, and they share node keys with the other materialization suites.
///     </para>
/// </summary>
public sealed class DevWorkflowMaterializedIsolationTests
{
    /// <summary>
    ///     Two tasks that do NOT depend on each other, so both clones are admitted by the same tick and their work
    ///     genuinely overlaps — a <c>dependsOn</c> chain would serialize them by design and prove nothing about what
    ///     happens when two children run at once.
    /// </summary>
    private const string TwoIndependentTasks = """
                                               [
                                                 { "id": "alpha", "title": "Add the parser", "goal": "Parse the manifest.", "changes": ["src/Manifest/Parser.cs"] },
                                                 { "id": "beta", "title": "Add the writer", "goal": "Write the manifest.", "changes": ["src/Manifest/Writer.cs"] }
                                               ]
                                               """;

    /// <summary>
    ///     The named C3 gate. Two materialized tasks implement two Development tasks of their own in the one project,
    ///     and each task id is the key the workspace provider partitions on — so the two children cannot land in one
    ///     worktree, one runtime directory or one sandbox.
    ///     <para>
    ///         The validation clones carry the same claim on the other lane: a Tool node run's workspace identity is
    ///         derived from its node key AND its attempt, so two clones validating at once are as separate as two
    ///         attempts of one clone are.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TwoMaterializedTasksImplementTwoTasksOfTheirOwnAndSoTwoWorkspaces()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        var (runId, projectId, operatorTaskId) = await DecomposeAsync(harness, TwoIndependentTasks).ConfigureAwait(false);

        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var alpha = await harness.ReadNodeRunAsync(runId, "implement#alpha").ConfigureAwait(false);
        var beta = await harness.ReadNodeRunAsync(runId, "implement#beta").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, alpha.Status, AssertEx.NotNull(alpha.TerminalReason ?? alpha.OutputJson));
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, beta.Status, AssertEx.NotNull(beta.TerminalReason ?? beta.OutputJson));

        var alphaTaskId = TaskId(alpha);
        var betaTaskId = TaskId(beta);
        AssertEx.NotEqual(alphaTaskId, betaTaskId, "two children implementing two slices must not share one task: the second would overwrite the first's work.");
        AssertEx.NotEqual(operatorTaskId, alphaTaskId, "and neither of them drives the operator's own task, which is what the fan-out was decomposed FROM.");
        AssertEx.NotEqual(operatorTaskId, betaTaskId);
        AssertEx.Equal(expected: 3,
            (await harness.ListDevelopmentTasksAsync(projectId).ConfigureAwait(false)).Count,
            "one project, three tasks: the operator's and one per materialized child.");

        // The workspace key itself, composed by the production path rather than re-derived here: the provider builds
        // both its worktree and its runtime directory from (ProjectId, TaskId), and keys the sandbox attach on the same
        // pair, so two children sharing a project are isolated by exactly this value being different.
        var project = await ReadProjectAsync(harness, projectId).ConfigureAwait(false);
        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        var graph = DevWorkflowGraph.Parse(run.GraphJson);
        var alphaCheck = await harness.ReadNodeRunAsync(runId, "validate#alpha").ConfigureAwait(false);
        var betaCheck = await harness.ReadNodeRunAsync(runId, "validate#beta").ConfigureAwait(false);
        var alphaWorkspace = Workspace(project, graph, run, alphaCheck);
        var betaWorkspace = Workspace(project, graph, run, betaCheck);

        AssertEx.Equal(project.Id, alphaWorkspace.ProjectId, "the children share the project, which is what carries the trust decision and the command profile.");
        AssertEx.Equal(project.Id, betaWorkspace.ProjectId);
        AssertEx.NotEqual(alphaWorkspace.TaskId, betaWorkspace.TaskId, "and are separated by the task key, which is the only thing the provider partitions on.");
        AssertEx.NotEqual(alphaWorkspace.TaskId,
            Workspace(project, graph, run, alphaCheck with
            {
                Attempt = alphaCheck.Attempt + 1
            }).TaskId,
            "a re-attempt is a new workspace too: reusing the last attempt's tree would re-validate the commit it was built from.");

        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply,
            (await harness.ReadDevelopmentTaskAsync(alphaTaskId).ConfigureAwait(false)).Status,
            "each child drove its own task the whole way, which is what makes the two workspaces two pieces of real work.");
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, (await harness.ReadDevelopmentTaskAsync(betaTaskId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     Two children of one project implement at the SAME MOMENT. Dev Mode's one-active-attempt rule is per TASK, and
    ///     giving each child its own task is exactly what stops that rule serializing a fan-out — so both rows are
    ///     caught Running with an attempt of their own in flight.
    ///     <para>
    ///         This is the attempt-level half of the concurrency question; the create-level half (four children creating
    ///         their tasks in one tick) is <c>FourChildrenCreatingTheirTasksAtOnce_AllGetOne</c> in the persistence
    ///         suite. Nothing here bounds the pair: <c>MaxConcurrentRuns</c> counts RUNS and these are two node runs of
    ///         one, and the sandbox lane's slots are not reached while the implementations are still working.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TwoMaterializedChildrenImplementAtTheSameTimeInOneProject()
    {
        await using var harness = DevWorkflowHarness.WithAScriptedChain();
        harness.Chain.HoldNextAttempt(count: 2);
        var (runId, projectId, _) = await DecomposeAsync(harness, TwoIndependentTasks).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var alpha = await harness.ReadNodeRunAsync(runId, "implement#alpha").ConfigureAwait(false);
        var beta = await harness.ReadNodeRunAsync(runId, "implement#beta").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, alpha.Status, $"'implement#alpha' is {alpha.Status}: {alpha.TerminalReason}");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running,
            beta.Status,
            $"'implement#beta' is {beta.Status} while its sibling works: {beta.TerminalReason}");

        var alphaTaskId = TaskId(alpha);
        var betaTaskId = TaskId(beta);
        AssertEx.NotEqual(alphaTaskId, betaTaskId);
        foreach (var taskId in new[]
                 {
                     alphaTaskId,
                     betaTaskId
                 })
        {
            AssertEx.Contains(await ReadAttemptsAsync(harness, taskId).ConfigureAwait(false),
                attempt => attempt.Status == DevelopmentAttemptStatus.Running,
                $"task {taskId:N} has no attempt in flight, so the two children are not really working at once.");
        }

        AssertEx.Equal(expected: 3,
            (await harness.ListDevelopmentTasksAsync(projectId).ConfigureAwait(false)).Count,
            "and both children got their task while the other was mid-create: the per-project ledger sequence does not refuse the second.");
    }

    /// <summary>
    ///     Starts a decomposing run over a real Development project and takes it to the point where its package is
    ///     written and its session done — the same shape <c>DevWorkflowMaterializationTests</c> uses, with the DevTask
    ///     template this phase is about.
    /// </summary>
    private static async Task<(Guid RunId, Guid ProjectId, Guid OperatorTaskId)> DecomposeAsync(DevWorkflowHarness harness, string package)
    {
        var (projectId, operatorTaskId) = await harness.SeedDevelopmentProjectAsync().ConfigureAwait(false);
        var runId = await harness.StartRunAsync(DevWorkflowGraphs.DecompositionIntoDevTasks, "Add the feature.", projectId).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = await harness.SaveAgentArtifactAsync(runId, "decompose", "tasks.json", package).ConfigureAwait(false);
        await harness.SettleAgentAsync(runId, "decompose").ConfigureAwait(false);
        return (runId, projectId, operatorTaskId);
    }

    /// <summary>
    ///     The execution snapshot a Tool node run stands in for a Dev Mode attempt with, composed by the production
    ///     path. The repository binding is built here because the resolve needs a registered folder these tests never
    ///     open; the only field the composition reads from it is the selected folder id.
    /// </summary>
    private static DevelopmentExecutionSnapshot Workspace(DevelopmentProjectSnapshot project,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun) =>
        DevWorkflowToolCommands.Synthesize(project,
            graph.Nodes[nodeRun.NodeKey],
            run,
            nodeRun,
            new DevelopmentRepositoryBinding(project.Id,
                project.SelectedFolderId ?? Guid.Empty,
                "repository",
                Path.Combine(Path.GetTempPath(), $"xe-c3-{project.Id:N}"),
                project.RepositoryIdentityHash));

    /// <summary>The task a child bound itself to, refusing a row that never bound one — which is a different failure.</summary>
    private static Guid TaskId(DevWorkflowNodeRunSnapshot nodeRun) =>
        nodeRun.DevelopmentTaskId ?? throw new AssertionException($"Node run '{nodeRun.NodeKey}' names no development task, so it never created one.");

    private static async Task<DevelopmentProjectSnapshot> ReadProjectAsync(DevWorkflowHarness harness, Guid projectId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>().GetProjectAsync(projectId).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DevelopmentAttemptSnapshot>> ReadAttemptsAsync(DevWorkflowHarness harness, Guid taskId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ListAttemptsAsync(taskId).ConfigureAwait(false);
    }
}
