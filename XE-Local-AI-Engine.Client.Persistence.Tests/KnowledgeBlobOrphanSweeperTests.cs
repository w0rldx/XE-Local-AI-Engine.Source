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
        _ = sweeper.ReconcileInterruptedWrites();
        var reclaimed = await sweeper.SweepOnceAsync(CancellationToken.None);

        // The foreign name parses as neither "{documentId:D}{extension}" nor a sibling of one, so no pass can attribute
        // it to a document; the temp leftover is attributable but was just written, and the grace window spares it.
        AssertEx.Equal(expected: 0, reclaimed);
        AssertEx.True(File.Exists(foreignPath), "A file that is not named after a document must never be deleted.");
        AssertEx.True(File.Exists(strayTempPath), "A temp leftover younger than the grace window must survive: a write may still be running.");
    }

    [Test]
    public async Task ReconcileInterruptedWrites_WhenTheLiveBlobIsMissing_RestoresTheBackupInsteadOfDeletingIt()
    {
        // The dangerous window: a same-extension reindex renamed the live blob aside and the process died before the
        // transaction committed, so this backup is the ONLY copy of what the still-live row claims to own.
        await using var provider = BuildProvider(GetDatabasePath("reconcile-restore.sqlite"));
        var livePath = BlobPath(Guid.NewGuid(), ".txt");
        Directory.CreateDirectory(DocumentsDirectory());
        var backupPath = WriteLitter(livePath, ".backup", "the only surviving copy", AgedWriteTimeUtc());

        var reconciled = CreateStore(provider, FixedClock()).ReconcileInterruptedWrites();

        AssertEx.Equal(expected: 1, reconciled.RestoredBlobNames.Count);
        AssertEx.True(File.Exists(livePath), "A backup whose live blob is missing must be restored to the live path.");
        AssertEx.False(File.Exists(backupPath), "The restore is a move, so the backup name must not survive it.");
        AssertEx.Equal("the only surviving copy", await File.ReadAllTextAsync(livePath));
    }

    [Test]
    public async Task ReconcileInterruptedWrites_RemovesAnAgedSiblingOfALiveBlobAndSparesAFreshOne()
    {
        await using var provider = BuildProvider(GetDatabasePath("reconcile-litter.sqlite"));
        var livePath = BlobPath(Guid.NewGuid(), ".txt");
        Directory.CreateDirectory(DocumentsDirectory());
        await File.WriteAllTextAsync(livePath, "the live blob");
        var agedBackupPath = WriteLitter(livePath, ".backup", "superseded", AgedWriteTimeUtc());
        var agedTempPath = WriteLitter(livePath, ".tmp", "abandoned", AgedWriteTimeUtc());
        var freshTempPath = WriteLitter(livePath, ".tmp", "a write in flight", FreshWriteTimeUtc());

        var reconciled = CreateStore(provider, FixedClock()).ReconcileInterruptedWrites();

        AssertEx.Equal(expected: 2, reconciled.RemovedLitterCount);
        AssertEx.False(File.Exists(agedBackupPath), "A backup is superseded once its live blob is back, and past the grace window it is litter.");
        AssertEx.False(File.Exists(agedTempPath), "A temp sibling past the grace window belongs to a dead writer.");
        AssertEx.True(File.Exists(freshTempPath), "A temp sibling inside the grace window may still be a write in flight.");
        AssertEx.True(File.Exists(livePath), "The live blob itself is never a candidate.");
        AssertEx.Equal("the live blob", await File.ReadAllTextAsync(livePath));
    }

    [Test]
    public async Task ReconcileInterruptedWrites_DeletesAnAgedTempEvenWhenNoLiveBlobSitsBesideIt()
    {
        // A temp sibling is aged out rather than recovered: its bytes were never verified, and a live row whose blob a
        // crash left missing is repaired by AddAsync's dedupe-repair path on the next re-add, not by promoting this.
        await using var provider = BuildProvider(GetDatabasePath("reconcile-orphan-temp.sqlite"));
        var livePath = BlobPath(Guid.NewGuid(), ".txt");
        Directory.CreateDirectory(DocumentsDirectory());
        var agedTempPath = WriteLitter(livePath, ".tmp", "a half-written blob no row ever named", AgedWriteTimeUtc());

        var reconciled = CreateStore(provider, FixedClock()).ReconcileInterruptedWrites();

        AssertEx.Equal(expected: 0, reconciled.RestoredBlobNames.Count);
        AssertEx.Equal(expected: 1, reconciled.RemovedLitterCount);
        AssertEx.False(File.Exists(agedTempPath), "An aged temp sibling is litter whether or not a live blob sits beside it.");
        AssertEx.False(File.Exists(livePath), "A temp sibling must never be promoted to the live path: its bytes were never verified.");
    }

    [Test]
    public async Task UpdateRepositoryDocument_PublishesTheBlobBeforeItCommits_LeavingNoLitterBehind()
    {
        // The writer-side half of the invariant above, asserted through what a completed reindex leaves on disk: the
        // new bytes at the live path and nothing under a temp or backup suffix for the sweep to have to judge.
        var databasePath = GetDatabasePath("reindex-publishes-before-commit.sqlite");
        await MigrateAsync(databasePath);

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider, FixedClock());
        var documentId = await SeedRepositoryDocumentAsync(store, "the first revision");

        var reindexed = await AddRepositoryDocumentAsync(store, documentId, "the second revision");

        AssertEx.True(reindexed.WasUpdated, "Precondition: the same repository path with new content must reindex, not dedupe.");
        AssertEx.Equal("the second revision", Encoding.UTF8.GetString((await store.ReadBytesAsync(documentId, CancellationToken.None))!));
        AssertEx.Empty(Directory.GetFiles(DocumentsDirectory(), "*.tmp"));
        AssertEx.Empty(Directory.GetFiles(DocumentsDirectory(), "*.backup"));
    }

    [Test]
    public async Task ReconcileInterruptedWrites_LeavesALiveDocumentWithNoLitterUntouched()
    {
        var databasePath = GetDatabasePath("reconcile-no-litter.sqlite");
        await MigrateAsync(databasePath);

        await using var provider = BuildProvider(databasePath);
        var store = CreateStore(provider, FixedClock());
        var liveDocumentId = await SeedDocumentAsync(store, "live document bytes");
        var livePath = BlobPath(liveDocumentId, ".txt");
        File.SetLastWriteTimeUtc(livePath, AgedWriteTimeUtc());

        using var sweeper = CreateSweeper(provider, store);
        var reconciled = sweeper.ReconcileInterruptedWrites();
        var reclaimed = await sweeper.SweepOnceAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, reconciled.RestoredBlobNames.Count);
        AssertEx.Equal(expected: 0, reconciled.RemovedLitterCount);
        AssertEx.Equal(expected: 0, reclaimed);
        AssertEx.True(File.Exists(livePath), "A live document's own blob is never litter, however old it is.");
    }

    // The interrupted-write siblings this store writes: "{blobPath}.{guid:N}{suffix}", stamped to a chosen instant so
    // the grace window is decided by the fixed clock rather than by how long the test took.
    private static string WriteLitter(string liveBlobPath, string suffix, string content, DateTime lastWriteUtc)
    {
        var path = string.Concat(liveBlobPath, ".", Guid.NewGuid().ToString("N"), suffix);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static DateTime AgedWriteTimeUtc() =>
        FixedNowUtc.AddHours(-1).UtcDateTime;

    private static DateTime FreshWriteTimeUtc() =>
        FixedNowUtc.AddMinutes(-1).UtcDateTime;

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
        var result = await store.AddAsync(new KnowledgeDocumentInput
            {
                DocumentId = documentId,
                OriginalFileName = "notes.txt",
                MimeType = "text/plain",
                Extension = ".txt",
                SizeBytes = content.Length,
                ContentHash = Convert.ToHexString(SHA256.HashData(content)),
                Content = content,
                EmbeddingModel = "nomic-embed-text"
            },
            CancellationToken.None);

        AssertEx.True(result.WasInserted, "Precondition: the control document must be inserted with its blob.");
        return documentId;
    }

    // A repository-sourced document, the only identity whose re-add reindexes in place rather than deduping.
    private static async Task<Guid> SeedRepositoryDocumentAsync(KnowledgeDocumentBlobStore store, string text)
    {
        var documentId = Guid.NewGuid();
        var result = await AddRepositoryDocumentAsync(store, documentId, text);
        AssertEx.True(result.WasInserted, "Precondition: the repository document must be inserted with its blob.");
        return documentId;
    }

    private static async Task<KnowledgeDocumentAddResult> AddRepositoryDocumentAsync(KnowledgeDocumentBlobStore store, Guid documentId, string text)
    {
        var content = Encoding.UTF8.GetBytes(text);
        return await store.AddAsync(new KnowledgeDocumentInput
            {
                DocumentId = documentId,
                OriginalFileName = "README.md",
                MimeType = "text/markdown",
                Extension = ".md",
                SizeBytes = content.Length,
                ContentHash = Convert.ToHexString(SHA256.HashData(content)),
                Content = content,
                EmbeddingModel = "nomic-embed-text",
                SourceKind = "repository",
                SourceId = "repo-under-test",
                SourcePath = "docs/README.md"
            },
            CancellationToken.None);
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

    private KnowledgeDocumentBlobStore CreateStore(ServiceProvider provider, TimeProvider? timeProvider = null)
    {
        return new KnowledgeDocumentBlobStore(provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedNodeDataDirectory(DataDirectory()),
            _keyHolder,
            timeProvider ?? TimeProvider.System);
    }

    // The interrupted-write grace window is measured against the store's clock, so pinning it makes "aged" and "fresh"
    // a property of the file stamps below rather than of how long the test took to reach the assertion.
    private static readonly DateTimeOffset FixedNowUtc = new(year: 2026, month: 9, day: 21, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    private static TimeProvider FixedClock() =>
        new FixedTimeProvider(FixedNowUtc);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
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
        services.AddScoped(_ => CreateContext(databasePath));
        return services.BuildServiceProvider();
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .UseInternalServiceProvider(SharedEfServiceProvider)
                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .Options;

        return new NodeChatDbContext(options, _keyHolder);
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

        public KnowledgeBlobReconciliationResult ReconcileInterruptedWrites() =>
            _inner.ReconcileInterruptedWrites();

        public Task DeleteAllBytesAsync(Guid documentId, CancellationToken cancellationToken)
        {
            return documentId == _failingDocumentId
                ? throw new IOException("The file is in use by another process.")
                : _inner.DeleteAllBytesAsync(documentId, cancellationToken);
        }
    }
}
