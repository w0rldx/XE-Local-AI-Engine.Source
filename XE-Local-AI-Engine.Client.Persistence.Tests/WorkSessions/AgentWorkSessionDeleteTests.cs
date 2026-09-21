namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class AgentWorkSessionDeleteTests
{
    [Test]
    public async Task DeleteAsync_EmptiesAllSixTablesAndLeavesASiblingIntact()
    {
        using var fixture = new WorkSessionTestFixture();
        var doomedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();

        await using (var context = await fixture.CreateSchemaAsync())
        {
            var store = WorkSessionTestFixture.StoreFor(context);
            await PopulateAsync(store, doomedId);
            await PopulateAsync(store, survivorId);

            var removed = await store.DeleteAsync(doomedId);
            AssertEx.True(removed >= 6, $"A populated session should remove at least one row per table; removed {removed}.");
        }

        // Raw COUNT(*), not an EF-graph assertion: the change tracker reports a removed graph even for rows that are
        // still on disk, so only a query that goes back to the file can tell the delete from a false pass.
        foreach (var (table, column) in Tables)
        {
            AssertEx.Equal(expected: 0L, await fixture.RawCountAsync(table, column, doomedId), $"{table} must be empty for the deleted session.");
            AssertEx.True(await fixture.RawCountAsync(table, column, survivorId) > 0, $"{table} must still carry the sibling session's rows.");
        }
    }

    [Test]
    public async Task PurgingTheConversation_TakesTheSessionAndItsWholeSubtree()
    {
        using var fixture = new WorkSessionTestFixture();
        var purgedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        Guid purgedConversationId;

        await using (var context = await fixture.CreateSchemaAsync())
        {
            var store = WorkSessionTestFixture.StoreFor(context);
            await PopulateAsync(store, purgedId);
            await PopulateAsync(store, survivorId);
            purgedConversationId = (await store.GetAsync(purgedId)).ConversationId;

            // A session owns its conversation, so a retention purge that left the objective, plan and findings behind
            // would be exactly the privacy gap ConversationFootprintPurge exists to close.
            await ConversationFootprintPurge.DeleteAsync(context, purgedConversationId, CancellationToken.None);
        }

        foreach (var (table, column) in Tables)
        {
            AssertEx.Equal(expected: 0L, await fixture.RawCountAsync(table, column, purgedId), $"{table} must be empty after the conversation purge.");
            AssertEx.True(await fixture.RawCountAsync(table, column, survivorId) > 0, $"{table} must still carry the untouched session's rows.");
        }
    }

    [Test]
    public async Task DeleteAsync_OnAnUnknownSessionRemovesNothing()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);

        AssertEx.Equal(expected: 0, await store.DeleteAsync(Guid.NewGuid()));
    }

    private static (string Table, string Column)[] Tables =>
    [
        ("agent_work_sessions", "id"),
        ("agent_work_session_tasks", "session_id"),
        ("agent_work_session_findings", "session_id"),
        ("agent_work_session_artifacts", "session_id"),
        ("agent_work_session_checkpoints", "session_id"),
        ("agent_work_session_events", "session_id")
    ];

    private static async Task PopulateAsync(AgentWorkSessionStore store, Guid sessionId)
    {
        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);
        var planned = await store.ApplyPlanAsync(new ApplyWorkPlanCommand
        {
            SessionId = sessionId,
            ExpectedVersion = created.Version,
            OperationId = Guid.NewGuid(),
            Origin = AgentWorkSessionTaskOrigin.Agent,
            Changes = [new WorkPlanTaskChange { TaskId = Guid.NewGuid(), Operation = WorkPlanTaskOperation.Add, Title = "Task" }]
        });
        var found = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand
        {
            SessionId = sessionId,
            FindingId = Guid.NewGuid(),
            ExpectedVersion = planned.Version,
            OperationId = Guid.NewGuid(),
            Kind = AgentWorkSessionFindingKind.Finding,
            Text = "Finding."
        });
        var artifactId = Guid.NewGuid();
        var saved = await store.AppendArtifactAsync(new AppendWorkSessionArtifactCommand
        {
            SessionId = sessionId,
            ArtifactId = artifactId,
            ExpectedVersion = found.Version,
            OperationId = Guid.NewGuid(),
            Kind = AgentWorkSessionArtifactKind.Report,
            Name = "report.md",
            MediaType = "text/markdown",
            ContentSha256 = "HASH",
            SizeBytes = 4,
            ManagedReference = string.Concat(sessionId.ToString("N"), "/", artifactId.ToString("N"))
        });
        _ = await store.AppendCheckpointAsync(new AppendWorkSessionCheckpointCommand
        {
            SessionId = sessionId,
            CheckpointId = Guid.NewGuid(),
            ExpectedVersion = saved.Version,
            OperationId = Guid.NewGuid(),
            Step = 0,
            Summary = "Summary.",
            StateJson = "{}"
        });
    }
}
