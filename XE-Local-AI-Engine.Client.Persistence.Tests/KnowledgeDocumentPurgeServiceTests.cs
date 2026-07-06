namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     C1 delete-cascade guarantee, proven on the real e_sqlite3 runtime connection with foreign-key enforcement OFF (the
///     same mode the app runs). Because <c>ON DELETE CASCADE</c> never fires without FK enforcement, the purge service must
///     itself delete every dependent row in child-to-parent order inside one transaction — and the chunk delete must fire
///     the FTS delete trigger so purged content is no longer searchable. These tests seed a full document graph with real
///     rows (which fires the FTS insert trigger), run the purge, then assert every table AND the FTS index are empty. This
///     deliberately uses the FK-off runtime connection so an EF-tracked cascade cannot produce a false pass.
/// </summary>
public sealed class KnowledgeDocumentPurgeServiceTests : IDisposable
{
    private const string SearchableToken = "zebrahorse";

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

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentGraphAsync(databasePath, documentId).ConfigureAwait(false);

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
            var purge = new KnowledgeDocumentPurgeService(context, Substitute.For<IKnowledgeDocumentBlobStore>());
            var purged = await purge.PurgeAsync(documentId, CancellationToken.None).ConfigureAwait(false);
            AssertEx.True(purged, "Purge should report success for an existing document.");
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_chunk_vectors;").ConfigureAwait(false));
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_document_chunks;").ConfigureAwait(false));
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_document_sections;").ConfigureAwait(false));
        AssertEx.Equal(expected: 0L, await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_documents;").ConfigureAwait(false));
    }

    [Test]
    public async Task PurgeAsync_WhenDocumentIsPurged_LeavesNoSearchableContentInTheFtsIndex()
    {
        var databasePath = GetDatabasePath("purge-fts.sqlite");
        var documentId = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentGraphAsync(databasePath, documentId).ConfigureAwait(false);

        await using (var before = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            AssertEx.True(await CountAsync(before, $"SELECT COUNT(*) FROM chunk_fts WHERE chunk_fts MATCH '{SearchableToken}';").ConfigureAwait(false) > 0,
                "The seeded chunk content should be searchable before purge (the FTS insert trigger fired).");
        }

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
            var purge = new KnowledgeDocumentPurgeService(context, Substitute.For<IKnowledgeDocumentBlobStore>());
            _ = await purge.PurgeAsync(documentId, CancellationToken.None).ConfigureAwait(false);
        }

        await using var after = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        AssertEx.Equal(expected: 0L,
            await CountAsync(after, $"SELECT COUNT(*) FROM chunk_fts WHERE chunk_fts MATCH '{SearchableToken}';").ConfigureAwait(false));
    }

    [Test]
    public async Task PurgeAsync_WhenDocumentDoesNotExist_ReturnsFalse()
    {
        var databasePath = GetDatabasePath("purge-missing.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
        var purge = new KnowledgeDocumentPurgeService(context, Substitute.For<IKnowledgeDocumentBlobStore>());

        var purged = await purge.PurgeAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(purged, "Purging a non-existent document should return false so the endpoint maps it to a 404.");
    }

    private async Task MigrateAsync(string databasePath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private static async Task SeedDocumentGraphAsync(string databasePath, Guid documentId)
    {
        var sectionId = Guid.NewGuid();
        var firstChunkId = Guid.NewGuid();
        var secondChunkId = Guid.NewGuid();

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

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
            ("$path", documentId.ToString("D") + ".txt")).ConfigureAwait(false);

        await ExecuteAsync(connection,
            "INSERT INTO knowledge_document_sections (section_id, document_id, ordinal) VALUES ($sid, $did, 0);",
            ("$sid", sectionId),
            ("$did", documentId)).ConfigureAwait(false);

        // Inserting chunks fires the FTS insert trigger (knowledge_document_chunks_ai) so chunk_fts is populated.
        await ExecuteAsync(connection,
            "INSERT INTO knowledge_document_chunks (chunk_id, document_id, section_id, chunk_index, content, token_count) VALUES ($cid, $did, $sid, 0, $content, 3);",
            ("$cid", firstChunkId),
            ("$did", documentId),
            ("$sid", sectionId),
            ("$content", $"the {SearchableToken} runs fast")).ConfigureAwait(false);

        await ExecuteAsync(connection,
            "INSERT INTO knowledge_document_chunks (chunk_id, document_id, section_id, chunk_index, content, token_count) VALUES ($cid, $did, $sid, 1, $content, 2);",
            ("$cid", secondChunkId),
            ("$did", documentId),
            ("$sid", sectionId),
            ("$content", $"another {SearchableToken}")).ConfigureAwait(false);

        await InsertVectorAsync(connection, firstChunkId, documentId).ConfigureAwait(false);
        await InsertVectorAsync(connection, secondChunkId, documentId).ConfigureAwait(false);
    }

    private static async Task InsertVectorAsync(SqliteConnection connection, Guid chunkId, Guid documentId)
    {
        await ExecuteAsync(connection,
            "INSERT INTO knowledge_chunk_vectors (chunk_id, document_id, dim, embedding, embedding_model) VALUES ($cid, $did, 4, $blob, 'nomic-embed-text');",
            ("$cid", chunkId),
            ("$did", documentId),
            ("$blob", new byte[16])).ConfigureAwait(false);
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

        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // SQL text is a fixed internal test literal, never user input.
        command.CommandText = sql;
#pragma warning restore CA2100
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await EnsureForeignKeysOffAsync(connection).ConfigureAwait(false);
        return connection;
    }

    // Microsoft.Data.Sqlite enables foreign-key enforcement by default; the node-sqlite runtime connection does not
    // enable it (C1's explicit child-to-parent delete design assumes FK enforcement is off), so every connection this
    // suite touches must match that runtime mode rather than let FK cascade produce a false pass.
    private static async Task EnsureForeignKeysOffAsync(DbConnection connection)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF;";
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
