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
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // Order by the BM25 score ascending: FTS5 returns more-negative scores for stronger matches, so ascending yields
        // the strongest matches first. The chunk and document identifiers are stored as GUID text. chunk_fts.document_id
        // is an available UNINDEXED column, so a document-scoped search can filter in the same MATCH query rather than
        // post-filtering in memory. Two separate string-literal CommandText assignments (never concatenation, which trips
        // CA2100) mirror the pattern in ManagedCosineVectorSearch.
        if (documentId is null)
        {
            command.CommandText = """
                                  SELECT chunk_id, document_id, bm25(chunk_fts) AS score
                                  FROM chunk_fts
                                  WHERE chunk_fts MATCH $match
                                  ORDER BY score ASC
                                  LIMIT $limit;
                                  """;
        }
        else
        {
            command.CommandText = """
                                  SELECT chunk_id, document_id, bm25(chunk_fts) AS score
                                  FROM chunk_fts
                                  WHERE chunk_fts MATCH $match AND document_id = $document_id
                                  ORDER BY score ASC
                                  LIMIT $limit;
                                  """;
        }

        AddParameter(command, "$match", EscapeMatchQuery(query));
        AddParameter(command, "$limit", limit);
        if (documentId is not null)
        {
            AddParameter(command, "$document_id", documentId.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var hits = new List<FtsSearchHit>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hits.Add(new FtsSearchHit(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetDouble(2)));
        }

        return hits;
    }

    /// <summary>
    ///     Escapes an untrusted search string into a safe FTS5 <c>MATCH</c> expression. The string is split on whitespace
    ///     and each token is wrapped in double quotes (with embedded double quotes doubled), then the quoted tokens are
    ///     joined with the <c>OR</c> operator. Quoting each token individually makes operator characters
    ///     (<c>- * : ( ) " ^</c>) and bare keywords (<c>OR AND NOT NEAR</c>) inside a token ordinary text, so the input
    ///     can never inject query syntax or trigger a MATCH parse error. <c>OR</c> (rather than implicit <c>AND</c>) keeps
    ///     recall high for RRF fusion: a document matching any term still surfaces, while BM25 continues to rank documents
    ///     that match more terms higher. A whitespace-only or empty input yields an empty quoted phrase, which is valid
    ///     FTS5 syntax that matches no rows. Example: <c>embedding model config</c> becomes
    ///     <c>"embedding" OR "model" OR "config"</c>.
    /// </summary>
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
