namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Buffers;
using System.Data.Common;
using System.Diagnostics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IVectorSearch" />: streams candidate rows from <c>knowledge_chunk_vectors</c> filtered to the
///     current embedding model, scores each one against the query vector, and keeps only the top-k.
/// </summary>
/// <remarks>
///     Vectors are never compared across models. Each <c>float32</c> BLOB, in the platform's native byte order, is
///     reinterpreted as a <see cref="ReadOnlySpan{Single}" /> over one reused pooled buffer, so no row allocates, and the
///     top-k lives in a bounded min-heap rather than a full corpus sort; only the scalar score and identifiers are kept,
///     never the vectors. Scoring follows <see cref="IKnowledgeVectorNormalizationState" />: a plain dot product once
///     every stored vector is known normalized, scale-invariant cosine until then. Scoped to the request db context.
/// </remarks>
public sealed class ManagedCosineVectorSearch : IVectorSearch
{
    private readonly NodeChatDbContext _dbContext;
    private readonly IKnowledgeVectorNormalizationState _normalizationState;

    public ManagedCosineVectorSearch(NodeChatDbContext dbContext, IKnowledgeVectorNormalizationState normalizationState)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _normalizationState = normalizationState ?? throw new ArgumentNullException(nameof(normalizationState));
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchAsync(ReadOnlyMemory<float> queryVector,
        string embeddingModel,
        string vectorIdentity,
        int vectorDimension,
        int limit,
        Guid? documentId,
        CancellationToken cancellationToken)
    {
        return await SearchCoreAsync(queryVector,
                embeddingModel,
                vectorIdentity,
                vectorDimension,
                limit,
                documentId,
                collectionId: null,
                cancellationToken);
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchAsync(ReadOnlyMemory<float> queryVector,
        string embeddingModel,
        string vectorIdentity,
        int vectorDimension,
        int limit,
        Guid? documentId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        if (!KnowledgeCollectionScope.TryNormalize(collectionId, out var normalizedCollectionId))
        {
            return [];
        }

        return await SearchCoreAsync(queryVector,
                embeddingModel,
                vectorIdentity,
                vectorDimension,
                limit,
                documentId,
                normalizedCollectionId,
                cancellationToken);
    }

    private async Task<IReadOnlyList<VectorSearchHit>> SearchCoreAsync(ReadOnlyMemory<float> queryVector,
        string embeddingModel,
        string vectorIdentity,
        int vectorDimension,
        int limit,
        Guid? documentId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorIdentity);
        if (queryVector.IsEmpty || limit <= 0)
        {
            return [];
        }

        if (queryVector.Length != vectorDimension || vectorDimension <= 0)
        {
            return [];
        }

        // Dot-product fast path once all stored vectors are known-normalized: normalize the query ONCE, then one dot pass
        // per candidate. A zero-magnitude query has no direction, so mirror cosine's all-NaN, empty result and return none.
        var useDot = _normalizationState.IsComplete;
        float[]? normalizedQueryBuffer = null;
        var scoringQuery = queryVector;
        if (useDot)
        {
            normalizedQueryBuffer = ArrayPool<float>.Shared.Rent(queryVector.Length);
            var normalizedQuery = normalizedQueryBuffer.AsSpan(0, queryVector.Length);
            queryVector.Span.CopyTo(normalizedQuery);
            if (!KnowledgeVectorMath.NormalizeInPlace(normalizedQuery))
            {
                ArrayPool<float>.Shared.Return(normalizedQueryBuffer);
                return [];
            }

            scoringQuery = normalizedQueryBuffer.AsMemory(0, queryVector.Length);
        }

        try
        {
            return await ScanAsync(scoringQuery,
                    useDot,
                    embeddingModel,
                    vectorIdentity,
                    vectorDimension,
                    limit,
                    documentId,
                    collectionId,
                    cancellationToken);
        }
        finally
        {
            if (normalizedQueryBuffer is not null)
            {
                ArrayPool<float>.Shared.Return(normalizedQueryBuffer);
            }
        }
    }

    private async Task<IReadOnlyList<VectorSearchHit>> ScanAsync(ReadOnlyMemory<float> scoringQuery,
        bool useDot,
        string embeddingModel,
        string vectorIdentity,
        int vectorDimension,
        int limit,
        Guid? documentId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        if (collectionId is null)
        {
            command.CommandText = """
                                  SELECT chunk_id, document_id, embedding
                                  FROM knowledge_chunk_vectors
                                  WHERE embedding_model = $embedding_model
                                    AND vector_identity = $vector_identity
                                    AND dim = $vector_dimension
                                    AND ($document_id IS NULL OR document_id = $document_id);
                                  """;
        }
        else
        {
            command.CommandText = """
                                  SELECT v.chunk_id, v.document_id, v.embedding
                                  FROM knowledge_chunk_vectors AS v
                                  JOIN knowledge_documents AS d ON d.document_id = v.document_id
                                  WHERE v.embedding_model = $embedding_model
                                    AND v.vector_identity = $vector_identity
                                    AND v.dim = $vector_dimension
                                    AND d.collection_id = $collection_id
                                    AND ($document_id IS NULL OR v.document_id = $document_id);
                                  """;
        }

        AddParameter(command, "$embedding_model", embeddingModel);
        AddParameter(command, "$vector_identity", vectorIdentity);
        AddParameter(command, "$vector_dimension", vectorDimension);
        if (collectionId is not null)
        {
            AddParameter(command, "$collection_id", collectionId);
        }

        AddParameter(command, "$document_id", documentId);

        var topK = new BoundedTopKSelector(limit);
        var candidatesScanned = 0L;
        byte[]? blobBuffer = null;
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            long sequence = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                // Honor cancellation between rows: the per-row scoring work does not otherwise observe it, so a large
                // corpus scan can be abandoned promptly.
                cancellationToken.ThrowIfCancellationRequested();
                candidatesScanned++;

                // Reinterpret the BLOB over a single reused pooled buffer rather than allocating a fresh byte[] per row.
                var candidate = ReadCandidateVector(reader, ref blobBuffer);

                // A same-model, different-width vector carries no comparable signal (a same-dim, different-model vector is
                // already excluded by the WHERE filter). Skip so it never dilutes the ranked list.
                if (candidate.Length != scoringQuery.Length)
                {
                    continue;
                }

                float score;
                if (useDot)
                {
                    // Every stored vector is normalized here EXCEPT a zero-magnitude one, which has no direction and stays
                    // zero; cosine yields NaN and skips it, so skip it too rather than scoring a false 0. The scan is ~free.
                    if (IsZeroVector(candidate))
                    {
                        continue;
                    }

                    score = TensorPrimitives.Dot(scoringQuery.Span, candidate);
                }
                else
                {
                    score = TensorPrimitives.CosineSimilarity(scoringQuery.Span, candidate);
                    if (float.IsNaN(score))
                    {
                        // CosineSimilarity returns NaN for a zero-magnitude vector; treat as no overlap.
                        continue;
                    }
                }

                // Materialize the id strings and Guids only for rows that actually enter the heap: once it is full most
                // scanned rows lose to the worst kept hit, so skipping their id reads saves two allocs and two parses each.
                if (topK.WouldAccept(score, sequence))
                {
                    topK.Offer(score, sequence, Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)));
                }

                sequence++;
            }

            return topK.ToSortedDescending();
        }
        finally
        {
            if (blobBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(blobBuffer);
            }

            NodeMetrics.KnowledgeVectorSearchCandidatesScanned.Record(candidatesScanned);
            NodeMetrics.KnowledgeVectorSearchDurationMs.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    ///     Reads the embedding BLOB (ordinal 2) into the reused pooled buffer via the reader's blob stream and returns it
    ///     reinterpreted as <c>float32</c> in native byte order, the layout the embedder wrote.
    /// </summary>
    /// <remarks>
    ///     No per-row <c>byte[]</c> allocation: the buffer grows only when a row is wider than any seen so far, and all
    ///     rows for one model share a width, so it is rented once. This cannot be async — C# forbids both a <c>ref</c>
    ///     parameter and a <c>ByRefLike</c> return in an async method — and that signature is what keeps the scan
    ///     allocation-free, threading the pooled buffer by ref and handing the row back as a span over it. The caller
    ///     <c>ScanAsync</c> already observes cancellation between rows.
    /// </remarks>
    private static ReadOnlySpan<float> ReadCandidateVector(DbDataReader reader, ref byte[]? buffer)
    {
#pragma warning disable MA0045 // ref parameter and ReadOnlySpan<float> return are both illegal in an async method; see the comment above.
        using var blob = reader.GetStream(2);
#pragma warning restore MA0045
        var length = checked((int)blob.Length);
        if (buffer is null || buffer.Length < length)
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            buffer = ArrayPool<byte>.Shared.Rent(length);
        }

        blob.ReadExactly(buffer.AsSpan(0, length));
        return MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, length));
    }

    // True when every component is zero. ContainsAnyExcept uses value equality, under which both +0.0 and -0.0 equal 0f,
    // so an all-zero vector of either sign is caught; the scan short-circuits, costing nothing for the non-zero majority.
    private static bool IsZeroVector(ReadOnlySpan<float> vector)
    {
        return !vector.ContainsAnyExcept(0f);
    }

    // Fixed-capacity min-heap keeping the `limit` best hits with a stable tie-break: among equal scores the earlier-read
    // row wins, via a monotonic sequence folded into the comparison, so this matches a full sort at O(n log k) and O(k).
    private sealed class BoundedTopKSelector
    {
        private readonly Candidate[] _heap;
        private int _count;

        public BoundedTopKSelector(int capacity)
        {
            _heap = new Candidate[Math.Max(1, capacity)];
        }

        // Cheap pre-check letting the caller skip materializing row ids for a candidate that would be rejected anyway:
        // identical decision logic to Offer, whose IsBetter never reads the ids, so the selected set and order are equal.
        public bool WouldAccept(float score, long sequence)
        {
            return _count < _heap.Length || IsBetter(new Candidate(score, sequence, Guid.Empty, Guid.Empty), _heap[0]);
        }

        public void Offer(float score, long sequence, Guid chunkId, Guid documentId)
        {
            var candidate = new Candidate(score, sequence, chunkId, documentId);
            if (_count < _heap.Length)
            {
                _heap[_count] = candidate;
                SiftUp(_count);
                _count++;
                return;
            }

            // Full: the root is the worst kept hit. Replace it only when the newcomer is strictly better, then re-heapify.
            if (IsBetter(candidate, _heap[0]))
            {
                _heap[0] = candidate;
                SiftDown(index: 0);
            }
        }

        public IReadOnlyList<VectorSearchHit> ToSortedDescending()
        {
            var kept = new Candidate[_count];
            Array.Copy(_heap, kept, _count);
            // Best first: descending by the same (score, then earlier sequence) order the heap ranks by.
            Array.Sort(kept, static (a, b) => IsBetter(a, b) ? -1 : 1);

            var results = new VectorSearchHit[_count];
            for (var i = 0; i < _count; i++)
            {
                results[i] = new VectorSearchHit { ChunkId = kept[i].ChunkId, DocumentId = kept[i].DocumentId, Score = kept[i].Score };
            }

            return results;
        }

        // a ranks above b when it scores higher, or ties on score but was read earlier (the stable-sort tie-break).
        private static bool IsBetter(in Candidate a, in Candidate b)
        {
            if (a.Score > b.Score)
            {
                return true;
            }

            if (a.Score < b.Score)
            {
                return false;
            }

            return a.Sequence < b.Sequence;
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                // Min-heap on "goodness": a parent that is BETTER than its child violates the invariant (the worst must be
                // at the root), so swap it down toward the leaves.
                if (IsBetter(_heap[parent], _heap[index]))
                {
                    (_heap[parent], _heap[index]) = (_heap[index], _heap[parent]);
                    index = parent;
                }
                else
                {
                    break;
                }
            }
        }

        private void SiftDown(int index)
        {
            while (true)
            {
                var left = (2 * index) + 1;
                var right = (2 * index) + 2;
                var worst = index;

                if (left < _count && IsBetter(_heap[worst], _heap[left]))
                {
                    worst = left;
                }

                if (right < _count && IsBetter(_heap[worst], _heap[right]))
                {
                    worst = right;
                }

                if (worst == index)
                {
                    break;
                }

                (_heap[worst], _heap[index]) = (_heap[index], _heap[worst]);
                index = worst;
            }
        }

        [StructLayout(LayoutKind.Auto)]
        private readonly record struct Candidate(float Score, long Sequence, Guid ChunkId, Guid DocumentId);
    }
}
