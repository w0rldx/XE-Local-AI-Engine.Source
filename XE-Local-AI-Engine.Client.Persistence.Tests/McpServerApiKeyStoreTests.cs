namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class McpServerApiKeyStoreTests : IDisposable
{
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"mcp-key-store-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        // Scoped to THIS database, never the process-global ClearAllPools: that one closes the pooled handle a
        // parallel sibling's in-flight command is using. Clearing this pool is what lets Windows delete the file.
        using (var poolKey = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(poolKey);
        }

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task SetAsync_RotatesHashPrefixAndScopeOnTheSingletonRow()
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(_databasePath, _keyHolder);
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerApiKeyStore(context, new FixedTimeProvider());

        _ = await store.SetAsync("xemcp_first", new byte[]
        {
            1,
            2,
            3
        }, 1);
        var replacement = await store.SetAsync("xemcp_second", new byte[]
        {
            4,
            5,
            6
        }, 0);

        AssertEx.Equal(expected: 1, await context.McpServerApiKeys.CountAsync());
        AssertEx.Equal("xemcp_second", replacement.Prefix);
        AssertEx.Equal(0, replacement.Scope);
        AssertEx.True(replacement.KeyHash.Span.SequenceEqual(new byte[]
            {
                4,
                5,
                6
            }),
            "A rotation must expose only the replacement digest.");
    }

    [Test]
    public async Task SetAsync_WithUndefinedScope_IsRejected()
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(_databasePath, _keyHolder);
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerApiKeyStore(context, new FixedTimeProvider());

        _ = await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SetAsync("xemcp_invalid", new byte[]
            {
                1,
                2,
                3
            }, scope: 2));

        AssertEx.Equal(expected: 0, await context.McpServerApiKeys.CountAsync());
    }

    [Test]
    public async Task TouchLastUsedAsync_AfterRotation_DoesNotStampReplacement()
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(_databasePath, _keyHolder);
        await context.Database.EnsureCreatedAsync();
        var store = new McpServerApiKeyStore(context, new FixedTimeProvider());
        var original = await store.SetAsync("xemcp_first", new byte[]
        {
            1,
            2,
            3
        }, scope: 1);
        var replacement = await store.SetAsync("xemcp_second", new byte[]
        {
            4,
            5,
            6
        }, scope: 0);

        var touched = await store.TouchLastUsedAsync(original.GenerationId, timestampUtc: 999);

        AssertEx.False(touched, "A stale validation generation must lose to key rotation.");
        var current = AssertEx.NotNull(await store.GetAsync());
        AssertEx.Equal(replacement.GenerationId, current.GenerationId);
        AssertEx.Null(current.LastUsedAtUtc);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UnixEpoch.AddDays(1);
    }
}
