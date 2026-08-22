namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class McpServerApiKeyStoreTests : IDisposable
{
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"mcp-key-store-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var store = new McpServerApiKeyStore(context, new FixedTimeProvider());

        _ = await store.SetAsync("xemcp_first", new byte[] { 1, 2, 3 }, 1).ConfigureAwait(false);
        var replacement = await store.SetAsync("xemcp_second", new byte[] { 4, 5, 6 }, 0).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, await context.McpServerApiKeys.CountAsync().ConfigureAwait(false));
        AssertEx.Equal("xemcp_second", replacement.Prefix);
        AssertEx.Equal(0, replacement.Scope);
        AssertEx.True(replacement.KeyHash.Span.SequenceEqual(new byte[] { 4, 5, 6 }),
            "A rotation must expose only the replacement digest.");
    }

    [Test]
    public async Task SetAsync_WithUndefinedScope_IsRejected()
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(_databasePath, _keyHolder);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var store = new McpServerApiKeyStore(context, new FixedTimeProvider());

        _ = await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SetAsync("xemcp_invalid", new byte[] { 1, 2, 3 }, scope: 2)).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, await context.McpServerApiKeys.CountAsync().ConfigureAwait(false));
    }

    [Test]
    public async Task TouchLastUsedAsync_AfterRotation_DoesNotStampReplacement()
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(_databasePath, _keyHolder);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var store = new McpServerApiKeyStore(context, new FixedTimeProvider());
        var original = await store.SetAsync("xemcp_first", new byte[] { 1, 2, 3 }, scope: 1).ConfigureAwait(false);
        var replacement = await store.SetAsync("xemcp_second", new byte[] { 4, 5, 6 }, scope: 0).ConfigureAwait(false);

        var touched = await store.TouchLastUsedAsync(original.GenerationId, timestampUtc: 999).ConfigureAwait(false);

        AssertEx.False(touched, "A stale validation generation must lose to key rotation.");
        var current = AssertEx.NotNull(await store.GetAsync().ConfigureAwait(false));
        AssertEx.Equal(replacement.GenerationId, current.GenerationId);
        AssertEx.Null(current.LastUsedAtUtc);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(1);
    }
}
