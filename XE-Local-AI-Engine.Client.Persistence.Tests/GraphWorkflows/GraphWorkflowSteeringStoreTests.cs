namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The steer intent on a node run: the encrypted <c>steering_json</c> append, its compare-and-set preconditions, and
///     the two ways a tick judges an entry — applied inside the reset, or ignored with its own event.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowSteeringStoreTests
{
    [Test]
    public async Task Append_OnARunningRow_StampsTheRowsAttemptAndInvocation_AndNeverWritesTheTextAsPlaintext()
    {
        using var fixture = new GraphWorkflowTestFixture();
        var message = "STEERNEEDLE-" + Guid.NewGuid().ToString("N");
        var invocationId = Guid.NewGuid();
        Guid runId;
        Guid nodeRunId;
        await using (var context = await fixture.CreateSchemaAsync())
        {
            (runId, nodeRunId) = await SeedRunningAgentAsync(context, invocationId);
            var store = GraphWorkflowTestFixture.StoreFor(context);

            AssertEx.NotNull(await store.AppendNodeRunSteeringAsync(Steer(runId, nodeRunId, Guid.NewGuid(), message)));
        }

        await using (var readContext = fixture.CreateContext())
        {
            var steering = (await GraphWorkflowTestFixture.StoreFor(readContext).GetNodeRunAsync(runId, "analyze")).Steering;
            AssertEx.Equal(1, steering.Count);
            var entry = steering[0];
            AssertEx.Equal(message, entry.Message);
            AssertEx.Equal(1, entry.Attempt);
            AssertEx.Equal<Guid?>(invocationId, entry.InvocationId);
            AssertEx.Equal("operator", entry.SteeredBySubject);
            AssertEx.Null(entry.Applied, "no tick has judged it yet.");
        }

        var fileBytes = await SqliteFileProbe.ReadAllBytesAsync(fixture.DatabasePath);
        AssertEx.False(fileBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(message)) >= 0, "steering text is operator free text and is encrypted at rest.");
    }

    [Test]
    public async Task Append_Declines_AReusedOperationId_AFullRow_ASettledRow_AndACancellingRun()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var (runId, nodeRunId) = await SeedRunningAgentAsync(context, Guid.NewGuid());
        var operationId = Guid.NewGuid();
        AssertEx.NotNull(await store.AppendNodeRunSteeringAsync(Steer(runId, nodeRunId, operationId, "first", maxEntries: 2)));

        AssertEx.Null(await store.AppendNodeRunSteeringAsync(Steer(runId, nodeRunId, operationId, "again", maxEntries: 2)), "an id is appended once.");
        AssertEx.NotNull(await store.AppendNodeRunSteeringAsync(Steer(runId, nodeRunId, Guid.NewGuid(), "second", maxEntries: 2)));
        AssertEx.Null(await store.AppendNodeRunSteeringAsync(Steer(runId, nodeRunId, Guid.NewGuid(), "third", maxEntries: 2)), "the cap is checked inside the write.");

        var (settledRunId, settledNodeRunId) = await SeedRunningAgentAsync(context, Guid.NewGuid(), GraphWorkflowNodeRunStatus.Succeeded);
        AssertEx.Null(await store.AppendNodeRunSteeringAsync(Steer(settledRunId, settledNodeRunId, Guid.NewGuid(), "late")), "a settled row is not steerable.");

        var (cancellingRunId, cancellingNodeRunId) = await SeedRunningAgentAsync(context, Guid.NewGuid(), runStatus: GraphWorkflowRunStatus.Cancelling);
        AssertEx.Null(await store.AppendNodeRunSteeringAsync(Steer(cancellingRunId, cancellingNodeRunId, Guid.NewGuid(), "late")), "a draining run is not steerable.");

        AssertEx.Equal("first,second", string.Join(',', (await store.GetNodeRunAsync(runId, "analyze")).Steering.Select(static entry => entry.Message)));
    }

    [Test]
    public async Task TheResetAppliesItsEntries_AndAnIgnoredEntryIsJudgedOnceWithItsOwnEvent()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var (runId, nodeRunId) = await SeedRunningAgentAsync(context, Guid.NewGuid());
        var applied = Guid.NewGuid();
        var ignored = Guid.NewGuid();
        _ = await store.AppendNodeRunSteeringAsync(Steer(runId, nodeRunId, applied, "apply me"));
        _ = await store.AppendNodeRunSteeringAsync(Steer(runId, nodeRunId, ignored, "ignore me"));

        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand
        {
            RunId = runId,
            NodeRunId = nodeRunId,
            ExpectedVersion = GraphWorkflowVersions.Any,
            TargetStatus = GraphWorkflowNodeRunStatus.Pending,
            EventType = GraphWorkflowEventTypes.NodeSteered,
            AppliedSteeringOperationIds = [applied]
        });
        AssertEx.NotNull(await store.IgnoreNodeRunSteeringAsync(runId, nodeRunId, ignored));
        AssertEx.Null(await store.IgnoreNodeRunSteeringAsync(runId, nodeRunId, applied), "an entry already judged is not judged again.");

        var nodeRun = await store.GetNodeRunAsync(runId, "analyze");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Pending, nodeRun.Status);
        AssertEx.Equal(1, nodeRun.Attempt, "a steer never spends an attempt.");
        AssertEx.Equal<bool?>(true, nodeRun.Steering.Single(entry => entry.OperationId == applied).Applied);
        AssertEx.Equal<bool?>(false, nodeRun.Steering.Single(entry => entry.OperationId == ignored).Applied);
        var events = (await store.ListEventsAsync(runId)).Select(static entry => entry.EventType).ToList();
        AssertEx.Equal(1, events.Count(static type => type == GraphWorkflowEventTypes.NodeSteered));
        AssertEx.Equal(1, events.Count(static type => type == GraphWorkflowEventTypes.NodeSteerIgnored));
        var ignoredDetail = (await store.ListEventsAsync(runId)).Single(static entry => entry.EventType == GraphWorkflowEventTypes.NodeSteerIgnored).DetailJson;
        AssertEx.True(ignoredDetail is not null && ignoredDetail.Contains("\"message\":\"ignore me\"", StringComparison.Ordinal)
                      && ignoredDetail.Contains($"\"operationId\":\"{ignored}\"", StringComparison.Ordinal) && ignoredDetail.Contains("\"attempt\":1", StringComparison.Ordinal),
            ignoredDetail ?? "no detail");
    }

    /// <summary>Its own AAD column name: steering text presented as the node's routed output must fail the tag check.</summary>
    [Test]
    public async Task CopyingTheSteeringBlobIntoTheOutputColumn_FailsAuthenticatedDecryption()
    {
        using var fixture = new GraphWorkflowTestFixture();
        Guid nodeRunId;
        await using (var context = await fixture.CreateSchemaAsync())
        {
            (var runId, nodeRunId) = await SeedRunningAgentAsync(context, Guid.NewGuid());
            _ = await GraphWorkflowTestFixture.StoreFor(context).AppendNodeRunSteeringAsync(Steer(runId, nodeRunId, Guid.NewGuid(), "route elsewhere"));
        }

        await fixture.RawExecuteAsync("UPDATE graph_workflow_node_runs SET output_json = steering_json WHERE id = $nodeRun;",
            command => command.Parameters.AddWithValue("$nodeRun", nodeRunId));

        await using var readContext = fixture.CreateContext();
        _ = AssertEx.Throws<CryptographicException>(() => AssertEx.NotNull(readContext.GraphWorkflowNodeRuns.AsNoTracking().Where(entity => entity.Id == nodeRunId).ToList()));
    }

    private static AppendGraphWorkflowSteeringCommand Steer(Guid runId, Guid nodeRunId, Guid operationId, string message, int maxEntries = 5) =>
        new() { RunId = runId, NodeRunId = nodeRunId, OperationId = operationId, Message = message, SteeredBySubject = "operator", MaxEntries = maxEntries };

    private static async Task<(Guid RunId, Guid NodeRunId)> SeedRunningAgentAsync(NodeChatDbContext context,
        Guid invocationId,
        GraphWorkflowNodeRunStatus status = GraphWorkflowNodeRunStatus.Running,
        GraphWorkflowRunStatus runStatus = GraphWorkflowRunStatus.Running)
    {
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(GraphWorkflowTestFixture.StoreFor(context), "Steered " + Guid.NewGuid().ToString("N"));
        var runId = await GraphWorkflowTestFixture.SeedRunAsync(context, definition.Id, runStatus);
        var nodeRunId = await GraphWorkflowTestFixture.SeedNodeRunAsync(context, runId, "analyze", GraphWorkflowNodeKind.Agent, status);
        _ = await context.GraphWorkflowNodeRuns.Where(entity => entity.Id == nodeRunId).ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.InvocationId, invocationId));

        // The bulk update bypasses the tracker, so the seeded instance would otherwise shadow the column the store reads.
        context.ChangeTracker.Clear();
        return (runId, nodeRunId);
    }
}
