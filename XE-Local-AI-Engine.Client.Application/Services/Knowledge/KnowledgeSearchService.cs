namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OllamaSharp.Models.Exceptions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IKnowledgeSearchService" />. Embeds the query with the current model (query-intent prefix),
///     retrieves candidates from the lexical FTS arm and the model-scoped semantic vector arm, fuses their rankings with
///     Reciprocal Rank Fusion, hydrates the top chunks over the raw-SQL path, and optionally expands each hit with its
///     surrounding neighbors. If the embedding model is unavailable the search degrades to lexical-only results rather than
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
    private readonly IContextExpansionService _contextExpansion;
    private readonly KnowledgeBaseOptions _options;
    private readonly ILogger<KnowledgeSearchService> _logger;

    public KnowledgeSearchService(NodeChatDbContext dbContext,
        ILocalModelProviderResolver providerResolver,
        IEmbeddingModelResolver embeddingModelResolver,
        IKnowledgeEmbeddingPrefixer prefixer,
        IFtsSearch ftsSearch,
        IVectorSearchFactory vectorSearchFactory,
        IRankingFusionService fusion,
        IContextExpansionService contextExpansion,
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
        _contextExpansion = contextExpansion ?? throw new ArgumentNullException(nameof(contextExpansion));
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

        // Lexical arm: the document scope is pushed into the FTS MATCH query itself, matching the vector arm below.
        var ftsHits = await _ftsSearch.SearchAsync(request.Query, candidatePool, request.DocumentId, cancellationToken).ConfigureAwait(false);
        var ftsRanked = ftsHits.Select(hit => hit.ChunkId).ToList();

        // Semantic arm: only runs when the query vector is available; otherwise the search degrades to lexical-only. The
        // vector arm is filtered by the SAME resolved model name the query was embedded with, so query vectors are only
        // ever compared against chunk vectors built by that identical model (the ingestion lane stamps the same key).
        var (queryVector, resolvedModel) = await TryEmbedQueryAsync(request.Query, cancellationToken).ConfigureAwait(false);
        var vectorRanked = new List<Guid>();
        if (!queryVector.IsEmpty)
        {
            var vectorHits = await _vectorSearchFactory.Create()
                .SearchAsync(queryVector, resolvedModel, candidatePool, request.DocumentId, cancellationToken)
                .ConfigureAwait(false);
            vectorRanked = vectorHits.Select(hit => hit.ChunkId).ToList();
        }

        var fused = _fusion.Fuse([ftsRanked, vectorRanked]);
        if (fused.Count == 0)
        {
            return new KnowledgeSearchResult([]);
        }

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        var hits = new List<KnowledgeSearchHit>(Math.Min(limit, fused.Count));
        foreach (var entry in fused.Take(limit))
        {
            var row = await HydrateChunkAsync(connection, entry.ChunkId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                // The chunk disappeared between retrieval and hydration (concurrent delete/reindex); skip it.
                continue;
            }

            var content = request.ExpandNeighbors
                ? await ExpandContentAsync(row.DocumentId, row.ChunkIndex, row.Content, cancellationToken).ConfigureAwait(false)
                : row.Content;

            hits.Add(new KnowledgeSearchHit(row.DocumentId,
                entry.ChunkId,
                DeriveTitle(row.HeadingPath, row.StoragePath),
                row.HeadingPath,
                content,
                SourceTag,
                entry.Score,
                row.ChunkIndex));
        }

        return new KnowledgeSearchResult(hits);
    }

    private async Task<string> ExpandContentAsync(Guid documentId, int chunkIndex, string matchedContent, CancellationToken cancellationToken)
    {
        var neighbors = await _contextExpansion.ExpandAsync(documentId, chunkIndex, NeighborWindow, cancellationToken).ConfigureAwait(false);
        if (neighbors.Count == 0)
        {
            return matchedContent;
        }

        return string.Join(Environment.NewLine, neighbors.Select(neighbor => neighbor.Content));
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

            var vector = generated[0].Vector;
            if (vector.Length != _options.EmbeddingDimension)
            {
                // A wrong-dimension query vector cannot be compared to the model-scoped chunk vectors; drop the vector arm.
                return (ReadOnlyMemory<float>.Empty, embeddingModelName);
            }

            return (vector, embeddingModelName);
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

    private static async Task<HydratedChunk?> HydrateChunkAsync(DbConnection connection, Guid chunkId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT c.document_id, c.chunk_index, c.content, c.heading_path, d.storage_path
                              FROM knowledge_document_chunks c
                              JOIN knowledge_documents d ON d.document_id = c.document_id
                              WHERE c.chunk_id = $chunk_id;
                              """;
        AddParameter(command, "$chunk_id", chunkId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var headingPath = await reader.IsDBNullAsync(ordinal: 3, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(3);
        return new HydratedChunk(Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetString(2),
            headingPath,
            reader.GetString(4));
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

    private sealed record HydratedChunk(Guid DocumentId, int ChunkIndex, string Content, string? HeadingPath, string StoragePath);
}
