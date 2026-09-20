namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IKnowledgeSearchService" />: embeds the query, retrieves from both arms, fuses, optionally
///     reranks, hydrates, and optionally expands each hit with its surrounding neighbors.
/// </summary>
/// <remarks>
///     Embedding applies the query-intent prefix; the lexical FTS arm and the model-scoped semantic vector arm are fused
///     with Reciprocal Rank Fusion, and the fused pool is optionally rescored by a local cross-encoder
///     (<see cref="KnowledgeBaseOptions.RerankerModelName" />) before the top-<c>limit</c> cut. An unavailable embedding
///     model or reranker degrades gracefully — lexical-only, or fusion order — rather than failing. No query or chunk
///     text is ever logged. Scoped: it drives the scoped collaborators through the request-scoped db context.
/// </remarks>
public sealed class KnowledgeSearchService : IKnowledgeSearchService
{
    /// <summary>Provenance tag stamped on every hit from this retrieval surface.</summary>
    private const string SourceTag = "knowledge-base";

    /// <summary>Neighbor <c>chunk_index</c> window applied on each side of a match when expansion is requested.</summary>
    private const int NeighborWindow = 1;

    /// <summary>Per-arm candidate pool fetched before fusion, so RRF has enough overlap material to combine.</summary>
    private const int CandidatePoolMultiplier = 4;

    private const int MinimumCandidatePool = 20;

    private readonly NodeChatDbContext _dbContext;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IEmbeddingModelResolver _embeddingModelResolver;
    private readonly IKnowledgeEmbeddingPrefixer _prefixer;
    private readonly IFtsSearch _ftsSearch;
    private readonly IVectorSearchFactory _vectorSearchFactory;
    private readonly IRankingFusionService _fusion;
    private readonly IRerankerClient _reranker;
    private readonly IContextExpansionService _contextExpansion;
    private readonly IKnowledgeQueryEmbeddingCache _queryEmbeddingCache;
    private readonly KnowledgeBaseOptions _options;
    private readonly ILogger<KnowledgeSearchService> _logger;

    public KnowledgeSearchService(NodeChatDbContext dbContext,
        ILocalModelProviderResolver providerResolver,
        IEmbeddingModelResolver embeddingModelResolver,
        IKnowledgeEmbeddingPrefixer prefixer,
        IFtsSearch ftsSearch,
        IVectorSearchFactory vectorSearchFactory,
        IRankingFusionService fusion,
        IRerankerClient reranker,
        IContextExpansionService contextExpansion,
        IKnowledgeQueryEmbeddingCache queryEmbeddingCache,
        IOptions<KnowledgeBaseOptions> options,
        ILogger<KnowledgeSearchService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _embeddingModelResolver = embeddingModelResolver ?? throw new ArgumentNullException(nameof(embeddingModelResolver));
        _prefixer = prefixer ?? throw new ArgumentNullException(nameof(prefixer));
        _ftsSearch = ftsSearch ?? throw new ArgumentNullException(nameof(ftsSearch));
        _vectorSearchFactory = vectorSearchFactory ?? throw new ArgumentNullException(nameof(vectorSearchFactory));
        _fusion = fusion ?? throw new ArgumentNullException(nameof(fusion));
        _reranker = reranker ?? throw new ArgumentNullException(nameof(reranker));
        _contextExpansion = contextExpansion ?? throw new ArgumentNullException(nameof(contextExpansion));
        _queryEmbeddingCache = queryEmbeddingCache ?? throw new ArgumentNullException(nameof(queryEmbeddingCache));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<KnowledgeSearchResult> SearchAsync(KnowledgeSearchRequest request, CancellationToken cancellationToken)
    {
        var searchStart = Stopwatch.GetTimestamp();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new KnowledgeSearchResult { Results = [] };
        }

        if (!KnowledgeCollectionScope.TryNormalize(request.CollectionId, out var collectionId))
        {
            return new KnowledgeSearchResult { Results = [] };
        }

        var limit = Math.Max(1, request.Limit);
        var candidatePool = Math.Max(MinimumCandidatePool, limit * CandidatePoolMultiplier);

        // The arms overlap safely: only the lexical arm touches the non-thread-safe request-scoped SQLite connection, the
        // embed arm only the provider process. Overlap rule: docs/wiki/15-knowledge-base.md ("Hybrid retrieval").
        var ftsArm = RunFtsArmAsync(request.Query, candidatePool, request.DocumentId, collectionId, cancellationToken);
        var embedArm = RunEmbedArmAsync(request.Query, cancellationToken);
        await Task.WhenAll(ftsArm, embedArm);

        // Carry each arm's SCORE into fusion, not only its rank. The raw scales are incomparable and oppositely oriented
        // (FTS5 BM25 is more-NEGATIVE-for-stronger), so negate BM25 here and leave per-arm blending to the fusion service.
        var ftsRanked = (await ftsArm)
                        .Select(hit => new RankFusionInput(hit.ChunkId, -hit.Bm25Score))
                        .ToList();
        var (queryVector, resolvedModel, vectorIdentity) = await embedArm;

        var vectorRanked = new List<RankFusionInput>();
        if (!queryVector.IsEmpty)
        {
            var vectorStart = Stopwatch.GetTimestamp();
            var vectorSearch = _vectorSearchFactory.Create();
            var vectorHits = await vectorSearch.SearchAsync(queryVector,
                                                   resolvedModel,
                                                   vectorIdentity,
                                                   queryVector.Length,
                                                   candidatePool,
                                                   request.DocumentId,
                                                   collectionId,
                                                   cancellationToken);
            RecordStage("vector", vectorStart);
            vectorRanked = vectorHits.Select(hit => new RankFusionInput(hit.ChunkId, hit.Score)).ToList();
        }

        var fused = _fusion.FuseScored([ftsRanked, vectorRanked], _options.FusionStrategy, _options.FusionScoreWeight);
        if (fused.Count == 0)
        {
            return new KnowledgeSearchResult { Results = [] };
        }

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        // Hydrate the fused candidate POOL once in fused order, then drop content duplicates BEFORE any top-`limit` cut so
        // near-identical chunks under different ids cannot crowd out distinct results; the higher-ranked one is kept.
        var pool = await HydratePoolAsync(connection, fused, candidatePool, collectionId, cancellationToken);
        var deduped = DeduplicateByContent(pool);

        // Optional rerank: a configured reranker rescores the deduped pool and reorders BEFORE the top-`limit` cut, so a
        // strong but lexically weak chunk can surface. Off or failed, the order stays RRF; it scores pre-expansion content.
        var rerankDecision = AdaptiveRetrievalPolicy.DecideRerank(_options.AdaptiveRerankingEnabled,
            !string.IsNullOrWhiteSpace(_options.RerankerModelName),
            ftsRanked,
            vectorRanked,
            deduped.Count,
            Stopwatch.GetElapsedTime(searchStart),
            TimeSpan.FromMilliseconds(Math.Max(1, _options.RetrievalLatencyBudgetMilliseconds)));
        var selections = rerankDecision.ShouldRerank
            ? await RerankWithinBudgetAsync(request.Query, deduped, limit, searchStart, cancellationToken)
            : deduped.Take(limit).ToList();

        // Neighbor expansion (when requested) is resolved for the whole final top-k in one batched call rather than one
        // round trip per hit; the content ordering and fallback are identical to expanding each hit individually.
        var contents = await ResolveContentsAsync(selections, request.ExpandNeighbors, cancellationToken);

        var hits = new List<KnowledgeSearchHit>(selections.Count);
        for (var index = 0; index < selections.Count; index++)
        {
            var selection = selections[index];

            // A hit only exists because the document has queryable chunks, so disclose last-known-good projections while a
            // re-index is pending or failed, and when an Indexed row still carries an older vector identity.
            var servingLastKnownGood =
                selection.Row.DocumentStatus != KnowledgeDocumentStatus.Indexed
                || (!queryVector.IsEmpty
                    && !string.Equals(selection.Row.VectorIdentity, vectorIdentity, StringComparison.Ordinal));

            hits.Add(new KnowledgeSearchHit
            {
                DocumentId = selection.Row.DocumentId,
                ChunkId = selection.ChunkId,
                Title = DeriveTitle(selection.Row.HeadingPath, selection.Row.StoragePath),
                Section = selection.Row.HeadingPath,
                Content = contents[index],
                Source = SourceTag,
                Score = selection.Score,
                ChunkIndex = selection.Row.ChunkIndex,
                DocumentStatus = selection.Row.DocumentStatus,
                ServingLastKnownGood = servingLastKnownGood,
                CollectionId = selection.Row.CollectionId,
                SourcePath = selection.Row.SourcePath,
                ContentKind = selection.Row.ContentKind,
                Language = selection.Row.Language,
                Symbol = selection.Row.Symbol,
                PageNumber = selection.Row.PageNumber,
                StartOffset = selection.Row.StartOffset,
                EndOffset = selection.Row.EndOffset
            });
        }

        return new KnowledgeSearchResult { Results = hits };
    }

    // Lexical arm wrapper: times the FTS round trip. Reads the request-scoped DB connection (so it never overlaps another
    // DB command — the embedding arm it runs beside touches only the provider process).
    private async Task<IReadOnlyList<FtsSearchHit>> RunFtsArmAsync(string query,
        int candidatePool,
        Guid? documentId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await _ftsSearch.SearchAsync(query, candidatePool, documentId, collectionId, cancellationToken);
        }
        finally
        {
            RecordStage("fts", start);
        }
    }

    // Semantic arm wrapper: times the query-embedding round trip. Only calls the embedding provider — never the DB — so it
    // is safe to overlap with the lexical arm above.
    private async Task<QueryEmbedding> RunEmbedArmAsync(string query,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await TryEmbedQueryAsync(query, cancellationToken);
        }
        finally
        {
            RecordStage("embed", start);
        }
    }

    // Resolves each hit's display content: the hydrated base content without expansion; with expansion the whole top-k is
    // expanded in one batched call and neighbors join in chunk order, an empty neighbor set keeping the base content.
    private async Task<IReadOnlyList<string>> ResolveContentsAsync(IReadOnlyList<ChunkSelection> selections, bool expandNeighbors, CancellationToken cancellationToken)
    {
        if (!expandNeighbors || selections.Count == 0)
        {
            return selections.Select(static selection => selection.Row.Content).ToList();
        }

        var expandStart = Stopwatch.GetTimestamp();
        var anchors = selections.Select(static selection => new KnowledgeNeighborAnchor(selection.Row.DocumentId, selection.Row.ChunkIndex)).ToList();
        var expanded = await _contextExpansion.ExpandBatchAsync(anchors, NeighborWindow, cancellationToken);
        RecordStage("expand", expandStart);

        var contents = new List<string>(selections.Count);
        for (var index = 0; index < selections.Count; index++)
        {
            var neighbors = expanded[index];
            contents.Add(neighbors.Count == 0
                ? selections[index].Row.Content
                : string.Join(Environment.NewLine, neighbors.Select(neighbor => neighbor.Content)));
        }

        return contents;
    }

    private static void RecordStage(string stage, long startTimestamp)
    {
        NodeMetrics.KnowledgeSearchStageDurationMs.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("stage", stage));
    }

    // Hydrates the fused candidate POOL — bounded to candidatePool, NOT the whole fused list — in one batched query, in
    // fused order, stamping the RRF score; a chunk deleted or reindexed meanwhile is absent and skipped, order intact.
    private static async Task<List<ChunkSelection>> HydratePoolAsync(DbConnection connection,
        IReadOnlyList<RankFusionEntry> fused,
        int candidatePool,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var pooled = fused.Take(candidatePool).ToList();
        var hydrated = await HydrateChunksAsync(connection,
                pooled.Select(static entry => entry.ChunkId).ToList(),
                collectionId,
                cancellationToken);

        var pool = new List<ChunkSelection>(pooled.Count);
        foreach (var entry in pooled)
        {
            if (hydrated.TryGetValue(entry.ChunkId, out var row))
            {
                pool.Add(new ChunkSelection { ChunkId = entry.ChunkId, Row = row, Score = entry.Score });
            }
        }

        return pool;
    }

    // Drops candidates whose normalized content duplicates a higher-ranked one: the pool is in fused order, so the FIRST
    // (highest-RRF) occurrence wins. Content is whitespace-collapsed and lowercased before hashing, so casing never splits.
    private static List<ChunkSelection> DeduplicateByContent(IReadOnlyList<ChunkSelection> pool)
    {
        // seen.Add returns false for a content already kept, so the Where keeps only the first (highest-RRF-ranked)
        // occurrence of each distinct content — the side effect is the dedup.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return pool.Where(selection => seen.Add(ContentDedupHash(selection.Row.Content))).ToList();
    }

    private static string ContentDedupHash(string content)
    {
        // Upper-invariant (CA1308: the invariant upper-case round-trips reliably) after collapsing whitespace, so chunks
        // that differ only in incidental whitespace or case hash equal.
        var collapsed = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(collapsed)));
    }

    // Enabled-rerank path: sends the hydrated, deduped pool's base contents to the local reranker and reorders by descending
    // relevance before taking `limit`; a failure (null or count mismatch) keeps the original RRF order and score.
    private async Task<IReadOnlyList<ChunkSelection>> RerankAsync(string query,
        IReadOnlyList<ChunkSelection> pool,
        int limit,
        CancellationToken cancellationToken)
    {
        if (pool.Count == 0)
        {
            return [];
        }

        var documents = pool.Select(static candidate => candidate.Row.Content).ToList();
        var rerankStart = Stopwatch.GetTimestamp();
        var scores = await _reranker.RerankAsync(_options.RerankerModelName, query, documents, cancellationToken);
        RecordStage("rerank", rerankStart);
        if (scores is null || scores.Count != pool.Count)
        {
            // Reranker unavailable or malformed response: keep the RRF order + score, take the top-`limit`.
            return pool.Take(limit).ToList();
        }

        // Reorder the pool by descending rerank relevance, stamping the rerank score onto each surviving hit, then cut to
        // `limit`. OrderByDescending is a stable sort, so equal scores preserve the RRF tie-break order.
        return pool
               .Select((candidate, index) => candidate with
               {
                   Score = scores[index]
               })
               .OrderByDescending(static candidate => candidate.Score)
               .Take(limit)
               .ToList();
    }

    private async Task<IReadOnlyList<ChunkSelection>> RerankWithinBudgetAsync(string query,
        IReadOnlyList<ChunkSelection> pool,
        int limit,
        long searchStart,
        CancellationToken cancellationToken)
    {
        var totalBudget = TimeSpan.FromMilliseconds(Math.Max(1, _options.RetrievalLatencyBudgetMilliseconds));
        var remaining = totalBudget - Stopwatch.GetElapsedTime(searchStart);
        if (remaining <= TimeSpan.Zero)
        {
            return pool.Take(limit).ToList();
        }

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(remaining);
        try
        {
            // The linked deadline flows through reranker acquisition and scoring, winning over the provider's larger
            // internal timeout; WaitAsync bounds the caller even if a provider violates the cancellation contract.
            return await RerankAsync(query, pool, limit, budgetCts.Token)
                         .WaitAsync(remaining, cancellationToken);
        }
        catch (TimeoutException)
        {
            await budgetCts.CancelAsync();
            return pool.Take(limit).ToList();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budgetCts.IsCancellationRequested)
        {
            return pool.Take(limit).ToList();
        }
    }

    // Returns the query vector plus the resolved model name it was embedded with, the vector-search scope key. On the
    // degrade path the vector is empty and the name is the unused configured one, since the vector arm is then skipped.
    private async Task<QueryEmbedding> TryEmbedQueryAsync(string query,
        CancellationToken cancellationToken)
    {
        try
        {
            var provider = _providerResolver.ResolveProvider(_options.EmbeddingProviderName);

            // Resolve ONCE: the same name embeds the query AND filters the stored chunk vectors, which the ingestion lane
            // stamped with it. Confidence is irrelevant here — any embedding failure degrades search to lexical-only.
            var resolution = await _embeddingModelResolver.ResolveAsync(provider, cancellationToken);
            var embeddingModelName = resolution.Name;
            var cacheFamilyIdentity = KnowledgeEmbeddingVectorPolicy.CreateCacheFamilyIdentity(resolution, _options.EmbeddingVectorMode);
            if (_queryEmbeddingCache.TryGet(cacheFamilyIdentity, query, out var cached)
                && KnowledgeEmbeddingVectorPolicy.MatchesCurrentPolicy(cached.VectorIdentity,
                    cached.Vector.Length,
                    resolution,
                    _options.EmbeddingVectorMode))
            {
                return new QueryEmbedding(cached.Vector, embeddingModelName, cached.VectorIdentity);
            }

            using var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
            {
                ModelName = embeddingModelName,
                ProviderName = _options.EmbeddingProviderName
            });

            // Prefix with the query intent so an asymmetric embedding model builds a query vector, not a passage vector.
            var generated = await generator.GenerateAsync([_prefixer.ForQuery(query)], options: null, cancellationToken);
            if (generated.Count == 0)
            {
                return new QueryEmbedding(ReadOnlyMemory<float>.Empty, embeddingModelName, KnowledgeEmbeddingVectorPolicy.LegacyIdentity);
            }

            var transformed = KnowledgeEmbeddingVectorPolicy.Transform(resolution, generated[0].Vector, _options.EmbeddingVectorMode);

            // The pre-generation key isolates the resolved model and policy family. The value retains the exact canonical
            // identity (including native width), which the read path validates before accepting a hit.
            _queryEmbeddingCache.Store(cacheFamilyIdentity,
                query,
                new KnowledgeQueryEmbeddingCacheEntry { Vector = transformed.Values, VectorIdentity = transformed.Identity });
            return new QueryEmbedding(transformed.Values, embeddingModelName, transformed.Identity);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaUnavailableException or InvalidOperationException or KnowledgeIngestionException)
        {
            // Model not pulled / provider down / transport error / unregistered provider name. Degrade to lexical-only.
            // Log the exception type only — never its message, never the query.
            _logger.LogWarning("Knowledge search query embedding unavailable; returning lexical results only. Exception type: {ExceptionType}.",
                exception.GetType().Name);
            return new QueryEmbedding(ReadOnlyMemory<float>.Empty, _options.EmbeddingModelName, KnowledgeEmbeddingVectorPolicy.LegacyIdentity);
        }
    }

    // Hydrates a set of chunk ids in one query per batch, keyed by chunk id, so a large candidate pool never fans out into
    // N SELECTs. Ids missing to a concurrent delete or reindex are absent from the map; the caller re-imposes the order.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification =
            "The IN-clause is a fixed count of $idN placeholders generated from an internal candidate count; every chunk id is bound as a parameter and no value is concatenated into the command text.")]
    [SuppressMessage("Security Hotspot", "S2077:Formatting SQL queries is security-sensitive",
        Justification = "Only internally-generated $idN placeholder names are interpolated; every chunk id is a bound parameter, so no user input reaches the command text.")]
    private static async Task<IReadOnlyDictionary<Guid, HydratedChunk>> HydrateChunksAsync(DbConnection connection,
        IReadOnlyList<Guid> chunkIds,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var hydrated = new Dictionary<Guid, HydratedChunk>(chunkIds.Count);
        if (chunkIds.Count == 0)
        {
            RecordStage("hydrate", start);
            return hydrated;
        }

        // SQLite caps host parameters per statement (at least 999 on every supported build); batch the IN-list well under
        // that so even a large candidate pool hydrates in a bounded number of statements rather than one per chunk.
        const int batchSize = 500;
        for (var offset = 0; offset < chunkIds.Count; offset += batchSize)
        {
            var count = Math.Min(batchSize, chunkIds.Count - offset);
            await using var command = connection.CreateCommand();
            var placeholders = new string[count];
            for (var i = 0; i < count; i++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"$id{i}");
                placeholders[i] = name;
                AddParameter(command, name, chunkIds[offset + i]);
            }

            command.CommandText = $"""
                                   SELECT c.chunk_id, c.document_id, c.chunk_index, c.content, c.heading_path,
                                          d.storage_path, d.status, d.vector_identity, d.collection_id,
                                          c.source_path, c.content_kind, c.language, c.symbol, c.page_number,
                                          c.start_offset, c.end_offset
                                   FROM knowledge_document_chunks c
                                   JOIN knowledge_documents d ON d.document_id = c.document_id
                                   WHERE d.collection_id = $collection_id
                                     AND c.chunk_id IN ({string.Join(", ", placeholders)});
                                   """;
            AddParameter(command, "$collection_id", collectionId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var chunkId = Guid.Parse(reader.GetString(0));
                var headingPath = await reader.IsDBNullAsync(ordinal: 4, cancellationToken)
                    ? null
                    : reader.GetString(4);
                hydrated[chunkId] = new HydratedChunk
                {
                    DocumentId = Guid.Parse(reader.GetString(1)),
                    ChunkIndex = reader.GetInt32(2),
                    Content = reader.GetString(3),
                    HeadingPath = headingPath,
                    StoragePath = reader.GetString(5),
                    DocumentStatus = ParseDocumentStatus(reader.GetString(6)),
                    VectorIdentity = reader.GetString(7),
                    CollectionId = reader.GetString(8),
                    SourcePath = await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9),
                    ContentKind = reader.GetString(10),
                    Language = await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetString(11),
                    Symbol = await reader.IsDBNullAsync(12, cancellationToken) ? null : reader.GetString(12),
                    PageNumber = await reader.IsDBNullAsync(13, cancellationToken) ? null : reader.GetInt32(13),
                    StartOffset = reader.GetInt32(14),
                    EndOffset = reader.GetInt32(15)
                };
            }
        }

        RecordStage("hydrate", start);
        return hydrated;
    }

    // The status column always holds a KnowledgeDocumentStatus name (written by the ingestion pipeline). If a row
    // somehow carries an unrecognized value, fail SAFE toward disclosure by treating it as a non-Indexed state.
    private static KnowledgeDocumentStatus ParseDocumentStatus(string status)
    {
        return Enum.TryParse<KnowledgeDocumentStatus>(status, out var parsed)
            ? parsed
            : KnowledgeDocumentStatus.Pending;
    }

    // Non-sensitive display title: the encrypted original file name must never leak into a search result, so this is the
    // heading trail's root segment, else the server-generated storage reference, which carries no user content.
    private static string DeriveTitle(string? headingPath, string storagePath)
    {
        if (string.IsNullOrWhiteSpace(headingPath))
        {
            return storagePath;
        }

        var root = headingPath.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return root.Length > 0 ? root[0] : storagePath;
    }

    private sealed record HydratedChunk
    {
        public required Guid DocumentId { get; init; }

        public required int ChunkIndex { get; init; }

        public required string Content { get; init; }

        public required string? HeadingPath { get; init; }

        public required string StoragePath { get; init; }

        public required KnowledgeDocumentStatus DocumentStatus { get; init; }

        public required string VectorIdentity { get; init; }

        public required string CollectionId { get; init; }

        public required string? SourcePath { get; init; }

        public required string ContentKind { get; init; }

        public required string? Language { get; init; }

        public required string? Symbol { get; init; }

        public required int? PageNumber { get; init; }

        public required int StartOffset { get; init; }

        public required int EndOffset { get; init; }
    }

    // One selected candidate carried from ranking to hit-building: the chunk id, its hydrated row, and the score to
    // stamp on the hit (the RRF score on the fusion/degrade paths, the rerank relevance on the reranked path).
    private sealed record ChunkSelection
    {
        public required Guid ChunkId { get; init; }

        public required HydratedChunk Row { get; init; }

        public required double Score { get; init; }
    }

    // The embedded query the semantic arm searches with: the vector (empty on the degrade path, which skips the arm), the
    // resolved model name scoping which stored vectors it may meet, and the identity pinning the policy it was built under.
    private sealed record QueryEmbedding(ReadOnlyMemory<float> Vector, string ResolvedModel, string VectorIdentity);
}
