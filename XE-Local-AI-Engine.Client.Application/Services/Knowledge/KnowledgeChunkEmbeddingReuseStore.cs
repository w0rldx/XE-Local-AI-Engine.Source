namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>Reads exact content/version/model matches from already-committed knowledge vectors.</summary>
public sealed class KnowledgeChunkEmbeddingReuseStore(IServiceScopeFactory scopeFactory) : IKnowledgeChunkEmbeddingReuseStore
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public async Task<IReadOnlyDictionary<KnowledgeChunkEmbeddingCacheKey, byte[]>> FindManyAsync(
        IReadOnlyList<KnowledgeChunkEmbeddingCacheKey> keys,
        DateTimeOffset notBeforeUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return new Dictionary<KnowledgeChunkEmbeddingCacheKey, byte[]>();
        }

        var requested = keys.ToHashSet();
        var hashes = keys.Select(static key => key.EmbeddingInputHash).Distinct(StringComparer.Ordinal).ToList();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<KnowledgeChunkEmbeddingCacheKey, (byte[] Vector, long UpdatedAtUtc)>();
        const int batchSize = 500;
        for (var offset = 0; offset < hashes.Count; offset += batchSize)
        {
            var count = Math.Min(batchSize, hashes.Count - offset);
            await ReadBatchAsync(connection,
                hashes.GetRange(offset, count),
                requested,
                result,
                notBeforeUtc.ToUnixTimeMilliseconds(),
                cancellationToken).ConfigureAwait(false);
        }

        return result.ToDictionary(static pair => pair.Key, static pair => pair.Value.Vector);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internally generated parameter placeholder names are interpolated; every hash is bound.")]
    [SuppressMessage("Security Hotspot", "S2077:Formatting SQL queries is security-sensitive",
        Justification = "Only internally generated parameter placeholder names are interpolated; every hash is bound.")]
    private static async Task ReadBatchAsync(DbConnection connection,
        IReadOnlyList<string> hashes,
        IReadOnlySet<KnowledgeChunkEmbeddingCacheKey> requested,
        IDictionary<KnowledgeChunkEmbeddingCacheKey, (byte[] Vector, long UpdatedAtUtc)> result,
        long cutoff,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var placeholders = new string[hashes.Count];
        for (var index = 0; index < hashes.Count; index++)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"$hash{index}");
            placeholders[index] = name;
            AddParameter(command, name, hashes[index]);
        }

        command.CommandText = $"""
                               SELECT c.embedding_input_hash, d.parser_version, d.chunker_version,
                                      v.vector_identity, v.dim, v.embedding, d.updated_at_utc
                               FROM knowledge_document_chunks AS c
                               JOIN knowledge_chunk_vectors AS v ON v.chunk_id = c.chunk_id
                               JOIN knowledge_documents AS d ON d.document_id = c.document_id
                               WHERE c.embedding_input_hash IN ({string.Join(", ", placeholders)})
                                 AND d.status = $indexed
                                 AND d.updated_at_utc >= $cutoff;
                               """;
        AddParameter(command, "$indexed", KnowledgeDocumentStatus.Indexed.ToString());
        AddParameter(command, "$cutoff", cutoff);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new KnowledgeChunkEmbeddingCacheKey(reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4));
            var vector = await reader.GetFieldValueAsync<byte[]>(5, cancellationToken).ConfigureAwait(false);
            var updatedAtUtc = reader.GetInt64(6);
            if (!requested.Contains(key)
                || vector.Length != checked(key.Dimension * sizeof(float))
                || (result.TryGetValue(key, out var existing) && existing.UpdatedAtUtc >= updatedAtUtc))
            {
                continue;
            }

            result[key] = (vector.ToArray(), updatedAtUtc);
        }
    }
}
