namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Proves that <see cref="ContextExpansionService.ExpandBatchAsync" /> (MED-006: one query per document instead of one
///     per hit) returns byte-for-byte the same rows and order as calling <see cref="ContextExpansionService.ExpandAsync" />
///     for each anchor individually — including same-document anchors with overlapping windows, an anchor at the lower
///     boundary, an anchor whose window runs past the last chunk, and anchors spanning multiple documents.
/// </summary>
public sealed class ContextExpansionServiceTests : IDisposable
{
    private const int Window = 1;

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
    public async Task ExpandBatchAsync_MatchesPerAnchorExpansion_AcrossOverlappingBoundaryAndMultiDocumentAnchors()
    {
        var databasePath = GetDatabasePath("expand-batch.sqlite");
        var documentA = Guid.NewGuid();
        var documentB = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentA).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentB).ConfigureAwait(false);
        for (var index = 0; index < 5; index++)
        {
            await SeedChunkAsync(databasePath, documentA, Guid.NewGuid(), index, $"a-chunk-{index}").ConfigureAwait(false);
        }

        for (var index = 0; index < 3; index++)
        {
            await SeedChunkAsync(databasePath, documentB, Guid.NewGuid(), index, $"b-chunk-{index}").ConfigureAwait(false);
        }

        var anchors = new List<KnowledgeNeighborAnchor>
        {
            new(documentA, ChunkIndex: 1), // window 0..2
            new(documentA, ChunkIndex: 2), // window 1..3 — overlaps the previous anchor's window
            new(documentA, ChunkIndex: 0), // window -1..1 — lower bound clamps to the first chunk
            new(documentA, ChunkIndex: 4), // window 3..5 — upper bound runs past the last chunk (index 4)
            new(documentB, ChunkIndex: 1) // a different document
        };

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = new ContextExpansionService(context);

        var batched = await service.ExpandBatchAsync(anchors, Window, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(anchors.Count, batched.Count);
        for (var index = 0; index < anchors.Count; index++)
        {
            var anchor = anchors[index];
            var perAnchor = await service.ExpandAsync(anchor.DocumentId, anchor.ChunkIndex, Window, CancellationToken.None).ConfigureAwait(false);
            AssertNeighborsEqual(perAnchor, batched[index]);
        }
    }

    [Test]
    public async Task ExpandBatchAsync_EmptyAnchors_ReturnsEmpty()
    {
        var databasePath = GetDatabasePath("expand-empty.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = new ContextExpansionService(context);

        var batched = await service.ExpandBatchAsync([], Window, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Empty(batched);
    }

    private static void AssertNeighborsEqual(IReadOnlyList<KnowledgeNeighborChunk> expected, IReadOnlyList<KnowledgeNeighborChunk> actual)
    {
        AssertEx.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            AssertEx.Equal(expected[index].ChunkId, actual[index].ChunkId);
            AssertEx.Equal(expected[index].ChunkIndex, actual[index].ChunkIndex);
            AssertEx.Equal(expected[index].Content, actual[index].Content);
            AssertEx.Equal(expected[index].HeadingPath, actual[index].HeadingPath);
        }
    }

    private async Task MigrateAsync(string databasePath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private async Task SeedDocumentAsync(string databasePath, Guid documentId)
    {
        byte[] encryptedName;
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            encryptedName = context.EncryptKnowledgeFileName("document.txt", documentId);
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status, chunk_count, embedding_model, created_at_utc, updated_at_utc)
            VALUES ($id, $name, 'text/plain', '.txt', 10, $hash, $path, 'Indexed', 5, 'nomic-embed-text', 1, 1);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$name", encryptedName);
        command.Parameters.AddWithValue("$hash", "hash-" + documentId.ToString("N"));
        command.Parameters.AddWithValue("$path", documentId.ToString("D") + ".txt");
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task SeedChunkAsync(string databasePath, Guid documentId, Guid chunkId, int chunkIndex, string content)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_document_chunks (chunk_id, document_id, chunk_index, content, token_count, heading_path)
            VALUES ($chunk, $document, $index, $content, 4, $heading);
            """;
        command.Parameters.AddWithValue("$chunk", chunkId);
        command.Parameters.AddWithValue("$document", documentId);
        command.Parameters.AddWithValue("$index", chunkIndex);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$heading", "Heading > " + content);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
