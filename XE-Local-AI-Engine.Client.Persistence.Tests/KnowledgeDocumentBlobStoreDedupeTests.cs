namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Content-hash dedupe on the real store: a second upload of byte-identical content must not create a second row. The
///     store inserts with <c>ON CONFLICT(content_hash) DO NOTHING</c> and re-selects the existing id, so the caller learns
///     the content already exists (<c>WasInserted = false</c>) and is pointed at the first document's id. This exercises
///     the unique <c>content_hash</c> index the migration creates.
/// </summary>
public sealed class KnowledgeDocumentBlobStoreDedupeTests : IDisposable
{
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task AddAsync_WhenIdenticalContentIsAddedTwice_SecondAddDedupesToTheFirstDocumentId()
    {
        var databasePath = GetDatabasePath("dedupe.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);

        var content = Encoding.UTF8.GetBytes("identical knowledge-base document bytes");
        var contentHash = Convert.ToHexString(SHA256.HashData(content));

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);

        var firstId = Guid.NewGuid();
        var first = await store.AddAsync(NewInput(firstId, content, contentHash), CancellationToken.None).ConfigureAwait(false);
        var second = await store.AddAsync(NewInput(Guid.NewGuid(), content, contentHash), CancellationToken.None).ConfigureAwait(false);

        AssertEx.True(first.WasInserted, "The first add of new content should insert a row.");
        AssertEx.False(second.WasInserted, "The second add of identical content should be deduped, not inserted.");
        AssertEx.Equal(firstId, second.DocumentId);
    }

    [Test]
    public async Task AddAsync_WhenIdenticalContentIsAddedTwice_LeavesExactlyOneDocumentRow()
    {
        var databasePath = GetDatabasePath("dedupe-count.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);

        var content = Encoding.UTF8.GetBytes("another identical payload");
        var contentHash = Convert.ToHexString(SHA256.HashData(content));

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);

        _ = await store.AddAsync(NewInput(Guid.NewGuid(), content, contentHash), CancellationToken.None).ConfigureAwait(false);
        _ = await store.AddAsync(NewInput(Guid.NewGuid(), content, contentHash), CancellationToken.None).ConfigureAwait(false);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM knowledge_documents;";
        var count = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        AssertEx.Equal(expected: 1L, count);
    }

    private static KnowledgeDocumentInput NewInput(Guid documentId, byte[] content, string contentHash)
    {
        return new KnowledgeDocumentInput(documentId,
            "notes.txt",
            "text/plain",
            ".txt",
            content.Length,
            contentHash,
            content,
            "nomic-embed-text");
    }

    private KnowledgeDocumentBlobStore CreateStore(ServiceProvider provider)
    {
        return new KnowledgeDocumentBlobStore(provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedNodeDataDirectory(Path.Combine(_rootPath, "data")),
            _keyHolder,
            TimeProvider.System);
    }

    private ServiceProvider BuildProvider(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateContextWithForeignKeysOff(databasePath));
        return services.BuildServiceProvider();
    }

    // Microsoft.Data.Sqlite enables foreign-key enforcement by default; the node-sqlite runtime connection does not,
    // so every KB test connection is aligned to that runtime mode even where this store's inserts have no FK to trip.
    private NodeChatDbContext CreateContextWithForeignKeysOff(string databasePath)
    {
        var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var connection = context.Database.GetDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF;";
        _ = command.ExecuteNonQuery();
        return context;
    }

    private async Task MigrateAsync(string databasePath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private sealed class FixedNodeDataDirectory(string root) : INodeDataDirectory
    {
        public string Root { get; } = root;
    }
}
