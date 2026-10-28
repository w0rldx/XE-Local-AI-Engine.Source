namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddMcpServerApiKey</c> creates the inbound MCP credential table with an encrypted <c>material</c> column;
///     <c>HashMcpServerApiKey</c> next empties it and renames that to <c>key_hash</c>, so both ends are pinned.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddMcpServerApiKeyMigrationTests
{
    private const string ThisMigrationId = "20260803153806_AddMcpServerApiKey";

    [Test]
    public async Task Migrate_ToThisMigration_CreatesMcpServerApiKeysWithMaterialColumn()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("mcp-server-api-key.sqlite", ThisMigrationId);

        AssertEx.True(await probe.TableExistsAsync("mcp_server_api_keys"), "mcp_server_api_keys must exist.");

        var columns = await probe.ColumnsAsync("mcp_server_api_keys");
        AssertEx.True(columns.SetEquals(new[]
        {
            "id",
            "prefix",
            "material",
            "created_at_utc",
            "last_used_at_utc"
        }), "mcp_server_api_keys must expose exactly the columns this migration created.");
    }

    [Test]
    public async Task Migrate_ToLatest_LeavesTheDigestColumnAndNoPlaintextColumn()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("mcp-server-api-key-latest.sqlite");

        var columns = await probe.ColumnsAsync("mcp_server_api_keys");
        AssertEx.True(columns.Contains("key_hash"), "HashMcpServerApiKey must have renamed material to key_hash.");
        AssertEx.False(columns.Contains("material"), "The recoverable-secret column must be gone.");
        AssertEx.False(columns.Contains("key"), "There must be no column the plaintext MCP key could be stored in.");
    }
}
