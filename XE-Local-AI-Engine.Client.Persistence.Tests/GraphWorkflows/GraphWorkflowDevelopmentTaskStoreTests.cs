namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The <c>development_task_id</c> pointer on a node run: the set-once write a DevTask lane makes before the task's
///     first action, and the task-to-run lookup the Development apply guard reads.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowDevelopmentTaskStoreTests
{
    [Test]
    public async Task Set_PinsTheTaskOnce_AndBothTheSnapshotAndTheLookupReadItBack()
    {
        using var fixture = new GraphWorkflowTestFixture();
        var taskId = Guid.NewGuid();
        SeededNodeRun seeded;
        await using (var context = await fixture.CreateSchemaAsync())
        {
            var store = GraphWorkflowTestFixture.StoreFor(context);
            seeded = await SeedNodeRunAsync(context);

            AssertEx.True(await store.SetNodeRunDevelopmentTaskAsync(seeded.RunId, seeded.NodeRunId, taskId) is GraphWorkflowConditionalWriteResult.Written);
            AssertEx.True(await store.SetNodeRunDevelopmentTaskAsync(seeded.RunId, seeded.NodeRunId, taskId) is GraphWorkflowConditionalWriteResult.Written,
                "a restarted lane re-pinning its own task is a no-op, not a refusal.");
            AssertEx.Equal<GraphWorkflowConditionalWriteResult>(new GraphWorkflowConditionalWriteResult.Declined
            {
                Reason = GraphWorkflowWriteDecline.NodeRunNotEligible
            }, await store.SetNodeRunDevelopmentTaskAsync(seeded.RunId, seeded.NodeRunId, Guid.NewGuid()), "a pinned row never moves to another task.");
        }

        await using var readContext = fixture.CreateContext();
        var readStore = GraphWorkflowTestFixture.StoreFor(readContext);
        AssertEx.Equal<Guid?>(taskId, (await readStore.GetNodeRunAsync(seeded.RunId, "implement")).DevelopmentTaskId);
        var owner = (await readStore.ListDevelopmentTaskRunsAsync([taskId])).Single();
        AssertEx.Equal(taskId, owner.DevelopmentTaskId);
        AssertEx.Equal(seeded.RunId, owner.RunId);
        AssertEx.Equal(GraphWorkflowRunStatus.Running, owner.RunStatus);
    }

    [Test]
    public async Task Set_Declines_APendingRow_ASettledRow_AndAnEndedRun()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var notEligible = new GraphWorkflowConditionalWriteResult.Declined
        {
            Reason = GraphWorkflowWriteDecline.NodeRunNotEligible
        };

        var pending = await SeedNodeRunAsync(context, GraphWorkflowNodeRunStatus.Pending);
        AssertEx.Equal<GraphWorkflowConditionalWriteResult>(notEligible, await store.SetNodeRunDevelopmentTaskAsync(pending.RunId, pending.NodeRunId, Guid.NewGuid()),
            "a row nothing dispatched yet has no lane to pin a task.");

        var settled = await SeedNodeRunAsync(context, GraphWorkflowNodeRunStatus.Succeeded);
        AssertEx.Equal<GraphWorkflowConditionalWriteResult>(notEligible, await store.SetNodeRunDevelopmentTaskAsync(settled.RunId, settled.NodeRunId, Guid.NewGuid()));

        var ended = await SeedNodeRunAsync(context, runStatus: GraphWorkflowRunStatus.Completed);
        AssertEx.Equal<GraphWorkflowConditionalWriteResult>(new GraphWorkflowConditionalWriteResult.Declined
        {
            Reason = GraphWorkflowWriteDecline.RunNotActive
        }, await store.SetNodeRunDevelopmentTaskAsync(ended.RunId, ended.NodeRunId, Guid.NewGuid()));

        AssertEx.Empty(await context.GraphWorkflowNodeRuns.Where(static entity => entity.DevelopmentTaskId != null).ToListAsync(), "a declined write writes nothing.");
    }

    /// <summary>EVERY owner, newest run first, each with its status: the guard refuses on any live one, so "latest wins" alone would hide one.</summary>
    [Test]
    public async Task List_ReturnsEveryOwningRunNewestFirst_WithItsStatus_AndNothingForATaskNoRunPinned()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var taskId = Guid.NewGuid();
        var otherTaskId = Guid.NewGuid();

        var older = await SeedNodeRunAsync(context);
        var newer = await SeedNodeRunAsync(context);
        var other = await SeedNodeRunAsync(context);
        _ = await store.SetNodeRunDevelopmentTaskAsync(older.RunId, older.NodeRunId, taskId);
        _ = await store.SetNodeRunDevelopmentTaskAsync(newer.RunId, newer.NodeRunId, taskId);
        _ = await store.SetNodeRunDevelopmentTaskAsync(other.RunId, other.NodeRunId, otherTaskId);
        _ = await context.GraphWorkflowRuns.Where(entity => entity.Id == newer.RunId)
                         .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.CreatedAtUtc, 2)
                                                               .SetProperty(entity => entity.Status, GraphWorkflowRunStatus.Completed));

        var owners = await store.ListDevelopmentTaskRunsAsync([taskId]);

        AssertEx.Equal($"{newer.RunId}:Completed,{older.RunId}:Running", string.Join(',', owners.Select(static owner => $"{owner.RunId}:{owner.RunStatus}")));
        AssertEx.True(owners.All(owner => owner.DevelopmentTaskId == taskId), "another task's owner is not this task's.");
        AssertEx.Empty(await store.ListDevelopmentTaskRunsAsync([Guid.NewGuid()]));
        AssertEx.Empty(await store.ListDevelopmentTaskRunsAsync([]));
    }

    private static async Task<SeededNodeRun> SeedNodeRunAsync(NodeChatDbContext context,
        GraphWorkflowNodeRunStatus status = GraphWorkflowNodeRunStatus.Running,
        GraphWorkflowRunStatus runStatus = GraphWorkflowRunStatus.Running)
    {
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(GraphWorkflowTestFixture.StoreFor(context), "Driven " + Guid.NewGuid().ToString("N"));
        var runId = await GraphWorkflowTestFixture.SeedRunAsync(context, definition.Id, runStatus);
        var nodeRunId = await GraphWorkflowTestFixture.SeedNodeRunAsync(context, runId, "implement", GraphWorkflowNodeKind.Agent, status);
        context.ChangeTracker.Clear();
        return new SeededNodeRun
        {
            RunId = runId,
            NodeRunId = nodeRunId
        };
    }

    private sealed record SeededNodeRun
    {
        public required Guid RunId { get; init; }

        public required Guid NodeRunId { get; init; }
    }
}
