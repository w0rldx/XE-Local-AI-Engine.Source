namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Proves that <see cref="ContextExpansionService.ExpandBatchAsync" /> (one query per document instead of one
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
        // Two documents → exactly two DB commands (one per document), regardless of how many anchors/ranges each holds.
        var batchQueryCount = service.LastBatchQueryCount;

        AssertEx.Equal(anchors.Count, batched.Count);
        for (var index = 0; index < anchors.Count; index++)
        {
            var anchor = anchors[index];
            var perAnchor = await service.ExpandAsync(anchor.DocumentId, anchor.ChunkIndex, Window, CancellationToken.None).ConfigureAwait(false);
            AssertNeighborsEqual(perAnchor, batched[index]);
        }

        AssertEx.Equal(2, batchQueryCount);
    }

    [Test]
    public async Task ExpandBatchAsync_TwoFarApartAnchors_ReturnsExactWindowsWithoutHydratingTheInterveningRange()
    {
        var databasePath = GetDatabasePath("expand-sparse.sqlite");
        var documentId = Guid.NewGuid();
        const int chunkCount = 200;
        const int nearAnchor = 10;
        const int farAnchor = 190;

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentId).ConfigureAwait(false);
        for (var index = 0; index < chunkCount; index++)
        {
            await SeedChunkAsync(databasePath, documentId, Guid.NewGuid(), index, $"chunk-{index}").ConfigureAwait(false);
        }

        var anchors = new List<KnowledgeNeighborAnchor>
        {
            new(documentId, nearAnchor),
            new(documentId, farAnchor)
        };

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = new ContextExpansionService(context);

        var batched = await service.ExpandBatchAsync(anchors, Window, CancellationToken.None).ConfigureAwait(false);
        // Capture the seams immediately: the two disjoint ranges of this one document must be read by a SINGLE DB command
        // (the one-query-per-document contract), and hydration must be bounded to the union of the two windows.
        var batchQueryCount = service.LastBatchQueryCount;
        var batchRowsHydrated = service.LastBatchRowsHydrated;

        // Content is unchanged: each anchor's window matches per-anchor expansion exactly.
        AssertEx.Equal(2, batched.Count);
        var nearExpected = await service.ExpandAsync(documentId, nearAnchor, Window, CancellationToken.None).ConfigureAwait(false);
        var farExpected = await service.ExpandAsync(documentId, farAnchor, Window, CancellationToken.None).ConfigureAwait(false);
        AssertNeighborsEqual(nearExpected, batched[0]);
        AssertNeighborsEqual(farExpected, batched[1]);

        // One document, two disjoint ranges → exactly one query (not one-per-range, not a min-to-max span).
        AssertEx.Equal(1, batchQueryCount);
        // Hydration is bounded to the union of the two 3-chunk windows (6 rows), NOT the ~181-chunk span between them.
        var expectedWindowRows = ((2 * Window) + 1) * 2;
        AssertEx.Equal(expectedWindowRows, batchRowsHydrated);
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

    [Test]
    public async Task ExpandAsync_CollectionScope_DeniesDocumentFromAnotherNamespace()
    {
        var databasePath = GetDatabasePath("expand-collection-scope.sqlite");
        var documentId = Guid.NewGuid();
        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentId, "PROJECT-A").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, Guid.NewGuid(), 0, "project-a-secret").ConfigureAwait(false);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = new ContextExpansionService(context);

        var denied = await service.ExpandAsync(documentId, 0, Window, "PROJECT-B", CancellationToken.None).ConfigureAwait(false);
        var allowed = await service.ExpandAsync(documentId, 0, Window, "PROJECT-A", CancellationToken.None).ConfigureAwait(false);

        AssertEx.Empty(denied);
        AssertEx.Equal(1, allowed.Count);
        AssertEx.Equal("project-a-secret", allowed[0].Content);
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

    private Task SeedDocumentAsync(string databasePath, Guid documentId)
    {
        return SeedDocumentAsync(databasePath, documentId, KnowledgeCollectionScope.DefaultId);
    }

    private async Task SeedDocumentAsync(string databasePath, Guid documentId, string collectionId)
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
            INSERT INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status, chunk_count, embedding_model, created_at_utc, updated_at_utc, collection_id)
            VALUES ($id, $name, 'text/plain', '.txt', 10, $hash, $path, 'Indexed', 5, 'nomic-embed-text', 1, 1, $collection);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$name", encryptedName);
        command.Parameters.AddWithValue("$hash", "hash-" + documentId.ToString("N"));
        command.Parameters.AddWithValue("$path", documentId.ToString("D") + ".txt");
        command.Parameters.AddWithValue("$collection", collectionId);
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
