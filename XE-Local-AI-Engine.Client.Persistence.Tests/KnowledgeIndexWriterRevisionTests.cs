namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Revision compare-and-swap coverage for the final knowledge-index write. Repository updates keep a stable document
///     id, so an old embedding job must not commit merely because the source row still exists.
/// </summary>
public sealed class KnowledgeIndexWriterRevisionTests : IDisposable
{
    private const string CurrentContentHash = "current-repository-revision";
    private const string StaleContentHash = "stale-repository-revision";

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
    public async Task WriteAsync_WhenSourceRevisionChanged_RejectsStaleProjectionAndPreservesCurrentRevisionPending()
    {
        var databasePath = Path.Combine(_rootPath, "revision-race.sqlite");
        var documentId = Guid.NewGuid();
        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedCurrentRepositoryRevisionAsync(databasePath, documentId).ConfigureAwait(false);

        bool written;
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
            var writer = new KnowledgeIndexWriter(context, TimeProvider.System);
            written = await writer.WriteAsync(StaleInput(documentId), CancellationToken.None).ConfigureAwait(false);
        }

        var (contentHash, status, failureReason, chunkCount) = await ReadStateAsync(databasePath, documentId).ConfigureAwait(false);
        AssertEx.False(written, "The old embedding job must not commit after a repository replacement changed the source hash.");
        AssertEx.Equal(CurrentContentHash, contentHash);
        AssertEx.Equal(KnowledgeDocumentStatus.Pending.ToString(), status);
        AssertEx.True(failureReason is null, "The retryable current revision must not retain a stale failure reason.");
        AssertEx.Equal(0, chunkCount);
        AssertEx.Equal(0, await CountChunksAsync(databasePath, documentId).ConfigureAwait(false));
    }

    private static KnowledgeIndexInput StaleInput(Guid documentId)
    {
        return new KnowledgeIndexInput(documentId,
            StaleContentHash,
            "test-embedding-model",
            "test-embedding-model::native:v1:1",
            VectorDimension: 1,
            Sections: [new KnowledgeChunkingSection(Ordinal: 0, Heading: null, Level: null)],
            Chunks:
            [
                new KnowledgeIndexChunk(ChunkIndex: 0,
                    SectionOrdinal: 0,
                    Content: "content from the stale revision",
                    HeadingPath: null,
                    TokenCount: 6,
                    Embedding: BitConverter.GetBytes(1F),
                    Dim: 1)
            ]);
    }

    private async Task MigrateAsync(string databasePath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private async Task SeedCurrentRepositoryRevisionAsync(string databasePath, Guid documentId)
    {
        byte[] encryptedName;
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            encryptedName = context.EncryptKnowledgeFileName("src/current.cs", documentId);
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await EnsureForeignKeysOffAsync(connection).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_documents
                (document_id, collection_id, original_file_name, mime_type, extension, size_bytes, content_hash,
                 storage_path, source_path, source_kind, status, failure_reason, chunk_count, embedding_model,
                 vector_identity, vector_dim, parser_version, chunker_version, created_at_utc, updated_at_utc)
            VALUES
                ($id, 'REPO-TEST', $name, 'text/plain', '.cs', 32, $hash, $storage_path, 'src/current.cs',
                 'repository', $status, 'stale transition', 7, 'test-embedding-model', 'legacy', 0, $parser, $chunker, 1, 2);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$name", encryptedName);
        command.Parameters.AddWithValue("$hash", CurrentContentHash);
        command.Parameters.AddWithValue("$storage_path", string.Concat(documentId.ToString("D"), ".cs"));
        // Simulate an old job having advanced status after the repository replacement first set the row Pending. The
        // revision-reject path must restore the CURRENT source to Pending rather than leave this stale transition behind.
        command.Parameters.AddWithValue("$status", KnowledgeDocumentStatus.Embedding.ToString());
        command.Parameters.AddWithValue("$parser", KnowledgeIndexVersions.Parser);
        command.Parameters.AddWithValue("$chunker", KnowledgeIndexVersions.Chunker);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<(string ContentHash, string Status, string? FailureReason, int ChunkCount)> ReadStateAsync(string databasePath,
        Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_hash, status, failure_reason, chunk_count FROM knowledge_documents WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        AssertEx.True(await reader.ReadAsync().ConfigureAwait(false));
        return (reader.GetString(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2).ConfigureAwait(false) ? null : reader.GetString(2),
            reader.GetInt32(3));
    }

    private static async Task<int> CountChunksAsync(string databasePath, Guid documentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM knowledge_document_chunks WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

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
}
