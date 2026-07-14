namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
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

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        return await ReadRangeAsync(connection, documentId, chunkIndex - safeWindow, chunkIndex + safeWindow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IReadOnlyList<KnowledgeNeighborChunk>>> ExpandBatchAsync(IReadOnlyList<KnowledgeNeighborAnchor> anchors,
        int window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        if (anchors.Count == 0)
        {
            return [];
        }

        var safeWindow = Math.Max(0, window);

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        // One range read per DISTINCT document (spanning the merged window of every anchor in that document), then each
        // anchor is sliced from its document's rows in memory. This yields the exact rows-and-order ExpandAsync would per
        // anchor, but collapses per-hit round trips to per-document ones.
        var documentRanges = new Dictionary<Guid, (int Lower, int Upper)>();
        foreach (var anchor in anchors)
        {
            var lower = anchor.ChunkIndex - safeWindow;
            var upper = anchor.ChunkIndex + safeWindow;
            documentRanges[anchor.DocumentId] = documentRanges.TryGetValue(anchor.DocumentId, out var existing)
                ? (Math.Min(existing.Lower, lower), Math.Max(existing.Upper, upper))
                : (lower, upper);
        }

        var rowsByDocument = new Dictionary<Guid, IReadOnlyList<KnowledgeNeighborChunk>>(documentRanges.Count);
        foreach (var (documentId, range) in documentRanges)
        {
            rowsByDocument[documentId] = await ReadRangeAsync(connection, documentId, range.Lower, range.Upper, cancellationToken).ConfigureAwait(false);
        }

        var results = new List<IReadOnlyList<KnowledgeNeighborChunk>>(anchors.Count);
        foreach (var anchor in anchors)
        {
            var lower = anchor.ChunkIndex - safeWindow;
            var upper = anchor.ChunkIndex + safeWindow;
            // The document's rows are already ascending by chunk_index; slice this anchor's window out of them so the
            // result is byte-for-byte what ExpandAsync(anchor) returns.
            var anchorWindow = rowsByDocument[anchor.DocumentId]
                               .Where(chunk => chunk.ChunkIndex >= lower && chunk.ChunkIndex <= upper)
                               .ToList();
            results.Add(anchorWindow);
        }

        return results;
    }

    private static async Task<IReadOnlyList<KnowledgeNeighborChunk>> ReadRangeAsync(DbConnection connection,
        Guid documentId,
        int lowerBound,
        int upperBound,
        CancellationToken cancellationToken)
    {
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
