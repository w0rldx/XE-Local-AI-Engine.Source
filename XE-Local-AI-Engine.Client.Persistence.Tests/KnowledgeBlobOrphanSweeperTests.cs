namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The startup sweep that reclaims knowledge document blobs whose <c>knowledge_documents</c> row is gone — the state a
///     purge leaves behind when its post-commit file delete fails, and which no later purge can collect (the second purge
///     finds no row and returns before touching the disk). Run on the real store over a real migrated SQLite database and
///     a real filesystem: the control document, seeded through the store itself, is what proves the sweep deletes only
///     what nothing owns.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class KnowledgeBlobOrphanSweeperTests : IDisposable
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
    public async Task SweepOnceAsync_RemovesTheBlobWithNoRowAndKeepsTheOneThatHasOne()
    {
        var databasePath = GetDatabasePath("orphan-sweep.sqlite");
        await MigrateAsync(databasePath);

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);

        // Control: a live document with a committed row AND its blob, written by the real store.
        var liveDocumentId = await SeedDocumentAsync(store, "live document bytes");
        var livePath = BlobPath(liveDocumentId, ".txt");

        // Orphan: exactly what a purge leaves when the row delete commits but the file delete does not.
        var orphanPath = await WriteStrayBlobAsync(Guid.NewGuid(), ".txt");

        using var sweeper = CreateSweeper(provider, store);
        var reclaimed = await sweeper.SweepOnceAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, reclaimed);
        AssertEx.False(File.Exists(orphanPath), "A blob whose document row is gone must be reclaimed.");
        AssertEx.True(File.Exists(livePath), "A blob whose document row still exists must survive the sweep.");
    }

    [Test]
    public async Task SweepOnceAsync_LeavesFilesWhoseNameIsNotADocumentId()
    {
        var databasePath = GetDatabasePath("orphan-foreign.sqlite");
        await MigrateAsync(databasePath);

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);

        Directory.CreateDirectory(DocumentsDirectory());
        var foreignPath = Path.Combine(DocumentsDirectory(), "notes.txt");
        await File.WriteAllTextAsync(foreignPath, "not ours");
        var strayTempPath = Path.Combine(DocumentsDirectory(), string.Concat(Guid.NewGuid().ToString("D"), ".txt.", Guid.NewGuid().ToString("N"), ".tmp"));
        await File.WriteAllTextAsync(strayTempPath, "interrupted write");

        using var sweeper = CreateSweeper(provider, store);
        var reclaimed = await sweeper.SweepOnceAsync(CancellationToken.None);

        // Neither name parses as "{documentId:D}{extension}", so neither is ever a candidate: the sweep deletes only
        // files it can attribute to a document, and a temp/backup sibling is reclaimed with its document or not at all.
        AssertEx.Equal(expected: 0, reclaimed);
        AssertEx.True(File.Exists(foreignPath), "A file that is not named after a document must never be deleted.");
        AssertEx.True(File.Exists(strayTempPath), "An unattributable temp leftover must not be mistaken for an orphan blob.");
    }

    [Test]
    public async Task SweepOnceAsync_WhenTheDocumentsDirectoryDoesNotExist_IsANoOp()
    {
        var databasePath = GetDatabasePath("orphan-no-directory.sqlite");
        await MigrateAsync(databasePath);

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);

        AssertEx.False(Directory.Exists(DocumentsDirectory()), "Precondition: nothing has written a knowledge blob yet.");

        using var sweeper = CreateSweeper(provider, store);
        AssertEx.Equal(expected: 0, await sweeper.SweepOnceAsync(CancellationToken.None));
    }

    [Test]
    public async Task SweepOnceAsync_WhenOneDeleteFails_StillReclaimsTheRemainingOrphans()
    {
        var databasePath = GetDatabasePath("orphan-delete-failure.sqlite");
        await MigrateAsync(databasePath);

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider);

        var failingDocumentId = Guid.NewGuid();
        var failingPath = await WriteStrayBlobAsync(failingDocumentId, ".txt");
        var secondOrphanPath = await WriteStrayBlobAsync(Guid.NewGuid(), ".md");

        // The real store wrapped so exactly one candidate's delete throws, standing in for a file the OS will not let go.
        using var sweeper = CreateSweeper(provider, new ThrowingDeleteBlobStore(store, failingDocumentId));
        var reclaimed = await sweeper.SweepOnceAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, reclaimed);
        AssertEx.True(File.Exists(failingPath), "The file whose delete threw is left for the next sweep.");
        AssertEx.False(File.Exists(secondOrphanPath), "A failure on one candidate must not stop the sweep reclaiming the rest.");
    }

    // Writes one document through the real store, so the row is committed by the same code path production uses.
    private static async Task<Guid> SeedDocumentAsync(KnowledgeDocumentBlobStore store, string text)
    {
        var documentId = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes(text);
        var result = await store.AddAsync(new KnowledgeDocumentInput(documentId,
                "notes.txt",
                "text/plain",
                ".txt",
                content.Length,
                Convert.ToHexString(SHA256.HashData(content)),
                content,
                "nomic-embed-text"),
            CancellationToken.None);

        AssertEx.True(result.WasInserted, "Precondition: the control document must be inserted with its blob.");
        return documentId;
    }

    // Puts a file where a purged document's blob would sit: on disk, with no knowledge_documents row behind it.
    private async Task<string> WriteStrayBlobAsync(Guid documentId, string extension)
    {
        Directory.CreateDirectory(DocumentsDirectory());
        var path = BlobPath(documentId, extension);
        await File.WriteAllTextAsync(path, "encrypted bytes nothing can read any more");
        return path;
    }

    private static KnowledgeBlobOrphanSweeper CreateSweeper(ServiceProvider provider, IKnowledgeDocumentBlobStore blobStore)
    {
        return new KnowledgeBlobOrphanSweeper(provider.GetRequiredService<IServiceScopeFactory>(),
            blobStore,
            NullLogger<KnowledgeBlobOrphanSweeper>.Instance);
    }

    private KnowledgeDocumentBlobStore CreateStore(ServiceProvider provider)
    {
        return new KnowledgeDocumentBlobStore(provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedNodeDataDirectory(DataDirectory()),
            _keyHolder,
            TimeProvider.System);
    }

    private string DataDirectory()
    {
        return Path.Combine(_rootPath, "data");
    }

    private string DocumentsDirectory()
    {
        return Path.Combine(DataDirectory(), "knowledge-base", "documents");
    }

    private string BlobPath(Guid documentId, string extension)
    {
        return Path.Combine(DocumentsDirectory(), string.Concat(documentId.ToString("D"), extension));
    }

    // One shared EF internal service provider for the whole suite, for the reason KnowledgeDocumentBlobStoreDedupeTests
    // documents: it keeps this suite's contribution to EF's process-wide provider cache at exactly one.
    private static readonly IServiceProvider SharedEfServiceProvider =
        new ServiceCollection().AddEntityFrameworkSqlite().BuildServiceProvider();

    private ServiceProvider BuildProvider(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateContextWithForeignKeysOff(databasePath));
        return services.BuildServiceProvider();
    }

    // Microsoft.Data.Sqlite enables foreign-key enforcement by default; the node-sqlite runtime connection does not, so
    // every knowledge-base test connection is aligned to that runtime mode.
    private NodeChatDbContext CreateContextWithForeignKeysOff(string databasePath)
    {
        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .UseInternalServiceProvider(SharedEfServiceProvider)
                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .Options;

        var context = new NodeChatDbContext(options, _keyHolder);
        var connection = context.Database.GetDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF;";
        _ = command.ExecuteNonQuery();
        return context;
    }

    // A copy of the shared at-head template, not a replay of the whole declared chain: this suite exercises the sweep
    // over the schema, never the migrator that produced it. See MigratedDatabaseTemplate.
    private static async Task MigrateAsync(string databasePath)
    {
        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private sealed class FixedNodeDataDirectory : INodeDataDirectory
    {
        public FixedNodeDataDirectory(string root)
        {
            Root = root;
        }

        public string Root { get; }
    }

    // Wraps the real store and fails the delete of one nominated document, reproducing a file the OS refuses to remove
    // without faking the enumeration or the row probe around it.
    private sealed class ThrowingDeleteBlobStore : IKnowledgeDocumentBlobStore
    {
        private readonly IKnowledgeDocumentBlobStore _inner;
        private readonly Guid _failingDocumentId;

        public ThrowingDeleteBlobStore(IKnowledgeDocumentBlobStore inner, Guid failingDocumentId)
        {
            _inner = inner;
            _failingDocumentId = failingDocumentId;
        }

        public Task<KnowledgeDocumentAddResult> AddAsync(KnowledgeDocumentInput input, CancellationToken cancellationToken) =>
            _inner.AddAsync(input, cancellationToken);

        public Task<byte[]?> ReadBytesAsync(Guid documentId, CancellationToken cancellationToken) =>
            _inner.ReadBytesAsync(documentId, cancellationToken);

        public Task DeleteBytesAsync(Guid documentId, string extension, CancellationToken cancellationToken) =>
            _inner.DeleteBytesAsync(documentId, extension, cancellationToken);

        public IReadOnlyList<Guid> ListStoredDocumentIds() =>
            _inner.ListStoredDocumentIds();

        public Task DeleteAllBytesAsync(Guid documentId, CancellationToken cancellationToken)
        {
            return documentId == _failingDocumentId
                ? throw new IOException("The file is in use by another process.")
                : _inner.DeleteAllBytesAsync(documentId, cancellationToken);
        }
    }
}
