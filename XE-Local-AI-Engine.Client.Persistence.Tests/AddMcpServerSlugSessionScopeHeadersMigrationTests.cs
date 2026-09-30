namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Pins the MCP hardening migration on seeded pre-migration rows: disabled rows, names that slugify alike, a
///     <c>?token=</c> URL and an environment on an HTTP row must all survive untouched, with no slug assigned yet.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddMcpServerSlugSessionScopeHeadersMigrationTests
{
    private const string PreviousMigrationId = "20260924201944_AddConversationState";
    private const string ThisMigrationId = "20260930182455_AddMcpServerSlugSessionScopeHeaders";

    [Test]
    public async Task Migrate_SeededRegistrations_AddsNullSlugsSharedScopeAndNoHeaders_AndKeepsEveryRowIntact()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("mcp-servers-slug-scope-headers.sqlite", PreviousMigrationId);
        await probe.ExecuteAsync("""
                                 INSERT INTO mcp_servers (id, name, transport_kind, command, url, env, trust_tier, enabled, version, created_at_utc, updated_at_utc)
                                 VALUES
                                   ('0b8f6c1e-0000-4000-8000-000000000001', 'Ticket Desk', 0, 'ticket', NULL, NULL, 0, 0, 3, 1, 1),
                                   ('0b8f6c1e-0000-4000-8000-000000000002', 'ticket_desk', 1, NULL, 'http://127.0.0.1:18912/mcp?token=s3cr3t', X'01020304', 0, 1, 5, 2, 2),
                                   ('0b8f6c1e-0000-4000-8000-000000000003', 'ticket-desk!', 0, 'ticket', NULL, NULL, 1, 1, 1, 3, 3);
                                 """);

        await probe.MigrateToAsync(ThisMigrationId);

        var columns = await probe.ColumnsAsync("mcp_servers");
        AssertEx.True(columns.Contains("slug") && columns.Contains("session_scope") && columns.Contains("headers"),
            "The migration must add the slug, session_scope and headers columns.");
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("mcp_servers", "session_scope"));
        AssertEx.True(await probe.IndexExistsAsync("mcp_servers", "IX_mcp_servers_slug", unique: true, "slug"),
            "Two servers must never share a tool-name slug.");

        AssertEx.Equal(expected: 0L, (await probe.LongsAsync("SELECT COUNT(*) FROM mcp_servers WHERE slug IS NOT NULL;")).Single(),
            "No slug is assigned by the migration: the first connect assigns it in enabled order, reproducing the old suffixes.");
        AssertEx.Equal(expected: 0L, (await probe.LongsAsync("SELECT COUNT(*) FROM mcp_servers WHERE session_scope <> 0 OR headers IS NOT NULL;")).Single(),
            "Every existing registration migrates to the Shared scope with no headers.");

        AssertEx.Equal("http://127.0.0.1:18912/mcp?token=s3cr3t",
            Convert.ToString(await probe.ScalarAsync("SELECT url FROM mcp_servers WHERE name = 'ticket_desk';")),
            "A pre-headers query-string credential stays stored so the row keeps connecting; it is masked on the wire, not rewritten.");
        AssertEx.Equal(expected: 1L, (await probe.LongsAsync("SELECT COUNT(*) FROM mcp_servers WHERE name = 'ticket_desk' AND env = X'01020304';")).Single(),
            "An environment stored on an HTTP row is kept byte for byte; only new writes reject it.");
        AssertEx.Equal("0,1,1", string.Join(",", await probe.LongsAsync("SELECT enabled FROM mcp_servers ORDER BY created_at_utc;")));
        AssertEx.Equal("3,5,1", string.Join(",", await probe.LongsAsync("SELECT version FROM mcp_servers ORDER BY created_at_utc;")),
            "Adding columns is not a connection change: no Version moves.");

        // NULL slugs coexist (SQLite treats them as distinct); a duplicate assigned slug does not.
        await probe.ExecuteAsync("UPDATE mcp_servers SET slug = 'ticket-desk' WHERE name = 'ticket_desk';");
        _ = await AssertEx.ThrowsAsync<SqliteException>(() =>
            probe.ExecuteAsync("UPDATE mcp_servers SET slug = 'ticket-desk' WHERE name = 'ticket-desk!';"));
        _ = await AssertEx.ThrowsAsync<SqliteException>(() =>
            probe.ExecuteAsync("UPDATE mcp_servers SET session_scope = 2;"));
    }
}
