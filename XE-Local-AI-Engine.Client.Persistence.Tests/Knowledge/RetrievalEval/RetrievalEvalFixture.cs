namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Hermetic, model-free retrieval-eval fixture (RAG-01). It ingests a small labeled corpus of synthetic markdown
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

    private readonly string _databasePath;
    private readonly INodeSqliteKeyHolder _keyHolder;
    private readonly KnowledgeBaseOptions _options;
    private readonly IOptions<KnowledgeBaseOptions> _optionsWrapper;
    private readonly IReadOnlyDictionary<string, string> _synonymToConcept;
    private readonly List<NodeChatDbContext> _searchContexts = [];

    private RetrievalEvalFixture(string databasePath,
        INodeSqliteKeyHolder keyHolder,
        KnowledgeBaseOptions options,
        IReadOnlyDictionary<string, string> synonymToConcept,
        IReadOnlyDictionary<string, Guid> documentIdsByKey)
    {
        _databasePath = databasePath;
        _keyHolder = keyHolder;
        _options = options;
        _optionsWrapper = Options.Create(options);
        _synonymToConcept = synonymToConcept;
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
    ///     Ingests a caller-supplied labeled corpus and synonym map through the same real pipeline. RAG-04 uses this to
    ///     ingest a small DISCRIMINATING corpus (engineered so score-agnostic RRF mis-orders the relevant chunk while
    ///     score-aware fusion recovers it) into its own database, without touching the shared baseline corpus.
    /// </summary>
    public static async Task<RetrievalEvalFixture> BuildAsync(string databasePath,
        INodeSqliteKeyHolder keyHolder,
        IReadOnlyList<RetrievalEvalCorpus.FixtureDocument> documents,
        IReadOnlyDictionary<string, string> synonymToConcept,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(keyHolder);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(synonymToConcept);

        var options = new KnowledgeBaseOptions
        {
            MaxChunkChars = FixtureMaxChunkChars,
            ChunkOverlapChars = FixtureChunkOverlapChars,
            RerankerModelName = string.Empty
        };
        var optionsWrapper = Options.Create(options);

        await using (var migrationContext = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, keyHolder))
        {
            await migrationContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        var documentIdsByKey = new Dictionary<string, Guid>(StringComparer.Ordinal);

        // One ingestion context (and connection) for the whole corpus: the ingestion service and the index writer share
        // it exactly as a request scope would in production.
        await using (var ingestionContext = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, keyHolder))
        {
            var blobStore = new InMemoryBlobStore();
            var extractor = new DocumentTextExtractor(NullLogger<DocumentTextExtractor>.Instance);
            var chunkingService = new HeaderBoundaryChunkingService(optionsWrapper);
            var providerResolver = new SingleProviderResolver(new DeterministicEmbeddingProvider(EmbeddingDimensions, synonymToConcept));
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
            await OpenAsync(connection, cancellationToken).ConfigureAwait(false);

            foreach (var document in documents)
            {
                var documentId = Guid.NewGuid();
                documentIdsByKey[document.Key] = documentId;

                var bytes = Encoding.UTF8.GetBytes(document.Body);
                blobStore.Register(documentId, bytes);
                await InsertPendingDocumentRowAsync(ingestionContext, connection, documentId, bytes.Length, cancellationToken).ConfigureAwait(false);

                await ingestionService.RunAsync(documentId, cancellationToken).ConfigureAwait(false);

                var status = await ReadStatusAsync(connection, documentId, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(status, KnowledgeDocumentStatus.Indexed.ToString(), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"Fixture document '{document.Key}' did not reach Indexed (status was '{status}')."));
                }
            }
        }

        return new RetrievalEvalFixture(databasePath, keyHolder, options, synonymToConcept, documentIdsByKey);
    }

    /// <summary>The default hybrid search: FTS ∪ vector, fused by the shipped default fusion strategy, no reranker.</summary>
    public IKnowledgeSearchService CreateHybridSearchService() =>
        CreateSearchService(HybridProviderResolver(), _options, Substitute.For<IRerankerClient>());

    /// <summary>
    ///     The hybrid search pinned to an explicit fusion strategy (RAG-04 before/after comparison). Both variants read
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
    public IKnowledgeSearchService CreateRerankedSearchService(IRerankerClient reranker)
    {
        ArgumentNullException.ThrowIfNull(reranker);
        var rerankedOptions = new KnowledgeBaseOptions
        {
            MaxChunkChars = _options.MaxChunkChars,
            ChunkOverlapChars = _options.ChunkOverlapChars,
            RerankerModelName = "bge-reranker-v2-m3"
        };
        return CreateSearchService(HybridProviderResolver(), rerankedOptions, reranker);
    }

    private ILocalModelProviderResolver HybridProviderResolver() =>
        new SingleProviderResolver(new DeterministicEmbeddingProvider(EmbeddingDimensions, _synonymToConcept));

    private IKnowledgeSearchService CreateSearchService(ILocalModelProviderResolver providerResolver, KnowledgeBaseOptions options, IRerankerClient reranker)
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
            NullLogger<KnowledgeSearchService>.Instance);
    }

    public void Dispose()
    {
        foreach (var context in _searchContexts)
        {
            context.Dispose();
        }

        _searchContexts.Clear();
    }

    // ── raw-SQL row seed (mirrors the existing KB test seeding: a Pending knowledge_documents row + an in-memory blob) ──

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
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadStatusAsync(DbConnection connection, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM knowledge_documents WHERE document_id = $id;";
        AddParameter(command, "$id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    private static async Task OpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }

    // ── collaborators local to the fixture ──

    /// <summary>In-memory blob source: the ingestion service reads the raw document bytes from here by id.</summary>
    private sealed class InMemoryBlobStore : IKnowledgeDocumentBlobStore
    {
        private readonly Dictionary<Guid, byte[]> _bytesById = [];

        public void Register(Guid documentId, byte[] bytes) => _bytesById[documentId] = bytes;

        public Task<byte[]?> ReadBytesAsync(Guid documentId, CancellationToken cancellationToken) =>
            Task.FromResult(_bytesById.TryGetValue(documentId, out var bytes) ? bytes : null);

        public Task<KnowledgeDocumentAddResult> AddAsync(KnowledgeDocumentInput input, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The retrieval-eval fixture seeds document rows directly.");

        public Task DeleteBytesAsync(Guid documentId, string extension, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>Returns the one managed cosine search instance bound to the search service's scoped context.</summary>
    private sealed class DirectVectorSearchFactory : IVectorSearchFactory
    {
        private readonly IVectorSearch _vectorSearch;

        public DirectVectorSearchFactory(IVectorSearch vectorSearch) => _vectorSearch = vectorSearch;

        public IVectorSearch Create() => _vectorSearch;
    }

    /// <summary>A cache that never hits — every query is embedded fresh (the harness is not measuring cache behavior).</summary>
    private sealed class NoOpQueryEmbeddingCache : IKnowledgeQueryEmbeddingCache
    {
        public bool TryGet(string resolvedModel, string query, out ReadOnlyMemory<float> vector)
        {
            vector = ReadOnlyMemory<float>.Empty;
            return false;
        }

        public void Store(string resolvedModel, string query, ReadOnlyMemory<float> vector)
        {
        }
    }
}
