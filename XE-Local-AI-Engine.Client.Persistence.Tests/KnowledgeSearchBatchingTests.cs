namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Drives the real <see cref="KnowledgeSearchService" /> over a seeded SQLite corpus to prove the batched-search
///     changes preserve behavior: the batched hydration returns the fused candidates in fused order and silently skips a
///     candidate whose row is gone, and running the FTS and embed arms concurrently still feeds the SAME two ranked lists
///     into Reciprocal Rank Fusion (the final order equals the RRF baseline of the two arms).
/// </summary>
public sealed class KnowledgeSearchBatchingTests : IDisposable
{
    private const string ResolvedModel = "nomic-embed-text";
    private const int Dimensions = 8;

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
    public async Task SearchAsync_BatchedHydration_PreservesFusedOrderAndSkipsMissingChunks()
    {
        var databasePath = GetDatabasePath("batch-hydrate.sqlite");
        var documentA = Guid.NewGuid();
        var documentB = Guid.NewGuid();
        var chunkA = Guid.NewGuid();
        var chunkB = Guid.NewGuid();
        var chunkC = Guid.NewGuid();
        var missingChunk = Guid.NewGuid(); // never seeded — the batch hydration must skip it

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentA).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentB).ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentA, chunkA, chunkIndex: 0, "alpha content").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentA, chunkB, chunkIndex: 1, "beta content").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentB, chunkC, chunkIndex: 0, "gamma content").ConfigureAwait(false);

        // FTS returns A, missing, B, C in rank order. Embedding is degraded (no provider), so the fused order is the FTS
        // order; the missing chunk must be dropped by hydration while A, B, C keep their order.
        // BM25 is more-negative-for-stronger, so the rank order A, missing, B, C descends into the negatives.
        var ftsHits = new List<FtsSearchHit>
        {
            new(chunkA, documentA, -4.0),
            new(missingChunk, documentA, -3.0),
            new(chunkB, documentA, -2.0),
            new(chunkC, documentB, -1.0)
        };

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, ftsHits, DegradedProviderResolver());

        var result = await service.SearchAsync(new KnowledgeSearchRequest("the query", Limit: 5), CancellationToken.None).ConfigureAwait(false);

        var orderedChunkIds = result.Results.Select(hit => hit.ChunkId).ToList();
        AssertEx.Equal(3, orderedChunkIds.Count);
        AssertEx.Equal(chunkA, orderedChunkIds[0]);
        AssertEx.Equal(chunkB, orderedChunkIds[1]);
        AssertEx.Equal(chunkC, orderedChunkIds[2]);
    }

    [Test]
    public async Task SearchAsync_FusesBothArms_MatchesRrfBaseline()
    {
        var databasePath = GetDatabasePath("dual-arm.sqlite");
        var documentA = Guid.NewGuid();
        var chunkA = Guid.NewGuid();
        var chunkB = Guid.NewGuid();
        var chunkC = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, documentA).ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentA, chunkA, chunkIndex: 0, "alpha content").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentA, chunkB, chunkIndex: 1, "beta content").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, documentA, chunkC, chunkIndex: 2, "gamma content").ConfigureAwait(false);

        // Lexical arm ranks A then B; semantic arm ranks B then C. Both arms must reach fusion under the concurrent
        // implementation, so the result order must equal the independent RRF baseline of the two lists.
        var ftsRanked = new List<Guid>
        {
            chunkA,
            chunkB
        };
        var vectorRanked = new List<Guid>
        {
            chunkB,
            chunkC
        };

        // BM25 is more-negative-for-stronger: the best-first rank order maps to descending (more-negative) scores.
        var ftsHits = ftsRanked.Select((id, index) => new FtsSearchHit(id, documentA, index - ftsRanked.Count)).ToList();
        var vectorSearch = Substitute.For<IVectorSearch>();
        vectorSearch.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(),
                        Arg.Any<string>(),
                        Arg.Any<string>(),
                        Arg.Any<int>(),
                        Arg.Any<int>(),
                        Arg.Any<Guid?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<VectorSearchHit>>(vectorRanked.Select((id, index) => new VectorSearchHit(id, documentA, 1.0f - (index * 0.1f))).ToList()));

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, ftsHits, ResolvingProviderResolver(), vectorSearch);

        var result = await service.SearchAsync(new KnowledgeSearchRequest("the query", Limit: 5), CancellationToken.None).ConfigureAwait(false);

        var baseline = new ReciprocalRankFusion().Fuse([ftsRanked, vectorRanked]).Select(entry => entry.ChunkId).ToList();
        var orderedChunkIds = result.Results.Select(hit => hit.ChunkId).ToList();
        AssertEx.Equal(baseline.Count, orderedChunkIds.Count);
        for (var index = 0; index < baseline.Count; index++)
        {
            AssertEx.Equal(baseline[index], orderedChunkIds[index]);
        }

        // Sanity: the fused order is genuinely the dual-arm RRF order (B ahead of A, C last), not just the FTS order.
        AssertEx.Equal(chunkB, orderedChunkIds[0]);
        AssertEx.Equal(chunkA, orderedChunkIds[1]);
        AssertEx.Equal(chunkC, orderedChunkIds[2]);
    }

    [Test]
    public async Task SearchAsync_DropsContentDuplicates_KeepingTheHigherRankedOccurrence()
    {
        var databasePath = GetDatabasePath("content-dedup.sqlite");
        var document = Guid.NewGuid();
        var chunkA = Guid.NewGuid();
        var chunkB = Guid.NewGuid();
        var chunkC = Guid.NewGuid();

        await MigrateAsync(databasePath).ConfigureAwait(false);
        await SeedDocumentAsync(databasePath, document).ConfigureAwait(false);
        // A and B carry the SAME content up to whitespace + case; C is distinct. A outranks B in the fused (FTS) order.
        await SeedChunkAsync(databasePath, document, chunkA, chunkIndex: 0, "Shared answer text.").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, document, chunkB, chunkIndex: 1, "shared   answer   text.").ConfigureAwait(false);
        await SeedChunkAsync(databasePath, document, chunkC, chunkIndex: 2, "A different answer.").ConfigureAwait(false);

        // BM25 is more-negative-for-stronger, so the rank order A, B, C descends into the negatives.
        var ftsHits = new List<FtsSearchHit>
        {
            new(chunkA, document, -4.0),
            new(chunkB, document, -3.0),
            new(chunkC, document, -2.0)
        };

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var service = CreateSearchService(context, ftsHits, DegradedProviderResolver());

        var result = await service.SearchAsync(new KnowledgeSearchRequest("the query", Limit: 5), CancellationToken.None).ConfigureAwait(false);

        var orderedChunkIds = result.Results.Select(hit => hit.ChunkId).ToList();
        AssertEx.Equal(2, orderedChunkIds.Count);
        AssertEx.Equal(chunkA, orderedChunkIds[0]);
        AssertEx.Equal(chunkC, orderedChunkIds[1]);
        AssertEx.True(!orderedChunkIds.Contains(chunkB), "The lower-ranked content duplicate (differing only in whitespace/case) must be dropped.");
    }

    // ── service factory ──────────────────────────────────────────────────────────────────────────────

    private static KnowledgeSearchService CreateSearchService(NodeChatDbContext context,
        IReadOnlyList<FtsSearchHit> ftsHits,
        ILocalModelProviderResolver providerResolver,
        IVectorSearch? vectorSearch = null)
    {
        var options = Options.Create(new KnowledgeBaseOptions
        {
            RerankerModelName = string.Empty
        });

        var ftsSearch = Substitute.For<IFtsSearch>();
        ftsSearch.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(ftsHits));

        var vectorSearchFactory = Substitute.For<IVectorSearchFactory>();
        vectorSearchFactory.Create().Returns(vectorSearch ?? Substitute.For<IVectorSearch>());

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

    // Provider resolver that throws so the query embedding degrades (lexical-only fusion).
    private static ILocalModelProviderResolver DegradedProviderResolver()
    {
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProvider(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("no embedding provider in this test"));
        return resolver;
    }

    // Provider resolver whose provider installs the configured embedding model and generates a fixed non-zero vector, so
    // the query embedding succeeds and the semantic arm runs.
    private static ILocalModelProviderResolver ResolvingProviderResolver()
    {
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProvider(Arg.Any<string>()).Returns(new FixedEmbeddingProvider());
        return resolver;
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

    // Node-local provider fake: installs the configured embedding model and returns a fixed non-zero vector so the query
    // embedding succeeds without Ollama or a network round-trip.
    private sealed class FixedEmbeddingProvider : ILocalModelProvider
    {
        public string ProviderName => "llamacpp";

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection) =>
            new FixedGenerator();

        public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([
                new LocalModelDescriptor
                {
                    ModelName = ResolvedModel,
                    ProviderName = "llamacpp",
                    IsAvailable = true,
                    SizeBytes = 1024,
                    ModifiedAt = DateTimeOffset.UnixEpoch,
                    MaxContextTokens = null,
                    Capabilities = []
                }
            ]);

        public IChatClient CreateChatClient(LocalModelSelection selection) =>
            throw new NotSupportedException();

        public Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteModelAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task WarmModelAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UnloadModelAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();

        private sealed class FixedGenerator : IEmbeddingGenerator<string, Embedding<float>>
        {
            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                var embeddings = values.Select(static _ =>
                {
                    var vector = new float[Dimensions];
                    Array.Fill(vector, 0.1f);
                    return new Embedding<float>(vector);
                });
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
            }

            public object? GetService(Type serviceType, object? serviceKey = null) =>
                null;

            public void Dispose()
            {
            }
        }
    }
}
