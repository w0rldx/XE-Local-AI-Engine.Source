namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Drives the real <see cref="KnowledgeSearchService" /> over a seeded SQLite corpus to prove the MED-007
///     serve-and-disclose behavior: a document whose catalog status is NOT <c>Indexed</c> (a pending re-index or a
///     failed re-ingest) still has its last-known-good chunks returned, but every hit carries the disclosure fields
///     (<see cref="KnowledgeSearchHit.DocumentStatus" /> + <see cref="KnowledgeSearchHit.ServingLastKnownGood" />)
///     so a consumer never treats potentially-stale content as freshly indexed. The embedding arm is intentionally
///     degraded (no provider) and reranking is off, so the fused order is the lexical (FTS) order.
/// </summary>
public sealed class KnowledgeSearchDisclosureTests : IDisposable
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
    [Arguments("Pending", KnowledgeDocumentStatus.Pending)]
    [Arguments("Failed", KnowledgeDocumentStatus.Failed)]
    public async Task SearchAsync_WhenDocumentNotIndexed_ReturnsLastKnownGoodHitsFlaggedStale(string storedStatus, KnowledgeDocumentStatus expectedStatus)
    {
        var databasePath = GetDatabasePath($"disclose-{storedStatus}.sqlite");
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        // The document was successfully indexed once (chunks exist) but its catalog row now shows a re-index that is
        // pending / has failed. Its prior projections are NOT purged, so the search still serves them.
        await SeedDocumentAsync(databasePath, documentId, storedStatus).ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkId, chunkIndex: 0, "alpha content").ConfigureAwait(false);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, [new FtsSearchHit(chunkId, documentId, 1.0)]);

        var result = await service.SearchAsync(new KnowledgeSearchRequest("the query", Limit: 5), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(1, result.Results.Count);
        var hit = result.Results[0];
        AssertEx.Equal(chunkId, hit.ChunkId);
        AssertEx.Equal(expectedStatus, hit.DocumentStatus);
        AssertEx.True(hit.ServingLastKnownGood, "a non-Indexed document's hits must disclose that they are last-known-good");
    }

    [Test]
    public async Task SearchAsync_WhenDocumentIndexed_DoesNotFlagStale()
    {
        var databasePath = GetDatabasePath("disclose-indexed.sqlite");
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentId, "Indexed").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkId, chunkIndex: 0, "alpha content").ConfigureAwait(false);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, [new FtsSearchHit(chunkId, documentId, 1.0)]);

        var result = await service.SearchAsync(new KnowledgeSearchRequest("the query", Limit: 5), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(1, result.Results.Count);
        var hit = result.Results[0];
        AssertEx.Equal(KnowledgeDocumentStatus.Indexed, hit.DocumentStatus);
        AssertEx.False(hit.ServingLastKnownGood);
    }

    // ── service factory ──────────────────────────────────────────────────────────────────────────────

    private static KnowledgeSearchService CreateSearchService(NodeChatDbContext context, IReadOnlyList<FtsSearchHit> ftsHits)
    {
        // Reranking OFF (empty model) so the fused order is the seeded FTS order, unmodified.
        var options = Options.Create(new KnowledgeBaseOptions
        {
            RerankerModelName = string.Empty
        });

        var ftsSearch = Substitute.For<IFtsSearch>();
        ftsSearch.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(ftsHits));

        // The vector arm never runs: with no provider the query embedding degrades, so fused order is the FTS order.
        var vectorSearch = Substitute.For<IVectorSearch>();
        var vectorSearchFactory = Substitute.For<IVectorSearchFactory>();
        vectorSearchFactory.Create().Returns(vectorSearch);

        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProvider(Arg.Any<string>())
                        .Returns(_ => throw new InvalidOperationException("no embedding provider in this test"));

        return new KnowledgeSearchService(context,
            providerResolver,
            new EmbeddingModelResolver(options),
            new KnowledgeEmbeddingPrefixer(),
            ftsSearch,
            vectorSearchFactory,
            new ReciprocalRankFusion(),
            Substitute.For<IRerankerClient>(),
            Substitute.For<IContextExpansionService>(),
            Substitute.For<IKnowledgeQueryEmbeddingCache>(),
            options,
            NullLogger<KnowledgeSearchService>.Instance);
    }

    // ── seed helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task MigrateAsync(string databasePath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private async Task SeedDocumentAsync(string databasePath, Guid documentId, string status)
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
            VALUES ($id, $name, 'text/plain', '.txt', 10, $hash, $path, $status, 1, 'nomic-embed-text', 1, 1);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$name", encryptedName);
        command.Parameters.AddWithValue("$hash", "hash-" + documentId.ToString("N"));
        command.Parameters.AddWithValue("$path", documentId.ToString("D") + ".txt");
        command.Parameters.AddWithValue("$status", status);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task SeedChunkAsync(string databasePath, Guid documentId, Guid chunkId, int chunkIndex, string content)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_document_chunks (chunk_id, document_id, chunk_index, content, token_count)
            VALUES ($chunk, $document, $index, $content, 4);
            """;
        command.Parameters.AddWithValue("$chunk", chunkId);
        command.Parameters.AddWithValue("$document", documentId);
        command.Parameters.AddWithValue("$index", chunkIndex);
        command.Parameters.AddWithValue("$content", content);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
