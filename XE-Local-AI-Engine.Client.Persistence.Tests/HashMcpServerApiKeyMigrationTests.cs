namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>HashMcpServerApiKey</c> turns the inbound-MCP credential into a one-way digest, and the <c>DELETE</c> it runs
///     first is load-bearing: a surviving row's ciphertext is sealed under the old AAD column name, so after the rename
///     <b>every</b> read throws.
/// </summary>
/// <remarks>
///     Forcing regeneration is the deliberate trade; converting in place needs the node key, which a migration lacks.
///     The rename is pinned at both ends by <c>AddMcpServerApiKeyMigrationTests</c>; this suite owns the deletion.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class HashMcpServerApiKeyMigrationTests
{
    private const string PreHashMigrationId = "20260803153806_AddMcpServerApiKey";
    private const string ThisMigrationId = "20260803163513_HashMcpServerApiKey";

    [Test]
    public async Task Migrate_OverAnExistingKey_DeletesItRatherThanCarryingItForward()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("hash-mcp-api-key.sqlite", PreHashMigrationId);

        await probe.ExecuteAsync("""
                                 INSERT INTO mcp_server_api_keys (id, prefix, material, created_at_utc)
                                 VALUES ($id, 'xemcp_', X'0102030405', 1234);
                                 """,
            command => command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()));

        AssertEx.Equal(expected: 1L, (await probe.LongsAsync("SELECT COUNT(*) FROM mcp_server_api_keys;")).Single(),
            "The pre-migration key must actually be present, or this test proves nothing.");

        await probe.MigrateToAsync(ThisMigrationId);

        AssertEx.Equal(expected: 0L, (await probe.LongsAsync("SELECT COUNT(*) FROM mcp_server_api_keys;")).Single(),
            "The old credential must be deleted — a renamed row would be undecryptable ciphertext that breaks every read.");

        var columns = await probe.ColumnsAsync("mcp_server_api_keys");
        AssertEx.True(columns.Contains("key_hash"), "The credential column must have been renamed to key_hash.");
        AssertEx.False(columns.Contains("material"), "The recoverable-secret column must be gone.");
    }
}
