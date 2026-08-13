namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Content-hash dedupe on the real store: a second upload of byte-identical content must not create a second row. The
///     store inserts with <c>ON CONFLICT DO NOTHING</c> and re-selects the existing id, so the caller learns the content
///     already exists (<c>WasInserted = false</c>) and is pointed at the first document's id. Repository sources exercise
///     the separate stable path identity: identical bytes at different paths remain distinct, while changed bytes at one
///     path update that document and reset it for reindex.
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

    [Test]
    public async Task AddAsync_WhenRowExistsButBlobMissing_ReAddOfIdenticalBytesRepairsBlob()
    {
        var databasePath = GetDatabasePath("repair.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);

        var content = Encoding.UTF8.GetBytes("document bytes that must survive a crash between row commit and blob write");
        var contentHash = Convert.ToHexString(SHA256.HashData(content));

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);

        var firstId = Guid.NewGuid();
        _ = await store.AddAsync(NewInput(firstId, content, contentHash), CancellationToken.None).ConfigureAwait(false);

        // Simulate a crash that committed the row but never wrote (or lost) the blob: delete the on-disk bytes.
        var blobPath = Path.Combine(_rootPath, "data", "knowledge-base", "documents", string.Concat(firstId.ToString("D"), ".txt"));
        AssertEx.True(File.Exists(blobPath), "Precondition: the first add must have written the blob.");
        File.Delete(blobPath);

        // Re-upload the byte-identical content: the dedupe branch must self-heal the missing blob.
        var second = await store.AddAsync(NewInput(Guid.NewGuid(), content, contentHash), CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(second.WasInserted, "The re-upload of identical content must still dedupe, not insert a second row.");
        AssertEx.Equal(firstId, second.DocumentId);
        AssertEx.True(File.Exists(blobPath), "The dedupe-repair path must restore the missing blob.");

        var repaired = AssertEx.NotNull(await store.ReadBytesAsync(firstId, CancellationToken.None).ConfigureAwait(false));
        AssertEx.True(content.AsSpan().SequenceEqual(repaired), "The repaired blob must decrypt back to the original bytes.");
    }

    [Test]
    public async Task AddAsync_WhenRepairingMissingBlob_ResetsFailedRowToPendingForReindex()
    {
        // A prior crash left the row marked Failed once ingestion could not read its bytes. The
        // repair path restores the blob AND must reset the row to Pending, or UploadKnowledgeDocumentEndpoint (which only
        // enqueues fresh or Pending rows) would never re-index the recovered bytes and every re-upload would keep
        // returning the stuck Failed document.
        var databasePath = GetDatabasePath("repair-status.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);

        var content = Encoding.UTF8.GetBytes("bytes whose row was marked Failed after a crash lost the blob");
        var contentHash = Convert.ToHexString(SHA256.HashData(content));

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);

        var firstId = Guid.NewGuid();
        _ = await store.AddAsync(NewInput(firstId, content, contentHash), CancellationToken.None).ConfigureAwait(false);

        // Simulate the post-crash state: the blob is gone and ingestion has already flipped the row to Failed.
        var blobPath = Path.Combine(_rootPath, "data", "knowledge-base", "documents", string.Concat(firstId.ToString("D"), ".txt"));
        File.Delete(blobPath);
        await SetStatusAsync(databasePath, "Failed").ConfigureAwait(false);

        var second = await store.AddAsync(NewInput(Guid.NewGuid(), content, contentHash), CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(second.WasInserted, "The re-upload of identical content must still dedupe, not insert a second row.");
        AssertEx.Equal(firstId, second.DocumentId);
        AssertEx.True(File.Exists(blobPath), "The repair path must restore the missing blob.");
        AssertEx.Equal("Pending", await GetStatusAsync(databasePath).ConfigureAwait(false));
    }

    [Test]
    public async Task AddAsync_WhenBlobIntact_DedupeLeavesRowStatusUnchanged()
    {
        // The status reset is scoped to the missing-blob repair branch only: an ordinary dedupe hit against a document
        // that already indexed (blob present) must never be knocked back to Pending and re-ingested.
        var databasePath = GetDatabasePath("no-status-reset.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);

        var content = Encoding.UTF8.GetBytes("already-indexed intact payload");
        var contentHash = Convert.ToHexString(SHA256.HashData(content));

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);

        _ = await store.AddAsync(NewInput(Guid.NewGuid(), content, contentHash), CancellationToken.None).ConfigureAwait(false);
        await SetStatusAsync(databasePath, "Indexed").ConfigureAwait(false);

        _ = await store.AddAsync(NewInput(Guid.NewGuid(), content, contentHash), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal("Indexed", await GetStatusAsync(databasePath).ConfigureAwait(false));
    }

    [Test]
    public async Task AddAsync_WhenRowAndBlobBothPresent_DedupeDoesNotRewriteBlob()
    {
        var databasePath = GetDatabasePath("no-rewrite.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);

        var content = Encoding.UTF8.GetBytes("intact payload");
        var contentHash = Convert.ToHexString(SHA256.HashData(content));

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);

        var firstId = Guid.NewGuid();
        _ = await store.AddAsync(NewInput(firstId, content, contentHash), CancellationToken.None).ConfigureAwait(false);

        var blobPath = Path.Combine(_rootPath, "data", "knowledge-base", "documents", string.Concat(firstId.ToString("D"), ".txt"));
        var beforeWriteUtc = File.GetLastWriteTimeUtc(blobPath);

        var second = await store.AddAsync(NewInput(Guid.NewGuid(), content, contentHash), CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(second.WasInserted);
        AssertEx.Equal(beforeWriteUtc, File.GetLastWriteTimeUtc(blobPath), "An intact blob must not be rewritten by the dedupe path.");
    }

    [Test]
    public async Task AddAsync_RepositoryFilesWithIdenticalBytesAtDifferentPaths_RetainDistinctDocumentIds()
    {
        var databasePath = GetDatabasePath("repository-distinct-paths.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);
        var content = Encoding.UTF8.GetBytes("shared license text");
        var contentHash = Convert.ToHexString(SHA256.HashData(content));

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);
        var first = await store.AddAsync(NewRepositoryInput(Guid.NewGuid(), "src/LICENSE.txt", content, contentHash), CancellationToken.None)
                               .ConfigureAwait(false);
        var second = await store.AddAsync(NewRepositoryInput(Guid.NewGuid(), "third_party/LICENSE.txt", content, contentHash), CancellationToken.None)
                                .ConfigureAwait(false);

        AssertEx.True(first.WasInserted);
        AssertEx.True(second.WasInserted);
        AssertEx.False(first.DocumentId == second.DocumentId);
        AssertEx.Equal(expected: 2L, await CountDocumentsAsync(databasePath).ConfigureAwait(false));
    }

    [Test]
    public async Task AddAsync_SameCollectionAndPathFromDifferentRepositories_RetainsIndependentDocuments()
    {
        var databasePath = GetDatabasePath("repository-distinct-sources.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);
        var repositoryA = Encoding.UTF8.GetBytes("repository A readme");
        var repositoryB = Encoding.UTF8.GetBytes("repository B readme");

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = await store.AddAsync(NewRepositoryInput(firstId,
                "README.md",
                repositoryA,
                Convert.ToHexString(SHA256.HashData(repositoryA)),
                "repository-a"),
            CancellationToken.None).ConfigureAwait(false);
        var second = await store.AddAsync(NewRepositoryInput(secondId,
                "README.md",
                repositoryB,
                Convert.ToHexString(SHA256.HashData(repositoryB)),
                "repository-b"),
            CancellationToken.None).ConfigureAwait(false);

        AssertEx.True(first.WasInserted);
        AssertEx.True(second.WasInserted);
        AssertEx.Equal(firstId, first.DocumentId);
        AssertEx.Equal(secondId, second.DocumentId);
        AssertEx.Equal(expected: 2L, await CountDocumentsAsync(databasePath).ConfigureAwait(false));
        var storedA = AssertEx.NotNull(await store.ReadBytesAsync(firstId, CancellationToken.None).ConfigureAwait(false));
        var storedB = AssertEx.NotNull(await store.ReadBytesAsync(secondId, CancellationToken.None).ConfigureAwait(false));
        AssertEx.True(repositoryA.AsSpan().SequenceEqual(storedA));
        AssertEx.True(repositoryB.AsSpan().SequenceEqual(storedB));
    }

    [Test]
    public async Task AddAsync_UpdatingOneRepositorySource_DoesNotOverwriteSamePathInAnotherRepository()
    {
        var databasePath = GetDatabasePath("repository-source-update-isolation.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);
        var originalA = Encoding.UTF8.GetBytes("repository A original");
        var changedA = Encoding.UTF8.GetBytes("repository A changed");
        var repositoryB = Encoding.UTF8.GetBytes("repository B unchanged");

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        _ = await store.AddAsync(NewRepositoryInput(firstId,
                "src/Widget.cs",
                originalA,
                Convert.ToHexString(SHA256.HashData(originalA)),
                "repository-a"),
            CancellationToken.None).ConfigureAwait(false);
        _ = await store.AddAsync(NewRepositoryInput(secondId,
                "src/Widget.cs",
                repositoryB,
                Convert.ToHexString(SHA256.HashData(repositoryB)),
                "repository-b"),
            CancellationToken.None).ConfigureAwait(false);

        var updated = await store.AddAsync(NewRepositoryInput(Guid.NewGuid(),
                "src/Widget.cs",
                changedA,
                Convert.ToHexString(SHA256.HashData(changedA)),
                "repository-a"),
            CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(updated.WasInserted);
        AssertEx.True(updated.WasUpdated);
        AssertEx.Equal(firstId, updated.DocumentId);
        AssertEx.Equal(expected: 2L, await CountDocumentsAsync(databasePath).ConfigureAwait(false));
        var storedA = AssertEx.NotNull(await store.ReadBytesAsync(firstId, CancellationToken.None).ConfigureAwait(false));
        var storedB = AssertEx.NotNull(await store.ReadBytesAsync(secondId, CancellationToken.None).ConfigureAwait(false));
        AssertEx.True(changedA.AsSpan().SequenceEqual(storedA));
        AssertEx.True(repositoryB.AsSpan().SequenceEqual(storedB));
    }

    [Test]
    public async Task AddAsync_WhenRepositoryPathBytesChange_UpdatesStableDocumentAndResetsItForReindex()
    {
        var databasePath = GetDatabasePath("repository-update.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);
        var original = Encoding.UTF8.GetBytes("original source");
        var changed = Encoding.UTF8.GetBytes("changed source");

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);
        var documentId = Guid.NewGuid();
        _ = await store.AddAsync(NewRepositoryInput(documentId,
                "src/Widget.cs",
                original,
                Convert.ToHexString(SHA256.HashData(original))),
            CancellationToken.None).ConfigureAwait(false);
        await SetStatusAsync(databasePath, "Indexed").ConfigureAwait(false);

        var result = await store.AddAsync(NewRepositoryInput(Guid.NewGuid(),
                "src/Widget.cs",
                changed,
                Convert.ToHexString(SHA256.HashData(changed))),
            CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(result.WasInserted);
        AssertEx.True(result.WasUpdated);
        AssertEx.Equal(documentId, result.DocumentId);
        AssertEx.Equal(expected: 1L, await CountDocumentsAsync(databasePath).ConfigureAwait(false));
        AssertEx.Equal("Pending", await GetStatusAsync(databasePath).ConfigureAwait(false));
        var stored = AssertEx.NotNull(await store.ReadBytesAsync(documentId, CancellationToken.None).ConfigureAwait(false));
        AssertEx.True(changed.AsSpan().SequenceEqual(stored));
    }

    [Test]
    public async Task AddAsync_RepositoryIdentity_NormalizesDirectorySeparators()
    {
        var databasePath = GetDatabasePath("repository-normalized-path.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);
        var content = Encoding.UTF8.GetBytes("stable source");
        var contentHash = Convert.ToHexString(SHA256.HashData(content));

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);
        var first = await store.AddAsync(NewRepositoryInput(Guid.NewGuid(), @"src\Widget.cs", content, contentHash), CancellationToken.None)
                               .ConfigureAwait(false);
        var second = await store.AddAsync(NewRepositoryInput(Guid.NewGuid(), "src/Widget.cs", content, contentHash), CancellationToken.None)
                                .ConfigureAwait(false);

        AssertEx.True(first.WasInserted);
        AssertEx.False(second.WasInserted);
        AssertEx.False(second.WasUpdated);
        AssertEx.Equal(first.DocumentId, second.DocumentId);
        AssertEx.Equal(expected: 1L, await CountDocumentsAsync(databasePath).ConfigureAwait(false));
    }

    [Test]
    public async Task MigrationDown_WhenCurrentRowsShareContentHash_FailsBeforeChangingSchemaOrData()
    {
        const string previousMigration = "20260811161453_AddModelLaunchArguments";
        var databasePath = GetDatabasePath("repository-down-guard.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);
        var content = Encoding.UTF8.GetBytes("same bytes with two provenances");
        var contentHash = Convert.ToHexString(SHA256.HashData(content));

        await using (var provider = BuildProvider(databasePath))
        {
            var store = CreateStore(provider);
            _ = await store.AddAsync(NewRepositoryInput(Guid.NewGuid(), "a.txt", content, contentHash), CancellationToken.None)
                           .ConfigureAwait(false);
            _ = await store.AddAsync(NewRepositoryInput(Guid.NewGuid(), "b.txt", content, contentHash), CancellationToken.None)
                           .ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            _ = await AssertEx.ThrowsAsync<SqliteException>(() =>
                    context.Database.GetService<IMigrator>().MigrateAsync(previousMigration))
                .ConfigureAwait(false);
        }

        AssertEx.Equal(expected: 2L, await CountDocumentsAsync(databasePath).ConfigureAwait(false));
        await using var verificationContext = CreateContext(databasePath);
        var applied = await verificationContext.Database.GetAppliedMigrationsAsync().ConfigureAwait(false);
        AssertEx.True(applied.Contains("20260813121930_AddKnowledgeCollectionsAndProvenance", StringComparer.Ordinal));
    }

    // Each test database carries exactly one knowledge_documents row, so these read/write the status column without an
    // id filter — avoiding any dependency on the store's on-wire document_id string encoding.
    private static async Task SetStatusAsync(string databasePath, string status)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE knowledge_documents SET status = $status;";
        _ = command.Parameters.AddWithValue("$status", status);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<string> GetStatusAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM knowledge_documents LIMIT 1;";
        return (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task<long> CountDocumentsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM knowledge_documents;";
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
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

    private static KnowledgeDocumentInput NewRepositoryInput(Guid documentId,
        string sourcePath,
        byte[] content,
        string contentHash,
        string sourceId = "repository-a")
    {
        return new KnowledgeDocumentInput(documentId,
            sourcePath,
            "text/plain",
            Path.GetExtension(sourcePath),
            content.Length,
            contentHash,
            content,
            "nomic-embed-text",
            "REPOSITORY",
            sourcePath,
            "repository",
            sourceId);
    }

    private KnowledgeDocumentBlobStore CreateStore(ServiceProvider provider)
    {
        return new KnowledgeDocumentBlobStore(provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedNodeDataDirectory(Path.Combine(_rootPath, "data")),
            _keyHolder,
            TimeProvider.System);
    }

    // A single shared EF internal service provider for every context this suite builds. Pinning it with
    // UseInternalServiceProvider keeps the suite's contribution to EF's process-wide provider cache at exactly ONE,
    // instead of risking the twenty-provider ManyServiceProvidersCreatedWarning cap (configured as an error solution-
    // wide, and tripped by a later test when the cumulative count overflows). This is the fixture-aligned alternative to
    // AgentDefinitionTestContextFactory's shared-interceptor trick, and needs no encryption interceptors — this store
    // encrypts blob bytes itself and the filename via the context helper, never through an EF interceptor.
    private static readonly IServiceProvider SharedEfServiceProvider =
        new ServiceCollection().AddEntityFrameworkSqlite().BuildServiceProvider();

    private ServiceProvider BuildProvider(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateContextWithForeignKeysOff(databasePath));
        return services.BuildServiceProvider();
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .UseInternalServiceProvider(SharedEfServiceProvider)
                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .Options;

        return new NodeChatDbContext(options, _keyHolder);
    }

    // Microsoft.Data.Sqlite enables foreign-key enforcement by default; the node-sqlite runtime connection does not,
    // so every KB test connection is aligned to that runtime mode even where this store's inserts have no FK to trip.
    private NodeChatDbContext CreateContextWithForeignKeysOff(string databasePath)
    {
        var context = CreateContext(databasePath);
        var connection = context.Database.GetDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF;";
        _ = command.ExecuteNonQuery();
        return context;
    }

    private async Task MigrateAsync(string databasePath)
    {
        await using var context = CreateContext(databasePath);
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
