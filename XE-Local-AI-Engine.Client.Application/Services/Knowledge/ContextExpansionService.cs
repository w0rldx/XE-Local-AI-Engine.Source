namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

    public async Task<IReadOnlyList<KnowledgeNeighborChunk>> ExpandAsync(Guid documentId,
        int chunkIndex,
        int window,
        string collectionId,
        CancellationToken cancellationToken)
    {
        if (!KnowledgeCollectionScope.TryNormalize(collectionId, out var normalizedCollectionId))
        {
            return [];
        }

        var safeWindow = Math.Max(0, window);
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);
        return await ReadRangeAsync(connection,
                documentId,
                chunkIndex - safeWindow,
                chunkIndex + safeWindow,
                normalizedCollectionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // Defensive cap on how many disjoint ranges are packed into one parameterized OR-disjunction, so the bound-parameter
    // count stays well under SQLite's per-statement limit even for a pathological anchor set (each range is 2 parameters
    // plus the shared document-id parameter). A document with more disjoint ranges than this splits into that few extra
    // queries; the common sparse top-k is one range set well under the cap and issues a single query.
    private const int MaxRangesPerQuery = 300;

    // Total chunk rows the last ExpandBatchAsync call actually hydrated from the database. Test-only seam
    // (internal + InternalsVisibleTo) that lets a test assert hydration is bounded to the union of the anchors' windows
    // rather than the min-to-max span across distant hits; not part of the public contract.
    internal int LastBatchRowsHydrated { get; private set; }

    // Number of DB commands the last ExpandBatchAsync call issued. Test-only seam that proves the one-query-per-document
    // contract: every disjoint range of a document is read by a SINGLE parameterized query (barring the defensive
    // range-chunking fallback above); not part of the public contract.
    internal int LastBatchQueryCount { get; private set; }

    public async Task<IReadOnlyList<IReadOnlyList<KnowledgeNeighborChunk>>> ExpandBatchAsync(IReadOnlyList<KnowledgeNeighborAnchor> anchors,
        int window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        LastBatchRowsHydrated = 0;
        LastBatchQueryCount = 0;
        if (anchors.Count == 0)
        {
            return [];
        }

        var safeWindow = Math.Max(0, window);

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        // Per document, merge only OVERLAPPING or ADJACENT anchor windows into DISJOINT ranges, then read ALL of that
        // document's ranges in ONE parameterized query (an OR of BETWEEN predicates) — never a single min-to-max span, so
        // two far-apart hits never drag in the intervening chunks, and never one query per range, so a sparse top-k stays a
        // single round trip per document. Each anchor is then sliced from the document's combined ascending rows; because
        // an anchor's window lies wholly inside one merged range that was read, the slice is byte-for-byte what
        // ExpandAsync(anchor) returns.
        var rowsByDocument = new Dictionary<Guid, IReadOnlyList<KnowledgeNeighborChunk>>();
        foreach (var group in anchors.GroupBy(static anchor => anchor.DocumentId))
        {
            var merged = MergeWindows(group.Select(anchor => (Lower: anchor.ChunkIndex - safeWindow, Upper: anchor.ChunkIndex + safeWindow)));
            rowsByDocument[group.Key] = await ReadDisjointRangesAsync(connection, group.Key, merged, cancellationToken).ConfigureAwait(false);
        }

        var results = new List<IReadOnlyList<KnowledgeNeighborChunk>>(anchors.Count);
        foreach (var anchor in anchors)
        {
            var lower = anchor.ChunkIndex - safeWindow;
            var upper = anchor.ChunkIndex + safeWindow;
            // The document's combined rows are ascending by chunk_index; slice this anchor's window out of them. All rows in
            // [lower, upper] were read (the window is inside one of the disjoint ranges), so the slice is complete and
            // ordered.
            var anchorWindow = rowsByDocument[anchor.DocumentId]
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

    // Reads every disjoint range of one document in a SINGLE parameterized query (an OR of BETWEEN predicates over the
    // shared document-id filter), splitting into additional queries only if the range count exceeds the defensive
    // per-query cap. Rows come back ascending by chunk_index; across the (ascending, disjoint) chunk batches the appended
    // result stays globally ascending. Updates the row-count and query-count seams.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification =
            "The OR-disjunction is a fixed count of $loN/$hiN placeholder pairs generated from an internal range count; every bound and id is a bound parameter and no value is concatenated into the command text.")]
    [SuppressMessage("Security Hotspot", "S2077:Formatting SQL queries is security-sensitive",
        Justification =
            "Only internally-generated $loN/$hiN placeholder names are interpolated; every range bound and the document id are bound parameters, so no user input reaches the command text.")]
    private async Task<IReadOnlyList<KnowledgeNeighborChunk>> ReadDisjointRangesAsync(DbConnection connection,
        Guid documentId,
        IReadOnlyList<(int Lower, int Upper)> ranges,
        CancellationToken cancellationToken)
    {
        var rows = new List<KnowledgeNeighborChunk>();
        for (var offset = 0; offset < ranges.Count; offset += MaxRangesPerQuery)
        {
            var count = Math.Min(MaxRangesPerQuery, ranges.Count - offset);
            await using var command = connection.CreateCommand();
            AddParameter(command, "$document_id", documentId);

            var predicates = new string[count];
            for (var i = 0; i < count; i++)
            {
                var (lower, upper) = ranges[offset + i];
                var lowerName = string.Create(CultureInfo.InvariantCulture, $"$lo{i}");
                var upperName = string.Create(CultureInfo.InvariantCulture, $"$hi{i}");
                predicates[i] = string.Create(CultureInfo.InvariantCulture, $"chunk_index BETWEEN {lowerName} AND {upperName}");
                AddParameter(command, lowerName, lower);
                AddParameter(command, upperName, upper);
            }

            command.CommandText = $"""
                                   SELECT chunk_id, chunk_index, content, heading_path
                                   FROM knowledge_document_chunks
                                   WHERE document_id = $document_id AND ({string.Join(" OR ", predicates)})
                                   ORDER BY chunk_index ASC;
                                   """;
            LastBatchQueryCount++;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var headingPath = await reader.IsDBNullAsync(ordinal: 3, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(3);
                rows.Add(new KnowledgeNeighborChunk(Guid.Parse(reader.GetString(0)),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    headingPath));
            }
        }

        LastBatchRowsHydrated += rows.Count;
        return rows;
    }

    private static async Task<IReadOnlyList<KnowledgeNeighborChunk>> ReadRangeAsync(DbConnection connection,
        Guid documentId,
        int lowerBound,
        int upperBound,
        CancellationToken cancellationToken)
    {
        return await ReadRangeAsync(connection, documentId, lowerBound, upperBound, collectionId: null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<KnowledgeNeighborChunk>> ReadRangeAsync(DbConnection connection,
        Guid documentId,
        int lowerBound,
        int upperBound,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT c.chunk_id, c.chunk_index, c.content, c.heading_path
                              FROM knowledge_document_chunks AS c
                              INNER JOIN knowledge_documents AS d ON d.document_id = c.document_id
                              WHERE c.document_id = $document_id
                                AND ($collection_id IS NULL OR d.collection_id = $collection_id)
                                AND c.chunk_index BETWEEN $lower AND $upper
                              ORDER BY c.chunk_index ASC;
                              """;
        AddParameter(command, "$document_id", documentId);
        AddParameter(command, "$collection_id", collectionId);
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
