namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IContextExpansionService" />. Reads the neighbor chunks of a match from
///     <c>knowledge_document_chunks</c> over the raw-SQL path, bounded to a <c>chunk_index</c> window in the same document.
///     Scoped: depends on the request-scoped <see cref="NodeChatDbContext" />.
/// </summary>
public sealed class ContextExpansionService : IContextExpansionService
{
    private readonly NodeChatDbContext _dbContext;

    public ContextExpansionService(NodeChatDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<KnowledgeNeighborChunk>> ExpandAsync(Guid documentId,
        int chunkIndex,
        int window,
        CancellationToken cancellationToken)
    {
        var safeWindow = Math.Max(0, window);
        var lowerBound = chunkIndex - safeWindow;
        var upperBound = chunkIndex + safeWindow;

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT chunk_id, chunk_index, content, heading_path
                              FROM knowledge_document_chunks
                              WHERE document_id = $document_id AND chunk_index BETWEEN $lower AND $upper
                              ORDER BY chunk_index ASC;
                              """;
        AddParameter(command, "$document_id", documentId);
        AddParameter(command, "$lower", lowerBound);
        AddParameter(command, "$upper", upperBound);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var neighbors = new List<KnowledgeNeighborChunk>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var headingPath = await reader.IsDBNullAsync(ordinal: 3, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(3);
            neighbors.Add(new KnowledgeNeighborChunk(Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetString(2),
                headingPath));
        }

        return neighbors;
    }
}
