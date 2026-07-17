namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OllamaSharp.Models.Exceptions;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IKnowledgeSearchService" />. Embeds the query with the current model (query-intent prefix),
///     retrieves candidates from the lexical FTS arm and the model-scoped semantic vector arm, fuses their rankings with
///     Reciprocal Rank Fusion, optionally rescoring the fused candidate pool with a local cross-encoder reranker
///     (<see cref="KnowledgeBaseOptions.RerankerModelName" />) before the top-<c>limit</c> cut, hydrates the selected
///     chunks over the raw-SQL path, and optionally expands each hit with its surrounding neighbors. If the embedding
///     model or the reranker is unavailable the search degrades gracefully (lexical-only / fusion order) rather than
///     failing. No query or chunk text is ever logged. Scoped: it drives the scoped retrieval collaborators through the
///     request-scoped <see cref="NodeChatDbContext" />.
/// </summary>
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
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new KnowledgeSearchResult([]);
        }

        var limit = Math.Max(1, request.Limit);
        var candidatePool = Math.Max(MinimumCandidatePool, limit * CandidatePoolMultiplier);

        // The two retrieval arms are launched together. The lexical FTS arm reads the request-scoped DB connection; the
        // query-embedding arm calls only the embedding provider process and never touches that connection — so overlapping
        // them can never run two commands on the non-thread-safe SQLite connection at once. Every DB-bound read that
        // CONSUMES the embedding (the vector scan, hydration, expansion) runs sequentially after this point, still on the
        // one shared connection. Overlapping the embedding round trip (typically the dominant latency) with the FTS query
        // is the win here; truly concurrent execution of BOTH DB arms would need a second connection/scope and is
        // deliberately not taken. The vector arm is filtered by the SAME resolved model name the query was embedded with,
        // so query vectors are only ever compared against chunk vectors built by that identical model.
        var ftsArm = RunFtsArmAsync(request.Query, candidatePool, request.DocumentId, cancellationToken);
        var embedArm = RunEmbedArmAsync(request.Query, cancellationToken);
        await Task.WhenAll(ftsArm, embedArm).ConfigureAwait(false);

        var ftsRanked = (await ftsArm.ConfigureAwait(false)).Select(hit => hit.ChunkId).ToList();
        var (queryVector, resolvedModel) = await embedArm.ConfigureAwait(false);

        var vectorRanked = new List<Guid>();
        if (!queryVector.IsEmpty)
        {
            var vectorStart = Stopwatch.GetTimestamp();
            var vectorHits = await _vectorSearchFactory.Create()
                                                       .SearchAsync(queryVector, resolvedModel, candidatePool, request.DocumentId, cancellationToken)
                                                       .ConfigureAwait(false);
            RecordStage("vector", vectorStart);
            vectorRanked = vectorHits.Select(hit => hit.ChunkId).ToList();
        }

        var fused = _fusion.Fuse([ftsRanked, vectorRanked]);
        if (fused.Count == 0)
        {
            return new KnowledgeSearchResult([]);
        }

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        // Hydrate the fused candidate POOL once (bounded to candidatePool, in one batched query) in fused order, then drop
        // content duplicates BEFORE any top-`limit` cut so near-identical chunks stored under different ids do not crowd
        // out distinct results. The higher-RRF-ranked occurrence of a duplicate is kept (the pool is in fused order), so
        // the dedup is deterministic.
        var pool = await HydratePoolAsync(connection, fused, candidatePool, cancellationToken).ConfigureAwait(false);
        var deduped = DeduplicateByContent(pool);

        // Optional rerank stage: when a reranker model is configured, the deduped pool is rescored by a local cross-encoder
        // and reordered BEFORE the top-`limit` cut, so a strong-but-lexically-weak chunk can be pulled into the results.
        // When reranking is off — or on ANY rerank failure — the behavior is the exact original: RRF order, Take(limit).
        // Reranking scores the BASE chunk content (pre-expansion); neighbor expansion is applied only to the final top-k.
        var selections = string.IsNullOrWhiteSpace(_options.RerankerModelName)
            ? deduped.Take(limit).ToList()
            : await RerankAsync(request.Query, deduped, limit, cancellationToken).ConfigureAwait(false);

        // Neighbor expansion (when requested) is resolved for the whole final top-k in one batched call rather than one
        // round trip per hit; the content ordering and fallback are identical to expanding each hit individually.
        var contents = await ResolveContentsAsync(selections, request.ExpandNeighbors, cancellationToken).ConfigureAwait(false);

        var hits = new List<KnowledgeSearchHit>(selections.Count);
        for (var index = 0; index < selections.Count; index++)
        {
            var selection = selections[index];

            // A hit only exists because the document has queryable chunks; when its catalog status is not Indexed
            // those chunks are the last-known-good projection served during a pending/failed re-index. Disclose it.
            var servingLastKnownGood = selection.Row.DocumentStatus != KnowledgeDocumentStatus.Indexed;

            hits.Add(new KnowledgeSearchHit(selection.Row.DocumentId,
                selection.ChunkId,
                DeriveTitle(selection.Row.HeadingPath, selection.Row.StoragePath),
                selection.Row.HeadingPath,
                contents[index],
                SourceTag,
                selection.Score,
                selection.Row.ChunkIndex,
                selection.Row.DocumentStatus,
                servingLastKnownGood));
        }

        return new KnowledgeSearchResult(hits);
    }

    // Lexical arm wrapper: times the FTS round trip. Reads the request-scoped DB connection (so it never overlaps another
    // DB command — the embedding arm it runs beside touches only the provider process).
    private async Task<IReadOnlyList<FtsSearchHit>> RunFtsArmAsync(string query, int candidatePool, Guid? documentId, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await _ftsSearch.SearchAsync(query, candidatePool, documentId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RecordStage("fts", start);
        }
    }

    // Semantic arm wrapper: times the query-embedding round trip. Only calls the embedding provider — never the DB — so it
    // is safe to overlap with the lexical arm above.
    private async Task<(ReadOnlyMemory<float> Vector, string ResolvedModel)> RunEmbedArmAsync(string query, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await TryEmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RecordStage("embed", start);
        }
    }

    // Resolves the display content for each selected hit. Without expansion this is the hydrated base content; with
    // expansion the whole top-k is expanded in one batched call and each hit's neighbors are joined in chunk order (an
    // empty neighbor set — e.g. the chunk vanished between selection and expansion — falls back to the base content, the
    // same behavior as expanding a single hit).
    private async Task<IReadOnlyList<string>> ResolveContentsAsync(IReadOnlyList<ChunkSelection> selections, bool expandNeighbors, CancellationToken cancellationToken)
    {
        if (!expandNeighbors || selections.Count == 0)
        {
            return selections.Select(static selection => selection.Row.Content).ToList();
        }

        var expandStart = Stopwatch.GetTimestamp();
        var anchors = selections.Select(static selection => new KnowledgeNeighborAnchor(selection.Row.DocumentId, selection.Row.ChunkIndex)).ToList();
        var expanded = await _contextExpansion.ExpandBatchAsync(anchors, NeighborWindow, cancellationToken).ConfigureAwait(false);
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

    // Hydrates the fused candidate POOL (bounded to candidatePool — NOT the whole fused list) in one batched query, in
    // fused order, stamping the RRF score. Hydration is one batched query for the whole set (not a round trip per chunk); a
    // chunk that disappeared between retrieval and hydration (concurrent delete/reindex) is simply absent from the batch
    // and skipped, and the surviving entries keep their fused order.
    private static async Task<List<ChunkSelection>> HydratePoolAsync(DbConnection connection,
        IReadOnlyList<RankFusionEntry> fused,
        int candidatePool,
        CancellationToken cancellationToken)
    {
        var pooled = fused.Take(candidatePool).ToList();
        var hydrated = await HydrateChunksAsync(connection, pooled.Select(static entry => entry.ChunkId).ToList(), cancellationToken).ConfigureAwait(false);

        var pool = new List<ChunkSelection>(pooled.Count);
        foreach (var entry in pooled)
        {
            if (hydrated.TryGetValue(entry.ChunkId, out var row))
            {
                pool.Add(new ChunkSelection(entry.ChunkId, row, entry.Score));
            }
        }

        return pool;
    }

    // Drops candidates whose normalized content duplicates a higher-ranked candidate. The pool is in fused order, so the
    // FIRST occurrence of a given content (the highest-RRF-ranked) is kept and later duplicates are dropped — deterministic
    // given RRF's deterministic order. Content is normalized (whitespace collapsed, lowercased) before hashing so chunks
    // that differ only in incidental whitespace/case are treated as duplicates.
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

    // Enabled-rerank path: send the base chunk contents of the (already hydrated + deduped) pool to the local reranker and
    // reorder by descending relevance before taking `limit`. On any rerank failure (null / count mismatch) the pool is kept
    // in its original RRF order with the RRF score, so the result is never worse than the disabled path.
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
        var scores = await _reranker.RerankAsync(_options.RerankerModelName, query, documents, cancellationToken).ConfigureAwait(false);
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

    // Returns the query vector plus the resolved model name it was embedded with (the vector-search scope key). On the
    // degrade path the vector is empty and the resolved name falls back to the configured name (unused, since the vector
    // arm is skipped when the vector is empty).
    private async Task<(ReadOnlyMemory<float> Vector, string ResolvedModel)> TryEmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var provider = _providerResolver.ResolveProvider(_options.EmbeddingProviderName);

            // Resolve ONCE. The same resolved name embeds the query AND filters the stored chunk vectors, so query and
            // chunk vectors are only ever compared when the identical model produced both (the ingestion lane stamps the
            // same resolved name as the scope key). A later same-dimension model swap changes this name and excludes the
            // now-incompatible old vectors instead of silently mis-comparing them. The confidence bit is irrelevant here —
            // search degrades to lexical-only on any embedding failure regardless of why the name is what it is.
            var embeddingModelName = (await _embeddingModelResolver.ResolveAsync(provider, cancellationToken).ConfigureAwait(false)).Name;

            // Cache hit: skip the embedding round trip (typically the dominant retrieval latency). The key is (resolved
            // model, query), so a same-dimension model swap changes the resolved name → a different key → the stale
            // cross-model vector is never returned. RAM-only + bounded + TTL'd; the raw query text is not retained (hashed).
            if (_queryEmbeddingCache.TryGet(embeddingModelName, query, out var cachedVector))
            {
                return (cachedVector, embeddingModelName);
            }

            using var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
            {
                ModelName = embeddingModelName,
                ProviderName = _options.EmbeddingProviderName
            });

            // Prefix with the query intent so an asymmetric embedding model builds a query vector, not a passage vector.
            var generated = await generator.GenerateAsync([_prefixer.ForQuery(query)], options: null, cancellationToken).ConfigureAwait(false);
            if (generated.Count == 0)
            {
                return (ReadOnlyMemory<float>.Empty, embeddingModelName);
            }

            // No static dimension check here: the query is embedded by the SAME resolved model that keys the stored chunk
            // vectors, and ManagedCosineVectorSearch skips any candidate whose width differs from the query (defense in
            // depth). A model with a non-768 native width therefore searches correctly instead of silently degrading.
            var queryVector = generated[0].Vector;
            _queryEmbeddingCache.Store(embeddingModelName, query, queryVector);
            return (queryVector, embeddingModelName);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaException or InvalidOperationException)
        {
            // Model not pulled / provider down / transport error / unregistered provider name. Degrade to lexical-only.
            // Log the exception type only — never its message, never the query.
            _logger.LogWarning("Knowledge search query embedding unavailable; returning lexical results only. Exception type: {ExceptionType}.",
                exception.GetType().Name);
            return (ReadOnlyMemory<float>.Empty, _options.EmbeddingModelName);
        }
    }

    // Hydrates a set of chunk ids in one query per batch (keyed by chunk id) instead of one round trip per chunk, so a
    // large candidate pool no longer fans out into N SELECTs. Missing ids (concurrent delete/reindex) are simply absent
    // from the returned map; the caller re-imposes the fused/rerank order and skips absentees.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification =
            "The IN-clause is a fixed count of $idN placeholders generated from an internal candidate count; every chunk id is bound as a parameter and no value is concatenated into the command text.")]
    [SuppressMessage("Security Hotspot", "S2077:Formatting SQL queries is security-sensitive",
        Justification = "Only internally-generated $idN placeholder names are interpolated; every chunk id is a bound parameter, so no user input reaches the command text.")]
    private static async Task<IReadOnlyDictionary<Guid, HydratedChunk>> HydrateChunksAsync(DbConnection connection,
        IReadOnlyList<Guid> chunkIds,
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
                                   SELECT c.chunk_id, c.document_id, c.chunk_index, c.content, c.heading_path, d.storage_path, d.status
                                   FROM knowledge_document_chunks c
                                   JOIN knowledge_documents d ON d.document_id = c.document_id
                                   WHERE c.chunk_id IN ({string.Join(", ", placeholders)});
                                   """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var chunkId = Guid.Parse(reader.GetString(0));
                var headingPath = await reader.IsDBNullAsync(ordinal: 4, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(4);
                hydrated[chunkId] = new HydratedChunk(Guid.Parse(reader.GetString(1)),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    headingPath,
                    reader.GetString(5),
                    ParseDocumentStatus(reader.GetString(6)));
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

    // Non-sensitive display title. The original file name is encrypted and must never leak into a search result, so the
    // title is the root segment of the heading trail when present, else the server-generated storage reference (the
    // document id plus its extension), which carries no user content.
    private static string DeriveTitle(string? headingPath, string storagePath)
    {
        if (string.IsNullOrWhiteSpace(headingPath))
        {
            return storagePath;
        }

        var root = headingPath.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return root.Length > 0 ? root[0] : storagePath;
    }

    private sealed record HydratedChunk(Guid DocumentId, int ChunkIndex, string Content, string? HeadingPath, string StoragePath, KnowledgeDocumentStatus DocumentStatus);

    // One selected candidate carried from ranking to hit-building: the chunk id, its hydrated row, and the score to
    // stamp on the hit (the RRF score on the fusion/degrade paths, the rerank relevance on the reranked path).
    private sealed record ChunkSelection(Guid ChunkId, HydratedChunk Row, double Score);
}
