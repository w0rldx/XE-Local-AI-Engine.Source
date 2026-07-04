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
///     Drives the real <see cref="KnowledgeSearchService" /> over a seeded SQLite corpus to prove the rerank stage:
///     when a reranker model is configured the fused candidate pool is rescored and reordered before the top-limit cut;
///     when it is disabled or the reranker degrades (returns <see langword="null" />) the Reciprocal-Rank-Fusion order is
///     kept unchanged. The embedding arm is intentionally degraded (no provider), so the fused order is the lexical
///     (FTS) order — which the reranker then reorders. Reranking scores the BASE chunk content and is bounded to the
///     candidate pool.
/// </summary>
public sealed class KnowledgeSearchRerankTests : IDisposable
{
    private const string RerankerModel = "bge-reranker-v2-m3";

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
    public async Task SearchAsync_RerankerEnabled_ReordersHitsByRelevanceScore()
    {
        var databasePath = GetDatabasePath("rerank-reorder.sqlite");
        var documentId = Guid.NewGuid();
        var chunkAlpha = Guid.NewGuid();
        var chunkBeta = Guid.NewGuid();
        var chunkGamma = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentId).ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkAlpha, chunkIndex: 0, "alpha content").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkBeta, chunkIndex: 1, "beta content").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkGamma, chunkIndex: 2, "gamma content").ConfigureAwait(false);

        // Fusion order (lexical): alpha, beta, gamma. Reranker makes gamma best, then beta, then alpha.
        var ftsHits = new List<FtsSearchHit>
        {
            new(chunkAlpha, documentId, 3.0),
            new(chunkBeta, documentId, 2.0),
            new(chunkGamma, documentId, 1.0)
        };
        var reranker = RerankerScoringBy(ScoreGammaBestBetaMidAlphaLow);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, ftsHits, reranker, RerankerModel);

        var result = await service.SearchAsync(new KnowledgeSearchRequest("the query", Limit: 3), CancellationToken.None).ConfigureAwait(false);

        var orderedChunkIds = result.Results.Select(hit => hit.ChunkId).ToList();
        AssertEx.Equal(3, orderedChunkIds.Count);
        AssertEx.Equal(chunkGamma, orderedChunkIds[0]);
        AssertEx.Equal(chunkBeta, orderedChunkIds[1]);
        AssertEx.Equal(chunkAlpha, orderedChunkIds[2]);
        // The top hit carries its rerank relevance score, not the RRF score.
        AssertEx.Equal(0.9, result.Results[0].Score);
    }

    [Test]
    public async Task SearchAsync_RerankerDegrades_KeepsFusionOrder()
    {
        var databasePath = GetDatabasePath("rerank-degrade.sqlite");
        var documentId = Guid.NewGuid();
        var chunkAlpha = Guid.NewGuid();
        var chunkBeta = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentId).ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkAlpha, chunkIndex: 0, "alpha content").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkBeta, chunkIndex: 1, "beta content").ConfigureAwait(false);

        var ftsHits = new List<FtsSearchHit>
        {
            new(chunkAlpha, documentId, 2.0),
            new(chunkBeta, documentId, 1.0)
        };
        // Reranker is CONFIGURED but unavailable → returns null → the search must keep the RRF order.
        var reranker = Substitute.For<IRerankerClient>();
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<double>?)null);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, ftsHits, reranker, RerankerModel);

        var result = await service.SearchAsync(new KnowledgeSearchRequest("the query", Limit: 3), CancellationToken.None).ConfigureAwait(false);

        var orderedChunkIds = result.Results.Select(hit => hit.ChunkId).ToList();
        AssertEx.Equal(2, orderedChunkIds.Count);
        AssertEx.Equal(chunkAlpha, orderedChunkIds[0]);
        AssertEx.Equal(chunkBeta, orderedChunkIds[1]);
    }

    [Test]
    public async Task SearchAsync_RerankerDisabled_NeverInvokesReranker_AndKeepsFusionOrder()
    {
        var databasePath = GetDatabasePath("rerank-disabled.sqlite");
        var documentId = Guid.NewGuid();
        var chunkAlpha = Guid.NewGuid();
        var chunkBeta = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentId).ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkAlpha, chunkIndex: 0, "alpha content").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkBeta, chunkIndex: 1, "beta content").ConfigureAwait(false);

        var ftsHits = new List<FtsSearchHit>
        {
            new(chunkAlpha, documentId, 2.0),
            new(chunkBeta, documentId, 1.0)
        };
        var reranker = Substitute.For<IRerankerClient>();

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        // Empty reranker model name = reranking OFF.
        var service = CreateSearchService(context, ftsHits, reranker, rerankerModelName: string.Empty);

        var result = await service.SearchAsync(new KnowledgeSearchRequest("the query", Limit: 3), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(chunkAlpha, result.Results[0].ChunkId);
        AssertEx.Equal(chunkBeta, result.Results[1].ChunkId);
        await reranker.DidNotReceive().RerankAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SearchAsync_RerankerEnabled_RescoresPoolThenTakesLimit()
    {
        var databasePath = GetDatabasePath("rerank-pool.sqlite");
        var documentId = Guid.NewGuid();
        var chunkAlpha = Guid.NewGuid();
        var chunkBeta = Guid.NewGuid();
        var chunkGamma = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentId).ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkAlpha, chunkIndex: 0, "alpha content").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkBeta, chunkIndex: 1, "beta content").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentId, chunkGamma, chunkIndex: 2, "gamma content").ConfigureAwait(false);

        var ftsHits = new List<FtsSearchHit>
        {
            new(chunkAlpha, documentId, 3.0),
            new(chunkBeta, documentId, 2.0),
            new(chunkGamma, documentId, 1.0)
        };
        IReadOnlyList<string>? rerankedDocuments = null;
        var reranker = Substitute.For<IRerankerClient>();
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var documents = callInfo.ArgAt<IReadOnlyList<string>>(2);
                    rerankedDocuments = documents;
                    // gamma best, alpha next, beta worst.
                    return documents.Select(ScoreGammaBestAlphaMidBetaLow).ToList();
                });

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, ftsHits, reranker, RerankerModel);

        // Limit is smaller than the fused pool: the reranker must see the whole pool, then the result is cut to limit.
        var result = await service.SearchAsync(new KnowledgeSearchRequest("the query", Limit: 2), CancellationToken.None).ConfigureAwait(false);

        AssertEx.True(rerankedDocuments is not null, "The reranker must be invoked when a model is configured.");
        AssertEx.Equal(3, rerankedDocuments!.Count); // whole fused pool, not just `limit`
        AssertEx.Equal(2, result.Results.Count); // cut to `limit` after reordering
        AssertEx.Equal(chunkGamma, result.Results[0].ChunkId);
        AssertEx.Equal(chunkAlpha, result.Results[1].ChunkId);
    }

    // ── service factory ──────────────────────────────────────────────────────────────────────────────

    private static KnowledgeSearchService CreateSearchService(NodeChatDbContext context,
        IReadOnlyList<FtsSearchHit> ftsHits,
        IRerankerClient reranker,
        string rerankerModelName)
    {
        var options = Options.Create(new KnowledgeBaseOptions
        {
            RerankerModelName = rerankerModelName
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
            reranker,
            Substitute.For<IContextExpansionService>(),
            options,
            NullLogger<KnowledgeSearchService>.Instance);
    }

    // Score maps keyed by the chunk content prefix, kept as named methods so the reranker stubs avoid nested ternaries.
    private static double ScoreGammaBestBetaMidAlphaLow(string content) => content switch
    {
        _ when content.StartsWith("gamma", StringComparison.Ordinal) => 0.9,
        _ when content.StartsWith("beta", StringComparison.Ordinal) => 0.5,
        _ => 0.1
    };

    private static double ScoreGammaBestAlphaMidBetaLow(string content) => content switch
    {
        _ when content.StartsWith("gamma", StringComparison.Ordinal) => 0.9,
        _ when content.StartsWith("alpha", StringComparison.Ordinal) => 0.6,
        _ => 0.2
    };

    private static IRerankerClient RerankerScoringBy(Func<string, double> scorer)
    {
        var reranker = Substitute.For<IRerankerClient>();
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => callInfo.ArgAt<IReadOnlyList<string>>(2).Select(scorer).ToList());
        return reranker;
    }

    // ── seed helpers ─────────────────────────────────────────────────────────────────────────────────

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
            VALUES ($id, $name, 'text/plain', '.txt', 10, $hash, $path, 'Indexed', 3, 'nomic-embed-text', 1, 1);
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
