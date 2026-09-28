namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Drives the real <see cref="KnowledgeSearchService" /> over a seeded SQLite corpus to prove the rerank stage:
///     when a reranker model is configured the fused candidate pool is rescored and reordered before the top-limit cut;
///     when it is disabled or the reranker degrades (returns <see langword="null" />) the Reciprocal-Rank-Fusion order is
///     kept unchanged. The embedding arm is intentionally degraded (no provider), so the fused order is the lexical
///     (FTS) order — which the reranker then reorders. Reranking scores the BASE chunk content and is bounded to the
///     candidate pool.
/// </summary>
[Category(TestCategories.Integration)]
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

        await MigrateAsync(databasePath);
        await SeedDocumentAsync(databasePath, documentId);
        await SeedChunkAsync(databasePath, documentId, chunkAlpha, chunkIndex: 0, "alpha content");
        await SeedChunkAsync(databasePath, documentId, chunkBeta, chunkIndex: 1, "beta content");
        await SeedChunkAsync(databasePath, documentId, chunkGamma, chunkIndex: 2, "gamma content");

        // Fusion order (lexical): alpha, beta, gamma. Reranker makes gamma best, then beta, then alpha.
        // FTS5 BM25 is more-negative-for-stronger (best first), so the scores descend into the negatives with rank.
        var ftsHits = new List<FtsSearchHit>
        {
            new()
            {
                ChunkId = chunkAlpha,
                DocumentId = documentId,
                Bm25Score = -3.0
            },
            new()
            {
                ChunkId = chunkBeta,
                DocumentId = documentId,
                Bm25Score = -2.0
            },
            new()
            {
                ChunkId = chunkGamma,
                DocumentId = documentId,
                Bm25Score = -1.0
            }
        };
        var reranker = RerankerScoringBy(ScoreGammaBestBetaMidAlphaLow);
        var prewarmer = Substitute.For<IKnowledgeModelPrewarmer>();

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, ftsHits, reranker, RerankerModel, prewarmer: prewarmer);

        var result = await service.SearchAsync(new KnowledgeSearchRequest
        {
            Query = "the query",
            Limit = 3
        }, CancellationToken.None);

        // A successful rerank means the reranker is warm: no background warm is requested.
        prewarmer.DidNotReceive().RequestWarm();
        var orderedChunkIds = result.Results.Select(hit => hit.ChunkId).ToList();
        AssertEx.Equal(3, orderedChunkIds.Count);
        AssertEx.Equal(chunkGamma, orderedChunkIds[0]);
        AssertEx.Equal(chunkBeta, orderedChunkIds[1]);
        AssertEx.Equal(chunkAlpha, orderedChunkIds[2]);
        // The top hit carries its rerank relevance score, not the RRF score, and every hit says so.
        AssertEx.Equal(0.9, result.Results[0].Score);
        AssertAllScoreKind(result, KnowledgeScoreKind.Rerank);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SearchAsync_RerankerDegrades_KeepsFusionOrderAndKind(bool countMismatch)
    {
        var databasePath = GetDatabasePath("rerank-degrade.sqlite");
        var documentId = Guid.NewGuid();
        var chunkAlpha = Guid.NewGuid();
        var chunkBeta = Guid.NewGuid();

        await MigrateAsync(databasePath);
        await SeedDocumentAsync(databasePath, documentId);
        await SeedChunkAsync(databasePath, documentId, chunkAlpha, chunkIndex: 0, "alpha content");
        await SeedChunkAsync(databasePath, documentId, chunkBeta, chunkIndex: 1, "beta content");

        // BM25 is more-negative-for-stronger, so alpha (rank 1) is the most negative.
        var ftsHits = new List<FtsSearchHit>
        {
            new()
            {
                ChunkId = chunkAlpha,
                DocumentId = documentId,
                Bm25Score = -2.0
            },
            new()
            {
                ChunkId = chunkBeta,
                DocumentId = documentId,
                Bm25Score = -1.0
            }
        };
        // Reranker is CONFIGURED but unavailable (null) or malformed (one score for two documents) → keep the fusion order.
        IReadOnlyList<double>? scores = countMismatch ? [0.9] : null;
        var reranker = Substitute.For<IRerankerClient>();
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(scores);
        var prewarmer = Substitute.For<IKnowledgeModelPrewarmer>();

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, ftsHits, reranker, RerankerModel, prewarmer: prewarmer);

        var result = await service.SearchAsync(new KnowledgeSearchRequest
        {
            Query = "the query",
            Limit = 3
        }, CancellationToken.None);

        // The rerank ran and produced no usable scores (cold, busy, failed or malformed), so one warm is requested to bring it up or re-probe it.
        await reranker.Received(1).RerankAsync(RerankerModel, "the query", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        prewarmer.Received(1).RequestWarm();

        var orderedChunkIds = result.Results.Select(hit => hit.ChunkId).ToList();
        AssertEx.Equal(2, orderedChunkIds.Count);
        AssertEx.Equal(chunkAlpha, orderedChunkIds[0]);
        AssertEx.Equal(chunkBeta, orderedChunkIds[1]);
        AssertAllScoreKind(result, KnowledgeScoreKind.Fusion);
    }

    [Test]
    public async Task SearchAsync_RerankerDisabled_NeverInvokesReranker_AndKeepsFusionOrder()
    {
        var databasePath = GetDatabasePath("rerank-disabled.sqlite");
        var documentId = Guid.NewGuid();
        var chunkAlpha = Guid.NewGuid();
        var chunkBeta = Guid.NewGuid();

        await MigrateAsync(databasePath);
        await SeedDocumentAsync(databasePath, documentId);
        await SeedChunkAsync(databasePath, documentId, chunkAlpha, chunkIndex: 0, "alpha content");
        await SeedChunkAsync(databasePath, documentId, chunkBeta, chunkIndex: 1, "beta content");

        // BM25 is more-negative-for-stronger, so alpha (rank 1) is the most negative.
        var ftsHits = new List<FtsSearchHit>
        {
            new()
            {
                ChunkId = chunkAlpha,
                DocumentId = documentId,
                Bm25Score = -2.0
            },
            new()
            {
                ChunkId = chunkBeta,
                DocumentId = documentId,
                Bm25Score = -1.0
            }
        };
        var reranker = Substitute.For<IRerankerClient>();

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        // Empty reranker model name = reranking OFF.
        var service = CreateSearchService(context, ftsHits, reranker, rerankerModelName: string.Empty);

        var result = await service.SearchAsync(new KnowledgeSearchRequest
        {
            Query = "the query",
            Limit = 3
        }, CancellationToken.None);

        AssertEx.Equal(chunkAlpha, result.Results[0].ChunkId);
        AssertEx.Equal(chunkBeta, result.Results[1].ChunkId);
        AssertAllScoreKind(result, KnowledgeScoreKind.Fusion);
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

        await MigrateAsync(databasePath);
        await SeedDocumentAsync(databasePath, documentId);
        await SeedChunkAsync(databasePath, documentId, chunkAlpha, chunkIndex: 0, "alpha content");
        await SeedChunkAsync(databasePath, documentId, chunkBeta, chunkIndex: 1, "beta content");
        await SeedChunkAsync(databasePath, documentId, chunkGamma, chunkIndex: 2, "gamma content");

        // BM25 is more-negative-for-stronger, so the fused order alpha, beta, gamma descends into the negatives.
        var ftsHits = new List<FtsSearchHit>
        {
            new()
            {
                ChunkId = chunkAlpha,
                DocumentId = documentId,
                Bm25Score = -3.0
            },
            new()
            {
                ChunkId = chunkBeta,
                DocumentId = documentId,
                Bm25Score = -2.0
            },
            new()
            {
                ChunkId = chunkGamma,
                DocumentId = documentId,
                Bm25Score = -1.0
            }
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
        var result = await service.SearchAsync(new KnowledgeSearchRequest
        {
            Query = "the query",
            Limit = 2
        }, CancellationToken.None);

        AssertEx.True(rerankedDocuments is not null, "The reranker must be invoked when a model is configured.");
        AssertEx.Equal(3, rerankedDocuments!.Count); // whole fused pool, not just `limit`
        AssertEx.Equal(2, result.Results.Count); // cut to `limit` after reordering
        AssertEx.Equal(chunkGamma, result.Results[0].ChunkId);
        AssertEx.Equal(chunkAlpha, result.Results[1].ChunkId);
        AssertAllScoreKind(result, KnowledgeScoreKind.Rerank);
    }

    [Test]
    public async Task SearchAsync_SingleCandidate_GateSkipsReranker_AndKeepsFusionKind()
    {
        var databasePath = GetDatabasePath("rerank-gate-skip.sqlite");
        var documentId = Guid.NewGuid();
        var chunkAlpha = Guid.NewGuid();

        await MigrateAsync(databasePath);
        await SeedDocumentAsync(databasePath, documentId);
        await SeedChunkAsync(databasePath, documentId, chunkAlpha, chunkIndex: 0, "alpha content");

        var ftsHits = new List<FtsSearchHit>
        {
            new()
            {
                ChunkId = chunkAlpha,
                DocumentId = documentId,
                Bm25Score = -1.0
            }
        };
        var reranker = RerankerScoringBy(ScoreGammaBestBetaMidAlphaLow);
        var prewarmer = Substitute.For<IKnowledgeModelPrewarmer>();

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, ftsHits, reranker, RerankerModel, prewarmer: prewarmer);

        var result = await service.SearchAsync(new KnowledgeSearchRequest
        {
            Query = "the query",
            Limit = 3
        }, CancellationToken.None);

        AssertEx.Equal(chunkAlpha, result.Results.Single().ChunkId);
        AssertAllScoreKind(result, KnowledgeScoreKind.Fusion);
        await reranker.DidNotReceive().RerankAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        prewarmer.DidNotReceive().RequestWarm();
    }

    [Test]
    public async Task SearchAsync_RerankBudgetExpires_KeepsFusionOrderAndKind()
    {
        var databasePath = GetDatabasePath("rerank-budget.sqlite");
        var documentId = Guid.NewGuid();
        var chunkAlpha = Guid.NewGuid();
        var chunkBeta = Guid.NewGuid();

        await MigrateAsync(databasePath);
        await SeedDocumentAsync(databasePath, documentId);
        await SeedChunkAsync(databasePath, documentId, chunkAlpha, chunkIndex: 0, "alpha content");
        await SeedChunkAsync(databasePath, documentId, chunkBeta, chunkIndex: 1, "beta content");

        var ftsHits = new List<FtsSearchHit>
        {
            new()
            {
                ChunkId = chunkAlpha,
                DocumentId = documentId,
                Bm25Score = -2.0
            },
            new()
            {
                ChunkId = chunkBeta,
                DocumentId = documentId,
                Bm25Score = -1.0
            }
        };
        // The reranker only ever answers by observing cancellation, so the per-search deadline must end the wait.
        var reranker = Substitute.For<IRerankerClient>();
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => NeverAnswersAsync(callInfo.ArgAt<CancellationToken>(3)));
        var prewarmer = Substitute.For<IKnowledgeModelPrewarmer>();

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        // real-timer: the service times its deadline with Stopwatch, which a FakeTimeProvider cannot advance. The budget
        // must outlast cold-start retrieval (migration, JIT, FTS, hydrate) so the rerank starts and the deadline cuts it.
        var service = CreateSearchService(context, ftsHits, reranker, RerankerModel, retrievalLatencyBudgetMilliseconds: 3000, prewarmer: prewarmer);

        var result = await service.SearchAsync(new KnowledgeSearchRequest
        {
            Query = "the query",
            Limit = 3
        }, CancellationToken.None);

        AssertEx.Equal(chunkAlpha, result.Results[0].ChunkId);
        AssertEx.Equal(chunkBeta, result.Results[1].ChunkId);
        AssertAllScoreKind(result, KnowledgeScoreKind.Fusion);
        // Proves the deadline cut a started rerank, not the early exit that skips reranking once the budget is spent.
        await reranker.Received(1).RerankAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        // An abandoned rerank may be a hung server, which only the warm's liveness re-probe catches.
        prewarmer.Received(1).RequestWarm();
    }

    [Test]
    public async Task SearchAsync_CallerCancelsDuringRerank_Propagates_AndRequestsNoWarm()
    {
        var databasePath = GetDatabasePath("rerank-caller-cancel.sqlite");
        var documentId = Guid.NewGuid();
        var chunkAlpha = Guid.NewGuid();
        var chunkBeta = Guid.NewGuid();

        await MigrateAsync(databasePath);
        await SeedDocumentAsync(databasePath, documentId);
        await SeedChunkAsync(databasePath, documentId, chunkAlpha, chunkIndex: 0, "alpha content");
        await SeedChunkAsync(databasePath, documentId, chunkBeta, chunkIndex: 1, "beta content");

        var ftsHits = new List<FtsSearchHit>
        {
            new()
            {
                ChunkId = chunkAlpha,
                DocumentId = documentId,
                Bm25Score = -2.0
            },
            new()
            {
                ChunkId = chunkBeta,
                DocumentId = documentId,
                Bm25Score = -1.0
            }
        };
        var rerankStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reranker = Substitute.For<IRerankerClient>();
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    rerankStarted.TrySetResult();
                    return NeverAnswersAsync(callInfo.ArgAt<CancellationToken>(3));
                });
        var prewarmer = Substitute.For<IKnowledgeModelPrewarmer>();
        using var caller = new CancellationTokenSource();

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, ftsHits, reranker, RerankerModel, prewarmer: prewarmer);

        var search = service.SearchAsync(new KnowledgeSearchRequest
        {
            Query = "the query",
            Limit = 3
        }, caller.Token);
        await rerankStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await caller.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => search);
        prewarmer.DidNotReceive().RequestWarm();
    }

    private static void AssertAllScoreKind(KnowledgeSearchResult result, KnowledgeScoreKind expected)
    {
        AssertEx.True(result.Results.Count > 0, "The search must return hits for the score kind to be checked.");
        foreach (var hit in result.Results)
        {
            AssertEx.Equal(expected, hit.ScoreKind);
        }
    }

    private static async Task<IReadOnlyList<double>?> NeverAnswersAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return null;
    }


    private static KnowledgeSearchService CreateSearchService(NodeChatDbContext context,
        IReadOnlyList<FtsSearchHit> ftsHits,
        IRerankerClient reranker,
        string rerankerModelName,
        int retrievalLatencyBudgetMilliseconds = 30_000,
        IKnowledgeModelPrewarmer? prewarmer = null)
    {
        var options = Options.Create(new KnowledgeBaseOptions
        {
            RerankerModelName = rerankerModelName,
            AdaptiveRerankingEnabled = false,
            RetrievalLatencyBudgetMilliseconds = retrievalLatencyBudgetMilliseconds
        });

        var ftsSearch = Substitute.For<IFtsSearch>();
        ftsSearch.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            Substitute.For<IKnowledgeQueryEmbeddingCache>(),
            options,
            NullLogger<KnowledgeSearchService>.Instance,
            prewarmer);
    }

    // Score maps keyed by the chunk content prefix, kept as named methods so the reranker stubs avoid nested ternaries.
    private static double ScoreGammaBestBetaMidAlphaLow(string content) =>
        content switch
        {
            _ when content.StartsWith("gamma", StringComparison.Ordinal) => 0.9,
            _ when content.StartsWith("beta", StringComparison.Ordinal) => 0.5,
            _ => 0.1
        };

    private static double ScoreGammaBestAlphaMidBetaLow(string content) =>
        content switch
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


    // A copy of the shared at-head template, not a replay of the whole declared chain: this suite exercises a service
    // over the schema, never the migrator that produced it. See MigratedDatabaseTemplate.
    private static async Task MigrateAsync(string databasePath)
    {
        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);
    }

    private async Task SeedDocumentAsync(string databasePath, Guid documentId)
    {
        byte[] encryptedName;
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            encryptedName = context.EncryptKnowledgeFileName("document.txt", documentId);
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
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
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedChunkAsync(string databasePath, Guid documentId, Guid chunkId, int chunkIndex, string content)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
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
        _ = await command.ExecuteNonQueryAsync();
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
