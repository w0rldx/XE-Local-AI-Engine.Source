namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion.Implementation;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Hermetic, model-free retrieval-eval fixture. It ingests a small labeled corpus of synthetic markdown
///     documents THROUGH THE REAL <see cref="KnowledgeIngestionService" /> — real extraction, real
///     <see cref="HeaderBoundaryChunkingService" /> windowing, the real deterministic-concept embedder, and the real
///     atomic <see cref="KnowledgeIndexWriter" /> (which fires the FTS5 trigger and writes the vector rows) — so the
///     chunk / FTS / vector indexes are genuinely exercised, not hand-seeded. It then builds REAL
///     <see cref="KnowledgeSearchService" /> instances (hybrid, lexical-only, or reranked) over the same SQLite database
///     for <see cref="RetrievalEvalHarness" /> to score.
///     <para>
///         The corpus, queries, and synonym map are the ONLY source of "semantics" — this fixture gates retrieval
///         MECHANICS and lexical/concept quality deterministically, not real embedding-model semantic quality.
///     </para>
/// </summary>
internal sealed class RetrievalEvalFixture : IDisposable
{
    // A moderate width keeps concept-hash collisions rare across the small fixture vocabulary while staying cheap.
    private const int EmbeddingDimensions = 512;

    // Small chunk window so a few-hundred-character document genuinely splits into multiple overlapping chunks — the
    // real windowing/overlap path in HeaderBoundaryChunkingService, not a single whole-document chunk.
    private const int FixtureMaxChunkChars = 220;
    private const int FixtureChunkOverlapChars = 40;

    // One stable repository id for the whole live corpus, as one selected folder would have.
    private const string RepositorySourceId = "retrievalevallivecorpus";

    private readonly string _databasePath;
    private readonly INodeSqliteKeyHolder _keyHolder;
    private readonly KnowledgeBaseOptions _options;
    private readonly ILocalModelProvider _embeddingProvider;
    private readonly List<NodeChatDbContext> _searchContexts = [];

    private RetrievalEvalFixture(string databasePath,
        INodeSqliteKeyHolder keyHolder,
        KnowledgeBaseOptions options,
        ILocalModelProvider embeddingProvider,
        IReadOnlyDictionary<string, Guid> documentIdsByKey)
    {
        _databasePath = databasePath;
        _keyHolder = keyHolder;
        _options = options;
        _embeddingProvider = embeddingProvider;
        DocumentIdsByKey = documentIdsByKey;
    }

    /// <summary>Fixture document key → the id assigned when it was ingested (the relevance label resolution map).</summary>
    public IReadOnlyDictionary<string, Guid> DocumentIdsByKey { get; }

    /// <summary>The labeled evaluation queries for this corpus.</summary>
    public static IReadOnlyList<LabeledQuery> Queries => RetrievalEvalCorpus.Queries;

    /// <summary>
    ///     Migrates a fresh database at <paramref name="databasePath" /> and ingests the whole labeled corpus through the
    ///     real ingestion pipeline. Throws if any document does not reach <see cref="KnowledgeDocumentStatus.Indexed" />,
    ///     so a broken fixture fails loudly instead of silently measuring an empty index.
    /// </summary>
    public static Task<RetrievalEvalFixture> BuildAsync(string databasePath, INodeSqliteKeyHolder keyHolder, CancellationToken cancellationToken) =>
        BuildAsync(databasePath, keyHolder, RetrievalEvalCorpus.Documents, RetrievalEvalCorpus.SynonymToConcept, cancellationToken);

    /// <summary>
    ///     Ingests a caller-supplied labeled corpus and synonym map through the same real pipeline. A fusion/reranker
    ///     comparison uses this to ingest a small DISCRIMINATING corpus (engineered so score-agnostic RRF mis-orders the relevant chunk while
    ///     score-aware fusion recovers it) into its own database, without touching the shared baseline corpus.
    /// </summary>
    public static Task<RetrievalEvalFixture> BuildAsync(string databasePath,
        INodeSqliteKeyHolder keyHolder,
        IReadOnlyList<RetrievalEvalCorpus.FixtureDocument> documents,
        IReadOnlyDictionary<string, string> synonymToConcept,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(synonymToConcept);
        var options = new KnowledgeBaseOptions
        {
            MaxChunkChars = FixtureMaxChunkChars,
            ChunkOverlapChars = FixtureChunkOverlapChars,
            RerankerModelName = string.Empty
        };
        return BuildCoreAsync(databasePath,
            keyHolder,
            documents,
            new DeterministicEmbeddingProvider(EmbeddingDimensions, synonymToConcept),
            options,
            sourcePathOf: null,
            cancellationToken);
    }

    /// <summary>
    ///     Ingests a labeled corpus the way a production repository import does, for the real-model eval: through the
    ///     REAL <see cref="KnowledgeDocumentBlobStore" />, with the metadata <c>KnowledgeRepositoryImportService</c> writes.
    /// </summary>
    /// <remarks>
    ///     The real extension picks extractor, content kind, language and code symbol, and fills the FTS
    ///     <c>source_path</c>/<c>symbol</c> columns. <paramref name="embeddingProvider" /> embeds chunks and queries;
    ///     <paramref name="options" /> sizes chunks (pass production defaults).
    /// </remarks>
    /// <param name="sourcePathOf">Maps a document key to the repository-relative path it is imported under.</param>
    public static Task<RetrievalEvalFixture> BuildAsync(string databasePath,
        INodeSqliteKeyHolder keyHolder,
        IReadOnlyList<RetrievalEvalCorpus.FixtureDocument> documents,
        ILocalModelProvider embeddingProvider,
        KnowledgeBaseOptions options,
        Func<string, string> sourcePathOf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePathOf);
        return BuildCoreAsync(databasePath, keyHolder, documents, embeddingProvider, options, sourcePathOf, cancellationToken);
    }

    private static async Task<RetrievalEvalFixture> BuildCoreAsync(string databasePath,
        INodeSqliteKeyHolder keyHolder,
        IReadOnlyList<RetrievalEvalCorpus.FixtureDocument> documents,
        ILocalModelProvider embeddingProvider,
        KnowledgeBaseOptions options,
        Func<string, string>? sourcePathOf,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(keyHolder);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(options);

        var optionsWrapper = Options.Create(options);

        // A copy of the shared at-head template, not a replay of the whole declared chain: what this fixture exercises
        // is the ingestion pipeline over the schema, never the migrator that produced it. See MigratedDatabaseTemplate.
        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);

        var documentIdsByKey = new Dictionary<string, Guid>(StringComparer.Ordinal);

        // One ingestion context (and connection) for the whole corpus, shared as a production request scope would.
        // Only the repository-import path needs a service provider (the real blob store opens a scope per call).
        await using var repositoryServices = sourcePathOf is null ? null : BuildRepositoryImportServices(databasePath, keyHolder);
        await using (var ingestionContext = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, keyHolder))
        {
            var memoryStore = new InMemoryBlobStore();
            IKnowledgeDocumentBlobStore blobStore = sourcePathOf is null
                ? memoryStore
                : new KnowledgeDocumentBlobStore(repositoryServices!.GetRequiredService<IServiceScopeFactory>(),
                    new FixedNodeDataDirectory(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, Path.GetFileNameWithoutExtension(databasePath) + "-data")),
                    keyHolder,
                    TimeProvider.System);
            var extractor = new DocumentTextExtractor(NullLogger<DocumentTextExtractor>.Instance);
            var chunkingService = new HeaderBoundaryChunkingService(optionsWrapper);
            var providerResolver = new SingleProviderResolver(embeddingProvider);
            var embedder = new KnowledgeChunkEmbedder(providerResolver, new EmbeddingModelResolver(optionsWrapper), new KnowledgeEmbeddingPrefixer(), optionsWrapper);
            var indexWriter = new KnowledgeIndexWriter(ingestionContext, TimeProvider.System);
            var notifier = Substitute.For<IKnowledgeIndexingNotifier>();
            var ingestionService = new KnowledgeIngestionService(ingestionContext,
                blobStore,
                extractor,
                chunkingService,
                embedder,
                indexWriter,
                notifier,
                TimeProvider.System,
                NullLogger<KnowledgeIngestionService>.Instance);

            var connection = ingestionContext.Database.GetDbConnection();
            await OpenAsync(connection, cancellationToken);

            foreach (var document in documents)
            {
                var bytes = Encoding.UTF8.GetBytes(document.Body);
                Guid documentId;
                if (sourcePathOf is null)
                {
                    documentId = Guid.NewGuid();
                    memoryStore.Register(documentId, bytes);
                    await InsertPendingDocumentRowAsync(ingestionContext, connection, documentId, bytes.Length, cancellationToken);
                }
                else
                {
                    documentId = await AddAsRepositoryFileAsync(blobStore, extractor, sourcePathOf(document.Key), bytes, options, cancellationToken);
                }

                documentIdsByKey[document.Key] = documentId;

                await ingestionService.RunAsync(documentId, cancellationToken);

                var status = await ReadStatusAsync(connection, documentId, cancellationToken);
                if (!string.Equals(status, KnowledgeDocumentStatus.Indexed.ToString(), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"Fixture document '{document.Key}' did not reach Indexed (status was '{status}')."));
                }
            }
        }

        return new RetrievalEvalFixture(databasePath, keyHolder, options, embeddingProvider, documentIdsByKey);
    }

    /// <summary>The distinct (vector width, vector identity) pairs the ingested chunk vectors were stored with.</summary>
    public async Task<IReadOnlyList<(long Dim, string Identity)>> ReadVectorShapesAsync(CancellationToken cancellationToken)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(_databasePath, _keyHolder);
        var connection = context.Database.GetDbConnection();
        await OpenAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT dim, vector_identity FROM knowledge_chunk_vectors;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var shapes = new List<(long, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            shapes.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return shapes;
    }

    /// <summary>The default hybrid search: FTS ∪ vector, fused by the shipped default fusion strategy, no reranker.</summary>
    public IKnowledgeSearchService CreateHybridSearchService() =>
        CreateSearchService(HybridProviderResolver(), _options, Substitute.For<IRerankerClient>());

    /// <summary>
    ///     The hybrid search pinned to an explicit fusion strategy (for a before/after comparison). Both variants read
    ///     the SAME ingested index, so a metric difference is attributable purely to the fusion.
    /// </summary>
    public IKnowledgeSearchService CreateHybridSearchService(RankFusionStrategy fusionStrategy, double fusionScoreWeight) =>
        CreateSearchService(HybridProviderResolver(), CloneOptionsWithFusion(fusionStrategy, fusionScoreWeight), Substitute.For<IRerankerClient>());

    private KnowledgeBaseOptions CloneOptionsWithFusion(RankFusionStrategy fusionStrategy, double fusionScoreWeight) =>
        new()
        {
            MaxChunkChars = _options.MaxChunkChars,
            ChunkOverlapChars = _options.ChunkOverlapChars,
            RerankerModelName = _options.RerankerModelName,
            FusionStrategy = fusionStrategy,
            FusionScoreWeight = fusionScoreWeight
        };

    /// <summary>
    ///     A search whose query-embedding provider is unavailable, so the vector arm is skipped and RRF degrades to the
    ///     lexical (FTS) ranking alone.
    /// </summary>
    public IKnowledgeSearchService CreateLexicalOnlySearchService() =>
        CreateSearchService(new UnavailableProviderResolver(), _options, Substitute.For<IRerankerClient>());

    /// <summary>The hybrid search plus a caller-supplied reranker (options carry a non-empty reranker model name).</summary>
    public IKnowledgeSearchService CreateRerankedSearchService(IRerankerClient reranker, int retrievalLatencyBudgetMilliseconds = 500)
    {
        ArgumentNullException.ThrowIfNull(reranker);
        var rerankedOptions = new KnowledgeBaseOptions
        {
            MaxChunkChars = _options.MaxChunkChars,
            ChunkOverlapChars = _options.ChunkOverlapChars,
            RerankerModelName = "bge-reranker-v2-m3",
            AdaptiveRerankingEnabled = false,
            RetrievalLatencyBudgetMilliseconds = retrievalLatencyBudgetMilliseconds
        };
        return CreateSearchService(HybridProviderResolver(), rerankedOptions, reranker);
    }

    /// <summary>
    ///     The hybrid search over the ingestion embedding provider with caller-supplied options and reranker, for the
    ///     real-model eval's gate, budget and fusion matrix. Only the options' search-time settings are read.
    /// </summary>
    /// <param name="logger">The search service's logger (the live eval counts query-embedding degrades); null is silent.</param>
    public IKnowledgeSearchService CreateSearchService(KnowledgeBaseOptions options, IRerankerClient reranker, ILogger<KnowledgeSearchService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reranker);
        return CreateSearchService(HybridProviderResolver(), options, reranker, logger);
    }

    private ILocalModelProviderResolver HybridProviderResolver() =>
        new SingleProviderResolver(_embeddingProvider);

    private IKnowledgeSearchService CreateSearchService(ILocalModelProviderResolver providerResolver,
        KnowledgeBaseOptions options,
        IRerankerClient reranker,
        ILogger<KnowledgeSearchService>? logger = null)
    {
        // A fresh scoped context per search service, mirroring the request-scoped DbContext the real service depends on.
        var context = AgentDefinitionTestContextFactory.CreateForMigration(_databasePath, _keyHolder);
        _searchContexts.Add(context);

        var optionsWrapper = Options.Create(options);
        var vectorSearch = new ManagedCosineVectorSearch(context, new KnowledgeVectorNormalizationState());
        return new KnowledgeSearchService(context,
            providerResolver,
            new EmbeddingModelResolver(optionsWrapper),
            new KnowledgeEmbeddingPrefixer(),
            new FtsSearch(context),
            new DirectVectorSearchFactory(vectorSearch),
            new ReciprocalRankFusion(),
            reranker,
            Substitute.For<IContextExpansionService>(),
            new NoOpQueryEmbeddingCache(),
            optionsWrapper,
            KnowledgeSearchRuntimeSettings.From(optionsWrapper),
            logger ?? NullLogger<KnowledgeSearchService>.Instance);
    }

    public void Dispose()
    {
        foreach (var context in _searchContexts)
        {
            context.Dispose();
        }

        _searchContexts.Clear();
    }


    // The repository-import shape of one file, as KnowledgeRepositoryImportService builds it. Where that service skips an
    // unsupported extension, this throws: a labelled document silently left out of the index would bias every metric.
    private static async Task<Guid> AddAsRepositoryFileAsync(IKnowledgeDocumentBlobStore blobStore,
        DocumentTextExtractor extractor,
        string sourcePath,
        byte[] bytes,
        KnowledgeBaseOptions options,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(sourcePath);
        if (!extractor.IsSupported(extension))
        {
            throw new InvalidOperationException($"Fixture document '{sourcePath}' has an extension the knowledge extractor does not support.");
        }

        var result = await blobStore.AddAsync(new KnowledgeDocumentInput
        {
            DocumentId = Guid.NewGuid(),
            OriginalFileName = sourcePath,
            MimeType = "text/plain",
            Extension = extension,
            SizeBytes = bytes.LongLength,
            ContentHash = Convert.ToHexString(SHA256.HashData(bytes)),
            Content = bytes,
            EmbeddingModel = options.EmbeddingModelName,
            SourcePath = sourcePath.Replace('\\', '/'),
            SourceKind = "repository",
            SourceId = RepositorySourceId
        }, cancellationToken);
        return result.WasInserted
            ? result.DocumentId
            : throw new InvalidOperationException($"Fixture document '{sourcePath}' was not inserted (duplicate source path or content).");
    }

    // The blob store opens its own scoped context per call, as it does under the host's DI container.
    private static ServiceProvider BuildRepositoryImportServices(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        var services = new ServiceCollection();
        _ = services.AddScoped(_ => AgentDefinitionTestContextFactory.CreateForMigration(databasePath, keyHolder));
        return services.BuildServiceProvider();
    }

    private static async Task InsertPendingDocumentRowAsync(NodeChatDbContext context, DbConnection connection, Guid documentId, int sizeBytes, CancellationToken cancellationToken)
    {
        var encryptedName = context.EncryptKnowledgeFileName("document.md", documentId);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path, status, chunk_count, embedding_model, created_at_utc, updated_at_utc)
            VALUES ($id, $name, 'text/markdown', '.md', $size, $hash, $path, 'Pending', 0, '', 1, 1);
            """;
        AddParameter(command, "$id", documentId);
        AddParameter(command, "$name", encryptedName);
        AddParameter(command, "$size", sizeBytes);
        AddParameter(command, "$hash", "hash-" + documentId.ToString("N"));
        AddParameter(command, "$path", documentId.ToString("D") + ".md");
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ReadStatusAsync(DbConnection connection, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM knowledge_documents WHERE document_id = $id;";
        AddParameter(command, "$id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    private static async Task OpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }

    private sealed class FixedNodeDataDirectory : INodeDataDirectory
    {
        public FixedNodeDataDirectory(string root)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
            Root = root;
        }

        public string Root { get; }
    }

    /// <summary>In-memory blob source: the ingestion service reads the raw document bytes from here by id.</summary>
    private sealed class InMemoryBlobStore : IKnowledgeDocumentBlobStore
    {
        private readonly Dictionary<Guid, byte[]> _bytesById = [];

        public void Register(Guid documentId, byte[] bytes) =>
            _bytesById[documentId] = bytes;

        public Task<byte[]?> ReadBytesAsync(Guid documentId, CancellationToken cancellationToken) =>
            Task.FromResult(_bytesById.TryGetValue(documentId, out var bytes) ? bytes : null);

        public Task<KnowledgeDocumentAddResult> AddAsync(KnowledgeDocumentInput input, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The retrieval-eval fixture seeds document rows directly.");

        public Task DeleteBytesAsync(Guid documentId, string extension, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public IReadOnlyList<Guid> ListStoredDocumentIds() =>
            [.. _bytesById.Keys];

        public Task DeleteAllBytesAsync(Guid documentId, CancellationToken cancellationToken)
        {
            _ = _bytesById.Remove(documentId);
            return Task.CompletedTask;
        }

        public KnowledgeBlobReconciliationResult ReconcileInterruptedWrites() =>
            new()
            {
                RestoredBlobNames = [],
                RemovedLitterCount = 0
            };
    }

    /// <summary>Returns the one managed cosine search instance bound to the search service's scoped context.</summary>
    private sealed class DirectVectorSearchFactory : IVectorSearchFactory
    {
        private readonly IVectorSearch _vectorSearch;

        public DirectVectorSearchFactory(IVectorSearch vectorSearch) =>
            _vectorSearch = vectorSearch;

        public IVectorSearch Create() =>
            _vectorSearch;
    }

    /// <summary>A cache that never hits — every query is embedded fresh (the harness is not measuring cache behavior).</summary>
    private sealed class NoOpQueryEmbeddingCache : IKnowledgeQueryEmbeddingCache
    {
        public bool TryGet(string policyFamilyIdentity, string query, out KnowledgeQueryEmbeddingCacheEntry entry)
        {
            entry = default!;
            return false;
        }

        public void Store(string policyFamilyIdentity, string query, KnowledgeQueryEmbeddingCacheEntry entry)
        {
        }
    }
}
