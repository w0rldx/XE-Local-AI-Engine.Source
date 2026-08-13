namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge;

using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Regression coverage for the collection boundary shared by both retrieval arms and for the structure-aware FTS
///     schema. Every test migrates a real SQLite database, so the FTS5 external-content table, triggers, BM25 weighting,
///     vector joins, and final hydration query are exercised together with the production SQL.
/// </summary>
public sealed class KnowledgeCollectionSearchTests : IDisposable
{
    private const string CollectionA = "PROJECT-A";
    private const string CollectionB = "PROJECT-B";
    private const string EmbeddingModel = "test-embedding-model";
    private const string VectorIdentity = "test-embedding-model::native:v1:4";

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
    public async Task FtsSearch_CollectionAndDocumentScopes_CannotEscapeNamespace()
    {
        var databasePath = GetDatabasePath("fts-namespace.sqlite");
        var documentA = Guid.NewGuid();
        var documentB = Guid.NewGuid();
        var chunkA = Guid.NewGuid();
        var chunkB = Guid.NewGuid();
        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentA, CollectionA).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentB, CollectionB).ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentA, chunkA, chunkIndex: 0, content: "shared lexical needle").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentB, chunkB, chunkIndex: 0, content: "shared lexical needle").ConfigureAwait(false);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var search = new FtsSearch(context);

        var scoped = await search.SearchAsync("needle", limit: 10, documentId: null, CollectionA, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, scoped.Count);
        AssertEx.Equal(chunkA, scoped[0].ChunkId);
        AssertEx.Equal(documentA, scoped[0].DocumentId);

        var escapedDocumentFilter = await search.SearchAsync("needle", limit: 10, documentB, CollectionA, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Empty(escapedDocumentFilter,
            "A document id from another collection must not bypass the collection predicate in the lexical arm.");
    }

    [Test]
    public async Task VectorSearch_CollectionAndDocumentScopes_CannotEscapeNamespace()
    {
        var databasePath = GetDatabasePath("vector-namespace.sqlite");
        var documentA = Guid.NewGuid();
        var documentB = Guid.NewGuid();
        var chunkA = Guid.NewGuid();
        var chunkB = Guid.NewGuid();
        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentA, CollectionA).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentB, CollectionB).ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentA, chunkA, chunkIndex: 0, content: "alpha").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentB, chunkB, chunkIndex: 0, content: "beta").ConfigureAwait(false);
        await SeedVectorAsync(databasePath, documentA, chunkA, [1f, 0f, 0f, 0f]).ConfigureAwait(false);
        await SeedVectorAsync(databasePath, documentB, chunkB, [1f, 0f, 0f, 0f]).ConfigureAwait(false);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var search = new ManagedCosineVectorSearch(context, new KnowledgeVectorNormalizationState());
        var query = new float[]
        {
            1f,
            0f,
            0f,
            0f
        };

        var scoped = await search.SearchAsync(query,
            EmbeddingModel,
            VectorIdentity,
            vectorDimension: 4,
            limit: 10,
            documentId: null,
            CollectionA,
            CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, scoped.Count);
        AssertEx.Equal(chunkA, scoped[0].ChunkId);
        AssertEx.Equal(documentA, scoped[0].DocumentId);

        var escapedDocumentFilter = await search.SearchAsync(query,
            EmbeddingModel,
            VectorIdentity,
            vectorDimension: 4,
            limit: 10,
            documentB,
            CollectionA,
            CancellationToken.None).ConfigureAwait(false);
        AssertEx.Empty(escapedDocumentFilter,
            "A document id from another collection must not bypass the collection predicate in the dense arm.");
    }

    [Test]
    public async Task FtsSearch_StructureAwareWeights_RankSymbolThenPathThenHeadingThenBody()
    {
        var databasePath = GetDatabasePath("fts-weights.sqlite");
        var document = Guid.NewGuid();
        var symbolChunk = Guid.NewGuid();
        var pathChunk = Guid.NewGuid();
        var headingChunk = Guid.NewGuid();
        var bodyChunk = Guid.NewGuid();
        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, document, CollectionA).ConfigureAwait(false);

        await SeedChunkAsync(databasePath, document, symbolChunk, 0, "ordinary body", symbol: "ExactNeedle").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, document, pathChunk, 1, "ordinary body", sourcePath: "src/ExactNeedle.cs").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, document, headingChunk, 2, "ordinary body", headingPath: "ExactNeedle").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, document, bodyChunk, 3, "ExactNeedle").ConfigureAwait(false);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var hits = await new FtsSearch(context)
                         .SearchAsync("ExactNeedle", limit: 10, documentId: null, CollectionA, CancellationToken.None)
                         .ConfigureAwait(false);

        AssertEx.Equal(expected: 4, hits.Count);
        AssertEx.Equal(symbolChunk, hits[0].ChunkId, "Symbol matches carry the largest configured BM25 weight.");
        AssertEx.Equal(pathChunk, hits[1].ChunkId, "Source-path matches should outrank headings and body-only matches.");
        AssertEx.Equal(headingChunk, hits[2].ChunkId, "Heading matches should outrank body-only matches.");
        AssertEx.Equal(bodyChunk, hits[3].ChunkId);
        AssertEx.True(hits[0].Bm25Score < hits[1].Bm25Score
                      && hits[1].Bm25Score < hits[2].Bm25Score
                      && hits[2].Bm25Score < hits[3].Bm25Score,
            "FTS5 BM25 is lower-is-better, so structural weights must produce strictly stronger scores in priority order.");
    }

    [Test]
    public async Task KnowledgeSearch_HitCarriesScopedSourceAndContentProvenance()
    {
        var databasePath = GetDatabasePath("search-provenance.sqlite");
        var documentA = Guid.NewGuid();
        var documentB = Guid.NewGuid();
        var chunkA = Guid.NewGuid();
        var chunkB = Guid.NewGuid();
        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentA, CollectionA).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentB, CollectionB).ConfigureAwait(false);
        await SeedChunkAsync(databasePath,
            documentA,
            chunkA,
            chunkIndex: 0,
            content: "provenance needle",
            sourcePath: "src/Services/WidgetService.cs",
            headingPath: "WidgetService > ExecuteAsync",
            contentKind: "code",
            language: "csharp",
            symbol: "WidgetService.ExecuteAsync",
            pageNumber: 7,
            startOffset: 120,
            endOffset: 341).ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentB, chunkB, chunkIndex: 0, content: "provenance needle").ConfigureAwait(false);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateLexicalSearchService(context);
        var result = await service.SearchAsync(new KnowledgeSearchRequest("needle", Limit: 10, CollectionId: "project-a"), CancellationToken.None)
                                  .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, result.Results.Count);
        var hit = result.Results[0];
        AssertEx.Equal(documentA, hit.DocumentId);
        AssertEx.Equal(chunkA, hit.ChunkId);
        AssertEx.Equal(CollectionA, hit.CollectionId);
        AssertEx.Equal("src/Services/WidgetService.cs", hit.SourcePath);
        AssertEx.Equal("code", hit.ContentKind);
        AssertEx.Equal("csharp", hit.Language);
        AssertEx.Equal("WidgetService.ExecuteAsync", hit.Symbol);
        AssertEx.Equal<int?>(7, hit.PageNumber);
        AssertEx.Equal(expected: 120, hit.StartOffset);
        AssertEx.Equal(expected: 341, hit.EndOffset);
        AssertEx.Equal("WidgetService > ExecuteAsync", hit.Section);
    }

    private static KnowledgeSearchService CreateLexicalSearchService(NodeChatDbContext context)
    {
        var options = Options.Create(new KnowledgeBaseOptions
        {
            RerankerModelName = string.Empty
        });
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProvider(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("embedding unavailable in lexical regression test"));
        var vectorSearchFactory = Substitute.For<IVectorSearchFactory>();

        return new KnowledgeSearchService(context,
            providerResolver,
            new EmbeddingModelResolver(options),
            new KnowledgeEmbeddingPrefixer(),
            new FtsSearch(context),
            vectorSearchFactory,
            new ReciprocalRankFusion(),
            Substitute.For<IRerankerClient>(),
            Substitute.For<IContextExpansionService>(),
            Substitute.For<IKnowledgeQueryEmbeddingCache>(),
            options,
            NullLogger<KnowledgeSearchService>.Instance);
    }

    private async Task MigrateAsync(string databasePath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private async Task SeedDocumentAsync(string databasePath, Guid documentId, string collectionId)
    {
        byte[] encryptedName;
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            encryptedName = context.EncryptKnowledgeFileName("document.txt", documentId);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_documents
                (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status,
                 chunk_count, embedding_model, vector_identity, collection_id, created_at_utc, updated_at_utc)
            VALUES
                ($id, $name, 'text/plain', '.txt', 10, $hash, $path, 'Indexed', 4, $model, $identity, $collection, 1, 1);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$name", encryptedName);
        command.Parameters.AddWithValue("$hash", "hash-" + documentId.ToString("N"));
        command.Parameters.AddWithValue("$path", documentId.ToString("D") + ".txt");
        command.Parameters.AddWithValue("$model", EmbeddingModel);
        command.Parameters.AddWithValue("$identity", VectorIdentity);
        command.Parameters.AddWithValue("$collection", collectionId);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task SeedChunkAsync(string databasePath,
        Guid documentId,
        Guid chunkId,
        int chunkIndex,
        string content,
        string? sourcePath = null,
        string? headingPath = null,
        string contentKind = "text",
        string? language = null,
        string? symbol = null,
        int? pageNumber = null,
        int startOffset = 0,
        int endOffset = 0)
    {
        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_document_chunks
                (chunk_id, document_id, chunk_index, content, token_count, source_path, heading_path, content_kind,
                 language, symbol, page_number, start_offset, end_offset, content_hash, embedding_input_hash)
            VALUES
                ($chunk, $document, $index, $content, 4, $source_path, $heading_path, $content_kind,
                 $language, $symbol, $page_number, $start_offset, $end_offset, $content_hash, $embedding_input_hash);
            """;
        command.Parameters.AddWithValue("$chunk", chunkId);
        command.Parameters.AddWithValue("$document", documentId);
        command.Parameters.AddWithValue("$index", chunkIndex);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$source_path", (object?)sourcePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$heading_path", (object?)headingPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$content_kind", contentKind);
        command.Parameters.AddWithValue("$language", (object?)language ?? DBNull.Value);
        command.Parameters.AddWithValue("$symbol", (object?)symbol ?? DBNull.Value);
        command.Parameters.AddWithValue("$page_number", pageNumber is int value ? value : DBNull.Value);
        command.Parameters.AddWithValue("$start_offset", startOffset);
        command.Parameters.AddWithValue("$end_offset", endOffset);
        command.Parameters.AddWithValue("$content_hash", "chunk-" + chunkId.ToString("N"));
        command.Parameters.AddWithValue("$embedding_input_hash", "embedding-" + chunkId.ToString("N"));
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task SeedVectorAsync(string databasePath, Guid documentId, Guid chunkId, float[] vector)
    {
        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_chunk_vectors (chunk_id, document_id, dim, embedding, embedding_model, vector_identity)
            VALUES ($chunk, $document, $dim, $embedding, $model, $identity);
            """;
        command.Parameters.AddWithValue("$chunk", chunkId);
        command.Parameters.AddWithValue("$document", documentId);
        command.Parameters.AddWithValue("$dim", vector.Length);
        command.Parameters.AddWithValue("$embedding", MemoryMarshal.AsBytes<float>(vector).ToArray());
        command.Parameters.AddWithValue("$model", EmbeddingModel);
        command.Parameters.AddWithValue("$identity", VectorIdentity);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
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
