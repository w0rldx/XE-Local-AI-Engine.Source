namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IVectorSearch" />. Streams candidate rows from <c>knowledge_chunk_vectors</c> filtered to the
///     current embedding model (M1 — never cosine across models), reinterprets each <c>float32</c> BLOB (laid out in the
///     platform's native byte order) as a <see cref="ReadOnlySpan{Single}" /> without a copy, scores it against the
///     query vector with <see cref="TensorPrimitives.CosineSimilarity(ReadOnlySpan{float}, ReadOnlySpan{float})" />, and returns the top-k.
///     Vectors are scored one row at a time; only the resulting scalar score and identifiers are retained, never the full
///     set of vectors. Scoped: depends on the request-scoped <see cref="NodeChatDbContext" />.
/// </summary>
public sealed class ManagedCosineVectorSearch : IVectorSearch
{
    private readonly NodeChatDbContext _dbContext;

    public ManagedCosineVectorSearch(NodeChatDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchAsync(ReadOnlyMemory<float> queryVector,
        string embeddingModel,
        int limit,
        Guid? documentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModel);
        if (queryVector.IsEmpty || limit <= 0)
        {
            return [];
        }

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        if (documentId is null)
        {
            command.CommandText = """
                                  SELECT chunk_id, document_id, embedding
                                  FROM knowledge_chunk_vectors
                                  WHERE embedding_model = $embedding_model;
                                  """;
        }
        else
        {
            command.CommandText = """
                                  SELECT chunk_id, document_id, embedding
                                  FROM knowledge_chunk_vectors
                                  WHERE embedding_model = $embedding_model AND document_id = $document_id;
                                  """;
        }

        AddParameter(command, "$embedding_model", embeddingModel);
        if (documentId is not null)
        {
            AddParameter(command, "$document_id", documentId.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var scored = new List<VectorSearchHit>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var blob = await reader.GetFieldValueAsync<byte[]>(ordinal: 2, cancellationToken).ConfigureAwait(false);
            var candidate = MemoryMarshal.Cast<byte, float>(blob);

            // Dimension mismatch carries no comparable signal; skip so it never dilutes the ranked list (a same-dim,
            // different-model vector is already excluded by the WHERE filter above).
            if (candidate.Length != queryVector.Length)
            {
                continue;
            }

            var score = TensorPrimitives.CosineSimilarity(queryVector.Span, candidate);
            if (float.IsNaN(score))
            {
                // CosineSimilarity returns NaN for a zero-magnitude vector; treat as no overlap.
                continue;
            }

            scored.Add(new VectorSearchHit(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), score));
        }

        return scored
               .OrderByDescending(hit => hit.Score)
               .Take(limit)
               .ToList();
    }
}
