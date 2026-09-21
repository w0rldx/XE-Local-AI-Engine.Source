namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     The delete guarantee, proven on the real e_sqlite3 runtime connection under the foreign-key enforcement the node
///     really runs (it does not run with enforcement off, whatever this suite used to claim). The purge service deletes
///     every dependent row in child-to-parent order inside one transaction, and the chunk delete fires the FTS delete
///     trigger so purged content is no longer searchable. Each test seeds a full document graph with real rows (which
///     fires the FTS insert trigger) plus a second document as a control, runs the purge, then asserts the unfiltered
///     table and FTS totals — so a delete that reaches too far fails as loudly as one that stops short.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class KnowledgeDocumentPurgeServiceTests : IDisposable
{
    private const string SearchableToken = "zebrahorse";
    private const string ControlToken = "okapimule";

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
    public async Task PurgeAsync_WhenDocumentHasSectionsChunksAndVectors_RemovesEveryDependentRow()
    {
        var databasePath = GetDatabasePath("purge-rows.sqlite");
        var documentId = Guid.NewGuid();
        var controlDocumentId = Guid.NewGuid();

        await MigrateAsync(databasePath);
        await SeedDocumentGraphAsync(databasePath, documentId);
        await SeedDocumentGraphAsync(databasePath, controlDocumentId, ControlToken);

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            var purge = new KnowledgeDocumentPurgeService(context, Substitute.For<IKnowledgeDocumentBlobStore>(), NullLogger<KnowledgeDocumentPurgeService>.Instance);
            var purged = await purge.PurgeAsync(documentId, CancellationToken.None);
            AssertEx.True(purged, "Purge should report success for an existing document.");
        }

        await using var connection = await OpenConnectionAsync(databasePath);
        AssertEx.Equal(expected: 0L,
            await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_document_chunks WHERE document_id = $document_id;", ("$document_id", documentId)));
        // Unfiltered totals: exactly the control document's graph survives, so a delete that reached too far fails here.
        AssertEx.Equal(expected: 2L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_chunk_vectors;"));
        AssertEx.Equal(expected: 2L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_document_chunks;"));
        AssertEx.Equal(expected: 1L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_document_sections;"));
        AssertEx.Equal(expected: 1L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_documents;"));
    }

    [Test]
    public async Task PurgeAsync_WhenDocumentIsPurged_LeavesNoSearchableContentInTheFtsIndex()
    {
        var databasePath = GetDatabasePath("purge-fts.sqlite");
        var documentId = Guid.NewGuid();
        var controlDocumentId = Guid.NewGuid();

        await MigrateAsync(databasePath);
        await SeedDocumentGraphAsync(databasePath, documentId);
        await SeedDocumentGraphAsync(databasePath, controlDocumentId, ControlToken);

        await using (var before = await OpenConnectionAsync(databasePath))
        {
            AssertEx.True(await CountAsync(before, $"SELECT COUNT(*) FROM chunk_fts WHERE chunk_fts MATCH '{SearchableToken}';") > 0,
                "The seeded chunk content should be searchable before purge (the FTS insert trigger fired).");
        }

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            var purge = new KnowledgeDocumentPurgeService(context, Substitute.For<IKnowledgeDocumentBlobStore>(), NullLogger<KnowledgeDocumentPurgeService>.Instance);
            _ = await purge.PurgeAsync(documentId, CancellationToken.None);
        }

        await using var after = await OpenConnectionAsync(databasePath);
        AssertEx.Equal(expected: 0L,
            await CountAsync(after, $"SELECT COUNT(*) FROM chunk_fts WHERE chunk_fts MATCH '{SearchableToken}';"));
        AssertEx.Equal(expected: 2L,
            await CountAsync(after, $"SELECT COUNT(*) FROM chunk_fts WHERE chunk_fts MATCH '{ControlToken}';"),
            "The control document's chunks must stay searchable — the purge empties one document's index entries, not the index.");
    }

    /// <summary>
    ///     Measured on the real migrated schema, not assumed: SQLite runs the chunks' AFTER DELETE trigger for rows an
    ///     <c>ON DELETE CASCADE</c> removed, so the FTS index follows a document delete that never names a chunk.
    /// </summary>
    [Test]
    public async Task DeletingTheDocumentRow_CascadesToChunks_AndFiresTheFtsDeleteTrigger()
    {
        var databasePath = GetDatabasePath("cascade-fts.sqlite");
        var documentId = Guid.NewGuid();
        var controlDocumentId = Guid.NewGuid();

        await MigrateAsync(databasePath);
        await SeedDocumentGraphAsync(databasePath, documentId);
        await SeedDocumentGraphAsync(databasePath, controlDocumentId, ControlToken);

        await using var connection = await OpenConnectionAsync(databasePath);
        AssertEx.Equal(expected: 1L, await CountAsync(connection, "PRAGMA foreign_keys;"),
            "Precondition: without enforcement the cascade never runs and this test proves nothing.");

        // Only the parent row is deleted — the chunks go solely by cascade.
        await ExecuteAsync(connection, "DELETE FROM knowledge_documents WHERE document_id = $document_id;", ("$document_id", documentId));

        AssertEx.Equal(expected: 2L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_document_chunks;"));
        AssertEx.Equal(expected: 0L,
            await CountAsync(connection, $"SELECT COUNT(*) FROM chunk_fts WHERE chunk_fts MATCH '{SearchableToken}';"),
            "The cascade-removed chunks must leave the FTS index too, or a purge could strand searchable content.");
        AssertEx.Equal(expected: 2L,
            await CountAsync(connection, $"SELECT COUNT(*) FROM chunk_fts WHERE chunk_fts MATCH '{ControlToken}';"));
    }

    [Test]
    public async Task PurgeAsync_WhenDocumentDoesNotExist_ReturnsFalse()
    {
        var databasePath = GetDatabasePath("purge-missing.sqlite");
        await MigrateAsync(databasePath);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var purge = new KnowledgeDocumentPurgeService(context, Substitute.For<IKnowledgeDocumentBlobStore>(), NullLogger<KnowledgeDocumentPurgeService>.Instance);

        var purged = await purge.PurgeAsync(Guid.NewGuid(), CancellationToken.None);

        AssertEx.False(purged, "Purging a non-existent document should return false so the endpoint maps it to a 404.");
    }

    [Test]
    public async Task PurgeAsync_WhenBlobDeleteThrows_StillReportsSuccessAndCommitsTheRowDeletes()
    {
        // The row deletes are already committed when the blob delete runs, so the delete HAS happened: a failure there
        // must not be turned into a 500 for the client. Only IOException/UnauthorizedAccessException are swallowed
        // inside the store, so an exception of any other type reaching PurgeAsync is the regression this locks down.
        var databasePath = GetDatabasePath("purge-blob-failure.sqlite");
        var documentId = Guid.NewGuid();
        var controlDocumentId = Guid.NewGuid();

        await MigrateAsync(databasePath);
        await SeedDocumentGraphAsync(databasePath, documentId);
        await SeedDocumentGraphAsync(databasePath, controlDocumentId, ControlToken);

        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        blobStore.When(store => store.DeleteBytesAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
                 .Do(_ => throw new NotSupportedException("The blob path could not be deleted."));

        bool purged;
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            var purge = new KnowledgeDocumentPurgeService(context, blobStore, NullLogger<KnowledgeDocumentPurgeService>.Instance);
            purged = await purge.PurgeAsync(documentId, CancellationToken.None);
        }

        AssertEx.True(purged, "A failed blob delete must not turn a committed row delete into a failed purge.");

        await using var connection = await OpenConnectionAsync(databasePath);
        AssertEx.Equal(expected: 0L,
            await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_documents WHERE document_id = $document_id;", ("$document_id", documentId)));
        AssertEx.Equal(expected: 1L,
            await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_documents WHERE document_id = $document_id;", ("$document_id", controlDocumentId)));
        // Unfiltered totals plus the untouched control document: the purge deleted exactly one document's graph.
        AssertEx.Equal(expected: 1L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_documents;"));
        AssertEx.Equal(expected: 2L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_document_chunks;"));
        AssertEx.Equal(expected: 2L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_chunk_vectors;"));
        AssertEx.Equal(expected: 1L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_document_sections;"));
    }

    [Test]
    public async Task PurgeAsync_WhenBlobDeleteIsCancelled_PropagatesTheCancellation()
    {
        // Cancellation is not a blob failure: it must still surface, exactly as it does from every other await in the
        // method. The rows stay deleted because they committed before the blob delete was ever attempted.
        var databasePath = GetDatabasePath("purge-blob-cancelled.sqlite");
        var documentId = Guid.NewGuid();

        await MigrateAsync(databasePath);
        await SeedDocumentGraphAsync(databasePath, documentId);

        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        blobStore.When(store => store.DeleteBytesAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
                 .Do(_ => throw new OperationCanceledException());

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            var purge = new KnowledgeDocumentPurgeService(context, blobStore, NullLogger<KnowledgeDocumentPurgeService>.Instance);
            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => purge.PurgeAsync(documentId, CancellationToken.None));
        }

        await using var connection = await OpenConnectionAsync(databasePath);
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_documents;"));
    }

    // A copy of the shared at-head template, not a replay of the whole declared chain: this suite exercises a service
    // over the schema, never the migrator that produced it. See MigratedDatabaseTemplate.
    private static async Task MigrateAsync(string databasePath)
    {
        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);
    }

    private static async Task SeedDocumentGraphAsync(string databasePath, Guid documentId, string token = SearchableToken)
    {
        var sectionId = Guid.NewGuid();
        var firstChunkId = Guid.NewGuid();
        var secondChunkId = Guid.NewGuid();

        await using var connection = await OpenConnectionAsync(databasePath);

        await ExecuteAsync(connection,
            """
            INSERT INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status, chunk_count, embedding_model, created_at_utc, updated_at_utc)
            VALUES ($id, $name, 'text/plain', '.txt', 10, $hash, $path, 'Indexed', 2, 'nomic-embed-text', 1, 1);
            """,
            ("$id", documentId),
            ("$name", new byte[]
            {
                1,
                2,
                3
            }),
            ("$hash", "hash-" + documentId.ToString("N")),
            ("$path", documentId.ToString("D") + ".txt"));

        await ExecuteAsync(connection,
            "INSERT INTO knowledge_document_sections (section_id, document_id, ordinal) VALUES ($sid, $did, 0);",
            ("$sid", sectionId),
            ("$did", documentId));

        // Inserting chunks fires the FTS insert trigger (knowledge_document_chunks_ai) so chunk_fts is populated.
        await ExecuteAsync(connection,
            "INSERT INTO knowledge_document_chunks (chunk_id, document_id, section_id, chunk_index, content, token_count) VALUES ($cid, $did, $sid, 0, $content, 3);",
            ("$cid", firstChunkId),
            ("$did", documentId),
            ("$sid", sectionId),
            ("$content", $"the {token} runs fast"));

        await ExecuteAsync(connection,
            "INSERT INTO knowledge_document_chunks (chunk_id, document_id, section_id, chunk_index, content, token_count) VALUES ($cid, $did, $sid, 1, $content, 2);",
            ("$cid", secondChunkId),
            ("$did", documentId),
            ("$sid", sectionId),
            ("$content", $"another {token}"));

        await InsertVectorAsync(connection, firstChunkId, documentId);
        await InsertVectorAsync(connection, secondChunkId, documentId);
    }

    private static async Task InsertVectorAsync(SqliteConnection connection, Guid chunkId, Guid documentId)
    {
        await ExecuteAsync(connection,
            "INSERT INTO knowledge_chunk_vectors (chunk_id, document_id, dim, embedding, embedding_model) VALUES ($cid, $did, 4, $blob, 'nomic-embed-text');",
            ("$cid", chunkId),
            ("$did", documentId),
            ("$blob", new byte[16]));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // SQL text is a fixed internal test literal, never user input.
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // SQL text is a fixed internal test literal, never user input.
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        return connection;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
