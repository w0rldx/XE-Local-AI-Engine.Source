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
    ///     Escapes an untrusted search string into a single safe FTS5 <c>MATCH</c> term. The whole string is wrapped in
    ///     double quotes and every embedded double quote is doubled, so FTS5 reads it as one quoted string literal (a
    ///     phrase). Operator characters (<c>- * : ( ) " ^</c>) inside the string are therefore treated as ordinary text
    ///     and can never inject query syntax or trigger a MATCH parse error. Example: the input <c>foo "bar</c> becomes
    ///     <c>"foo ""bar"</c>.
    /// </summary>
    public static string EscapeMatchQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return string.Concat("\"", query.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
    }
}
