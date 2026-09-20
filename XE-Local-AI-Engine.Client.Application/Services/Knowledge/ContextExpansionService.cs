namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
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
        await OpenIfNeededAsync(connection, cancellationToken);

        return await ReadRangeAsync(connection, documentId, chunkIndex - safeWindow, chunkIndex + safeWindow, cancellationToken);
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
        await OpenIfNeededAsync(connection, cancellationToken);
        return await ReadRangeAsync(connection,
                documentId,
                chunkIndex - safeWindow,
                chunkIndex + safeWindow,
                normalizedCollectionId,
                cancellationToken);
    }

    /// <summary>
    ///     Defensive cap on how many disjoint ranges are packed into one parameterized OR-disjunction, keeping the
    ///     bound-parameter count well under SQLite's per-statement limit.
    /// </summary>
    /// <remarks>
    ///     Each range is two parameters plus the shared document-id parameter, so even a pathological anchor set stays
    ///     inside the limit. A document with more disjoint ranges than this splits into that few extra queries; the
    ///     common sparse top-k is one range set well under the cap and issues a single query.
    /// </remarks>
    private const int MaxRangesPerQuery = 300;

    /// <summary>Total chunk rows the last <c>ExpandBatchAsync</c> call actually hydrated from the database.</summary>
    /// <remarks>
    ///     Test-only seam (internal + <c>InternalsVisibleTo</c>) letting a test assert hydration is bounded to the union
    ///     of the anchors' windows rather than the min-to-max span across distant hits; not part of the public contract.
    /// </remarks>
    internal int LastBatchRowsHydrated { get; private set; }

    /// <summary>Number of DB commands the last <c>ExpandBatchAsync</c> call issued.</summary>
    /// <remarks>
    ///     Test-only seam proving the one-query-per-document contract: every disjoint range of a document is read by a
    ///     SINGLE parameterized query, barring the defensive range-chunking fallback above; not part of the public
    ///     contract.
    /// </remarks>
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
        await OpenIfNeededAsync(connection, cancellationToken);

        // Per document, merge only OVERLAPPING or ADJACENT anchor windows into disjoint ranges and read them all in one
        // query: never a min-to-max span (no intervening chunks), never one query per range (one round trip per document).
        var rowsByDocument = new Dictionary<Guid, IReadOnlyList<KnowledgeNeighborChunk>>();
        foreach (var group in anchors.GroupBy(static anchor => anchor.DocumentId))
        {
            var merged = MergeWindows(group.Select(anchor => new TextWindow(anchor.ChunkIndex - safeWindow, anchor.ChunkIndex + safeWindow)));
            rowsByDocument[group.Key] = await ReadDisjointRangesAsync(connection, group.Key, merged, cancellationToken);
        }

        var results = new List<IReadOnlyList<KnowledgeNeighborChunk>>(anchors.Count);
        foreach (var anchor in anchors)
        {
            var lower = anchor.ChunkIndex - safeWindow;
            var upper = anchor.ChunkIndex + safeWindow;
            // The document's combined rows are ascending by chunk_index and every row in [lower, upper] was read (the
            // window lies inside one disjoint range), so slicing this anchor's window out of them is complete and ordered.
            var anchorWindow = rowsByDocument[anchor.DocumentId]
                               .Where(chunk => chunk.ChunkIndex >= lower && chunk.ChunkIndex <= upper)
                               .ToList();
            results.Add(anchorWindow);
        }

        return results;
    }

    // Merges overlapping or adjacent (touching, no unindexed gap) windows into disjoint ascending ranges; a window past
    // even one unindexed position starts a new range, so a distant anchor never widens an earlier range across the gap.
    private static List<TextWindow> MergeWindows(IEnumerable<TextWindow> windows)
    {
        var merged = new List<TextWindow>();
        foreach (var (lower, upper) in windows.OrderBy(static window => window.Lower))
        {
            if (merged.Count > 0 && lower <= merged[^1].Upper + 1)
            {
                merged[^1] = new TextWindow(merged[^1].Lower, Math.Max(merged[^1].Upper, upper));
            }
            else
            {
                merged.Add(new TextWindow(lower, upper));
            }
        }

        return merged;
    }

    // Reads every disjoint range of one document in a SINGLE parameterized query (an OR of BETWEEN over the shared
    // document-id filter), splitting only past the per-query cap; rows stay globally ascending. Updates both seams.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification =
            "The OR-disjunction is a fixed count of $loN/$hiN placeholder pairs generated from an internal range count; every bound and id is a bound parameter and no value is concatenated into the command text.")]
    [SuppressMessage("Security Hotspot", "S2077:Formatting SQL queries is security-sensitive",
        Justification =
            "Only internally-generated $loN/$hiN placeholder names are interpolated; every range bound and the document id are bound parameters, so no user input reaches the command text.")]
    private async Task<IReadOnlyList<KnowledgeNeighborChunk>> ReadDisjointRangesAsync(DbConnection connection,
        Guid documentId,
        IReadOnlyList<TextWindow> ranges,
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

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var headingPath = await reader.IsDBNullAsync(ordinal: 3, cancellationToken)
                    ? null
                    : reader.GetString(3);
                rows.Add(new KnowledgeNeighborChunk
                {
                    ChunkId = Guid.Parse(reader.GetString(0)),
                    ChunkIndex = reader.GetInt32(1),
                    Content = reader.GetString(2),
                    HeadingPath = headingPath
                });
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
        return await ReadRangeAsync(connection, documentId, lowerBound, upperBound, collectionId: null, cancellationToken);
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

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var neighbors = new List<KnowledgeNeighborChunk>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var headingPath = await reader.IsDBNullAsync(ordinal: 3, cancellationToken)
                ? null
                : reader.GetString(3);
            neighbors.Add(new KnowledgeNeighborChunk
            {
                ChunkId = Guid.Parse(reader.GetString(0)),
                ChunkIndex = reader.GetInt32(1),
                Content = reader.GetString(2),
                HeadingPath = headingPath
            });
        }

        return neighbors;
    }

    // An inclusive chunk-index range. Named rather than System.Range, which indexes a sequence instead of describing one.
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct TextWindow(int Lower, int Upper);
}
