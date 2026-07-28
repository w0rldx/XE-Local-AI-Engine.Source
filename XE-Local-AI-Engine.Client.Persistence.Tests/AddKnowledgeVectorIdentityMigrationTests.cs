namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;

public sealed class AddKnowledgeVectorIdentityMigrationTests : IDisposable
{
    private const string PreviousMigrationId = "20260726192021_AddLaunchPolicyFingerprintAndBenchmarkResources";
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
    public async Task MigrateAsync_ExistingKnowledgeProjection_IsExplicitlyLegacyAndSourceIsPreserved()
    {
        var databasePath = GetDatabasePath("knowledge-vector-identity-up.sqlite");
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigrationId).ConfigureAwait(false);
        }

        await SeedLegacyProjectionAsync(databasePath, documentId, chunkId).ConfigureAwait(false);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        await using (var document = connection.CreateCommand())
        {
            document.CommandText =
                "SELECT status, vector_identity, vector_dim, content_hash, storage_path FROM knowledge_documents WHERE document_id = $id;";
            document.Parameters.AddWithValue("$id", documentId);
            await using var reader = await document.ExecuteReaderAsync().ConfigureAwait(false);
            _ = await reader.ReadAsync().ConfigureAwait(false);
            AssertEx.Equal(KnowledgeDocumentStatus.Indexed.ToString(), reader.GetString(0));
            AssertEx.Equal(KnowledgeEmbeddingVectorPolicy.LegacyIdentity, reader.GetString(1));
            AssertEx.Equal(0, reader.GetInt32(2));
            AssertEx.Equal("source-hash", reader.GetString(3));
            AssertEx.Equal("source.txt", reader.GetString(4));
        }

        await using (var vector = connection.CreateCommand())
        {
            vector.CommandText =
                "SELECT vector_identity, dim, length(embedding) FROM knowledge_chunk_vectors WHERE chunk_id = $id;";
            vector.Parameters.AddWithValue("$id", chunkId);
            await using var reader = await vector.ExecuteReaderAsync().ConfigureAwait(false);
            _ = await reader.ReadAsync().ConfigureAwait(false);
            AssertEx.Equal(KnowledgeEmbeddingVectorPolicy.LegacyIdentity, reader.GetString(0));
            AssertEx.Equal(768, reader.GetInt32(1));
            AssertEx.Equal(768 * sizeof(float), reader.GetInt32(2));
        }

        await using var chunk = connection.CreateCommand();
        chunk.CommandText = "SELECT content FROM knowledge_document_chunks WHERE chunk_id = $id;";
        chunk.Parameters.AddWithValue("$id", chunkId);
        AssertEx.Equal("preserved source chunk", (string?)await chunk.ExecuteScalarAsync().ConfigureAwait(false));
    }

    [Test]
    public async Task MigrateAsync_RollbackStructurallyRemovesIdentityColumnsAndPreservesSourceRows()
    {
        var databasePath = GetDatabasePath("knowledge-vector-identity-down.sqlite");
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigrationId).ConfigureAwait(false);
        }

        await SeedLegacyProjectionAsync(databasePath, documentId, chunkId).ConfigureAwait(false);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var documentColumns = await ReadDocumentColumnsAsync(connection).ConfigureAwait(false);
        var vectorColumns = await ReadVectorColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.False(documentColumns.Contains("vector_identity"));
        AssertEx.False(documentColumns.Contains("vector_dim"));
        AssertEx.False(vectorColumns.Contains("vector_identity"));

        await using var source = connection.CreateCommand();
        source.CommandText =
            """
            SELECT COUNT(*)
            FROM knowledge_documents d
            JOIN knowledge_document_chunks c ON c.document_id = d.document_id
            WHERE d.document_id = $document_id AND c.chunk_id = $chunk_id;
            """;
        source.Parameters.AddWithValue("$document_id", documentId);
        source.Parameters.AddWithValue("$chunk_id", chunkId);
        AssertEx.Equal(1L, (long)(await source.ExecuteScalarAsync().ConfigureAwait(false))!);
    }

    [Test]
    public void Model_HasNoPendingChangesAgainstSnapshot()
    {
        var databasePath = GetDatabasePath("knowledge-vector-identity-drift.sqlite");
        using var context = CreateContext(databasePath);
        AssertEx.False(context.Database.HasPendingModelChanges(),
            "The NodeChat model has drifted from the generated AddKnowledgeVectorIdentity snapshot.");
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private static async Task SeedLegacyProjectionAsync(string databasePath, Guid documentId, Guid chunkId)
    {
        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

        await using (var document = connection.CreateCommand())
        {
            document.Transaction = (SqliteTransaction)transaction;
            document.CommandText =
                """
                INSERT INTO knowledge_documents
                    (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path,
                     status, failure_reason, chunk_count, embedding_model, created_at_utc, updated_at_utc)
                VALUES
                    ($document_id, X'01', 'text/plain', '.txt', 42, 'source-hash', 'source.txt',
                     'Indexed', NULL, 1, 'nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M', 1, 1);
                """;
            document.Parameters.AddWithValue("$document_id", documentId);
            _ = await document.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var chunk = connection.CreateCommand())
        {
            chunk.Transaction = (SqliteTransaction)transaction;
            chunk.CommandText =
                """
                INSERT INTO knowledge_document_chunks
                    (chunk_id, document_id, section_id, chunk_index, content, token_count, heading_path)
                VALUES ($chunk_id, $document_id, NULL, 0, 'preserved source chunk', 4, NULL);
                """;
            chunk.Parameters.AddWithValue("$chunk_id", chunkId);
            chunk.Parameters.AddWithValue("$document_id", documentId);
            _ = await chunk.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var vector = connection.CreateCommand())
        {
            vector.Transaction = (SqliteTransaction)transaction;
            vector.CommandText =
                """
                INSERT INTO knowledge_chunk_vectors (chunk_id, document_id, dim, embedding, embedding_model)
                VALUES ($chunk_id, $document_id, 768, zeroblob($bytes), 'nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M');
                """;
            vector.Parameters.AddWithValue("$chunk_id", chunkId);
            vector.Parameters.AddWithValue("$document_id", documentId);
            vector.Parameters.AddWithValue("$bytes", 768 * sizeof(float));
            _ = await vector.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> ReadDocumentColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(knowledge_documents);";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    private static async Task<HashSet<string>> ReadVectorColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(knowledge_chunk_vectors);";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
