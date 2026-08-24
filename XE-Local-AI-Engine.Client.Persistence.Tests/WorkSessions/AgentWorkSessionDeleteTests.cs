namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AgentWorkSessionDeleteTests
{
    [Test]
    public async Task DeleteAsync_EmptiesAllSixTablesAndLeavesASiblingIntact()
    {
        using var fixture = new WorkSessionTestFixture();
        var doomedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();

        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var store = WorkSessionTestFixture.StoreFor(context);
            await PopulateAsync(store, doomedId).ConfigureAwait(false);
            await PopulateAsync(store, survivorId).ConfigureAwait(false);

            var removed = await store.DeleteAsync(doomedId).ConfigureAwait(false);
            AssertEx.True(removed >= 6, $"A populated session should remove at least one row per table; removed {removed}.");
        }

        // Raw COUNT(*), not an EF-graph assertion: the connection runs without PRAGMA foreign_keys, so a cascade-based
        // delete would false-pass through the change tracker while leaving every child row on disk.
        foreach (var (table, column) in Tables)
        {
            AssertEx.Equal(expected: 0L, await fixture.RawCountAsync(table, column, doomedId).ConfigureAwait(false), $"{table} must be empty for the deleted session.");
            AssertEx.True(await fixture.RawCountAsync(table, column, survivorId).ConfigureAwait(false) > 0, $"{table} must still carry the sibling session's rows.");
        }
    }

    [Test]
    public async Task PurgingTheConversation_TakesTheSessionAndItsWholeSubtree()
    {
        using var fixture = new WorkSessionTestFixture();
        var purgedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        Guid purgedConversationId;

        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var store = WorkSessionTestFixture.StoreFor(context);
            await PopulateAsync(store, purgedId).ConfigureAwait(false);
            await PopulateAsync(store, survivorId).ConfigureAwait(false);
            purgedConversationId = (await store.GetAsync(purgedId).ConfigureAwait(false)).ConversationId;

            // A session owns its conversation, so a retention purge that left the objective, plan and findings behind
            // would be exactly the privacy gap ConversationFootprintPurge exists to close.
            await ConversationFootprintPurge.DeleteAsync(context, purgedConversationId, CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var (table, column) in Tables)
        {
            AssertEx.Equal(expected: 0L, await fixture.RawCountAsync(table, column, purgedId).ConfigureAwait(false), $"{table} must be empty after the conversation purge.");
            AssertEx.True(await fixture.RawCountAsync(table, column, survivorId).ConfigureAwait(false) > 0, $"{table} must still carry the untouched session's rows.");
        }
    }

    [Test]
    public async Task DeleteAsync_OnAnUnknownSessionRemovesNothing()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = WorkSessionTestFixture.StoreFor(context);

        AssertEx.Equal(expected: 0, await store.DeleteAsync(Guid.NewGuid()).ConfigureAwait(false));
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
        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId).ConfigureAwait(false);
        var planned = await store.ApplyPlanAsync(new ApplyWorkPlanCommand(sessionId,
                created.Version,
                Guid.NewGuid(),
                AgentWorkSessionTaskOrigin.Agent,
                [new WorkPlanTaskChange(Guid.NewGuid(), WorkPlanTaskOperation.Add, Title: "Task")]))
            .ConfigureAwait(false);
        var found = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                Guid.NewGuid(),
                planned.Version,
                Guid.NewGuid(),
                AgentWorkSessionFindingKind.Finding,
                "Finding."))
            .ConfigureAwait(false);
        var artifactId = Guid.NewGuid();
        var saved = await store.AppendArtifactAsync(new AppendWorkSessionArtifactCommand(sessionId,
                artifactId,
                found.Version,
                Guid.NewGuid(),
                AgentWorkSessionArtifactKind.Report,
                "report.md",
                "text/markdown",
                "HASH",
                SizeBytes: 4,
                string.Concat(sessionId.ToString("N"), "/", artifactId.ToString("N"))))
            .ConfigureAwait(false);
        _ = await store.AppendCheckpointAsync(new AppendWorkSessionCheckpointCommand(sessionId,
                Guid.NewGuid(),
                saved.Version,
                Guid.NewGuid(),
                Step: 0,
                "Summary.",
                "{}"))
            .ConfigureAwait(false);
    }
}
