namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddLocalModelProxyApiKey</c> creates the inbound model-proxy credential table. Same show-once shape as the
///     MCP key: a display <c>prefix</c> plus a one-way <c>key_hash</c>, and no column that could hold the plaintext.
/// </summary>
public sealed class AddLocalModelProxyApiKeyMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_CreatesLocalModelProxyApiKeysWithHashOnly()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("local-model-proxy-api-key.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("local_model_proxy_api_keys").ConfigureAwait(false),
            "local_model_proxy_api_keys must exist.");

        var columns = await probe.ColumnsAsync("local_model_proxy_api_keys").ConfigureAwait(false);
        AssertEx.True(columns.IsSupersetOf(new[]
        {
            "id",
            "prefix",
            "key_hash",
            "created_at_utc",
            "last_used_at_utc"
        }), "local_model_proxy_api_keys must expose the mapped columns.");

        AssertEx.False(columns.Contains("key"), "There must be no column the plaintext proxy key could be stored in.");
    }
}
