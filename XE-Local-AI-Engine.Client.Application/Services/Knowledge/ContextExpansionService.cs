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

    // Total chunk rows the last ExpandBatchAsync call actually hydrated from the database. Test-only seam
    // (internal + InternalsVisibleTo) that lets a test assert hydration is bounded to the union of the anchors' windows
    // rather than the min-to-max span across distant hits; not part of the public contract.
    internal int LastBatchRowsHydrated { get; private set; }

    public async Task<IReadOnlyList<IReadOnlyList<KnowledgeNeighborChunk>>> ExpandBatchAsync(IReadOnlyList<KnowledgeNeighborAnchor> anchors,
        int window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        LastBatchRowsHydrated = 0;
        if (anchors.Count == 0)
        {
            return [];
        }

        var safeWindow = Math.Max(0, window);

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        // Per document, merge only OVERLAPPING or ADJACENT anchor windows into DISJOINT ranges and read each range with its
        // own bounded query — never a single min-to-max span, so two far-apart hits in one document never drag in the
        // intervening chunks. Each anchor is then sliced from the one merged range that fully contains its window, so the
        // per-anchor output is byte-for-byte what ExpandAsync(anchor) returns.
        var rangesByDocument = new Dictionary<Guid, List<HydratedRange>>();
        foreach (var group in anchors.GroupBy(static anchor => anchor.DocumentId))
        {
            var merged = MergeWindows(group.Select(anchor => (Lower: anchor.ChunkIndex - safeWindow, Upper: anchor.ChunkIndex + safeWindow)));
            var hydrated = new List<HydratedRange>(merged.Count);
            foreach (var (lower, upper) in merged)
            {
                var rows = await ReadRangeAsync(connection, group.Key, lower, upper, cancellationToken).ConfigureAwait(false);
                LastBatchRowsHydrated += rows.Count;
                hydrated.Add(new HydratedRange(lower, upper, rows));
            }

            rangesByDocument[group.Key] = hydrated;
        }

        var results = new List<IReadOnlyList<KnowledgeNeighborChunk>>(anchors.Count);
        foreach (var anchor in anchors)
        {
            var lower = anchor.ChunkIndex - safeWindow;
            var upper = anchor.ChunkIndex + safeWindow;
            // Exactly one merged range fully contains this anchor's window (by construction); slice it out. Its rows are
            // already ascending by chunk_index.
            var owningRange = rangesByDocument[anchor.DocumentId].First(range => lower >= range.Lower && upper <= range.Upper);
            var anchorWindow = owningRange.Rows
                                          .Where(chunk => chunk.ChunkIndex >= lower && chunk.ChunkIndex <= upper)
                                          .ToList();
            results.Add(anchorWindow);
        }

        return results;
    }

    // Merges overlapping or adjacent (touching, i.e. no unindexed gap) windows into disjoint ascending ranges. A window
    // separated from the previous one by even a single unindexed position starts a new range, so a distant anchor never
    // widens an earlier range across the gap between them.
    private static List<(int Lower, int Upper)> MergeWindows(IEnumerable<(int Lower, int Upper)> windows)
    {
        var merged = new List<(int Lower, int Upper)>();
        foreach (var (lower, upper) in windows.OrderBy(static window => window.Lower))
        {
            if (merged.Count > 0 && lower <= merged[^1].Upper + 1)
            {
                merged[^1] = (merged[^1].Lower, Math.Max(merged[^1].Upper, upper));
            }
            else
            {
                merged.Add((lower, upper));
            }
        }

        return merged;
    }

    private sealed record HydratedRange(int Lower, int Upper, IReadOnlyList<KnowledgeNeighborChunk> Rows);

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
