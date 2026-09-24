namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IFtsSearch" />. Queries the <c>chunk_fts</c> FTS5 external-content index with a BM25-ranked
///     <c>MATCH</c> over the raw-SQL path. Scoped: depends on the request-scoped <see cref="NodeChatDbContext" />.
/// </summary>
public sealed class FtsSearch : IFtsSearch
{
    private readonly NodeChatDbContext _dbContext;

    public FtsSearch(NodeChatDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<FtsSearchHit>> SearchAsync(string query, int limit, Guid? documentId, CancellationToken cancellationToken)
    {
        return await SearchAsync(query, limit, documentId, KnowledgeCollectionScope.DefaultId, cancellationToken);
    }

    public async Task<IReadOnlyList<FtsSearchHit>> SearchAsync(string query,
        int limit,
        Guid? documentId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        if (!KnowledgeCollectionScope.TryNormalize(collectionId, out var normalizedCollectionId))
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        // Identifiers are UNINDEXED and title-bearing metadata carries a larger BM25 weight than body text; the document
        // join applies the dense arm's collection boundary before ranking, so no cross-project candidate enters fusion.
        command.CommandText = """
                              SELECT chunk_fts.chunk_id, chunk_fts.document_id,
                                     bm25(chunk_fts, 0.0, 0.0, 6.0, 3.0, 8.0, 1.0) AS score
                              FROM chunk_fts
                              JOIN knowledge_documents AS d ON d.document_id = chunk_fts.document_id
                              WHERE chunk_fts MATCH $match
                                AND d.collection_id = $collection_id
                                AND ($document_id IS NULL OR chunk_fts.document_id = $document_id)
                              ORDER BY score ASC
                              LIMIT $limit;
                              """;

        AddParameter(command, "$match", EscapeMatchQuery(query));
        AddParameter(command, "$collection_id", normalizedCollectionId);
        AddParameter(command, "$document_id", documentId);
        AddParameter(command, "$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var hits = new List<FtsSearchHit>();
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new FtsSearchHit
            {
                ChunkId = Guid.Parse(reader.GetString(0)),
                DocumentId = Guid.Parse(reader.GetString(1)),
                Bm25Score = reader.GetDouble(2)
            });
        }

        return hits;
    }

    /// <summary>Escapes an untrusted search string into a safe FTS5 <c>MATCH</c> expression.</summary>
    /// <remarks>
    ///     Splits on whitespace, wraps each token in double quotes with embedded quotes doubled, and joins them with <c>OR</c>.
    ///     Per-token quoting makes operator characters (<c>- * : ( ) " ^</c>) and bare keywords (<c>OR AND NOT NEAR</c>) ordinary
    ///     text, so input can never inject query syntax or trigger a MATCH parse error. <c>OR</c> rather than implicit
    ///     <c>AND</c> keeps recall high for RRF fusion: any matching term surfaces the document, while BM25 still ranks
    ///     documents matching more terms higher. Empty or whitespace-only input yields an empty quoted phrase matching no rows.
    /// </remarks>
    public static string EscapeMatchQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            // An empty MATCH string is a parse error, so emit an empty quoted phrase: valid syntax that matches nothing.
            return "\"\"";
        }

        return string.Join(" OR ",
            tokens.Select(static token => string.Concat("\"", token.Replace("\"", "\"\"", StringComparison.Ordinal), "\"")));
    }
}
