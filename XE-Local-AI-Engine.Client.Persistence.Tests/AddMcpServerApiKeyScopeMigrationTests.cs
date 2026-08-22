namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>Pins the trust-preserving backfill for MCP keys minted before scopes existed.</summary>
public sealed class AddMcpServerApiKeyScopeMigrationTests
{
    private const string PreviousMigrationId = "20260819092826_AddConversationListIndex";
    private const string ThisMigrationId = "20260822013858_AddMcpServerApiKeyScope";

    [Test]
    public async Task Migrate_ExistingKey_BackfillsDelegateScope()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("mcp-server-api-key-scope.sqlite", PreviousMigrationId)
                                                          .ConfigureAwait(false);
        await probe.ExecuteAsync("""
                                 INSERT INTO mcp_server_api_keys (id, prefix, key_hash, created_at_utc)
                                 VALUES ('6b1f0f2a-6f2f-4c1f-9d3e-7a4c0b5e8d21', 'xemcp_legacy', X'01020304', 1234);
                                 """).ConfigureAwait(false);

        await probe.MigrateToAsync(ThisMigrationId).ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("mcp_server_api_keys").ConfigureAwait(false);
        AssertEx.True(columns.Contains("scope"),
            "The singleton key row must persist its caller scope.");
        AssertEx.True(columns.Contains("generation_id"),
            "Each rotation needs a generation token so stale validation cannot stamp or authenticate its replacement.");
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("mcp_server_api_keys", "scope").ConfigureAwait(false));
        AssertEx.Equal(expected: 1L,
            (await probe.LongsAsync("SELECT \"notnull\" FROM pragma_table_info('mcp_server_api_keys') WHERE name = 'scope';")
                        .ConfigureAwait(false)).Single(),
            "Scope must be non-null so no key has ambiguous authority.");
        AssertEx.Equal(expected: 1L,
            (await probe.LongsAsync("SELECT \"notnull\" FROM pragma_table_info('mcp_server_api_keys') WHERE name = 'generation_id';")
                        .ConfigureAwait(false)).Single(),
            "The conditional last-used write requires every row to carry a generation token.");
        AssertEx.Equal(expected: 0L,
            (await probe.LongsAsync("SELECT scope FROM mcp_server_api_keys;").ConfigureAwait(false)).Single(),
            "Legacy credentials must remain delegate credentials after migration.");

        var tableSql = Convert.ToString(await probe.ScalarAsync("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'mcp_server_api_keys';").ConfigureAwait(false));
        AssertEx.True(AssertEx.NotNull(tableSql).Contains("CK_mcp_server_api_keys_scope", StringComparison.Ordinal),
            "The database must reject undefined scope values even when persistence is bypassed.");
        _ = await AssertEx.ThrowsAsync<SqliteException>(() =>
            probe.ExecuteAsync("UPDATE mcp_server_api_keys SET scope = 2;")).ConfigureAwait(false);
    }
}
