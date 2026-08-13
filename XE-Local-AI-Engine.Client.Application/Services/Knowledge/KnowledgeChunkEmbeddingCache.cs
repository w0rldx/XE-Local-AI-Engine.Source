namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.Caching;

/// <summary>
///     Bounded, TTL'd chunk-embedding reuse layered over vectors already committed to the local knowledge index. The
///     durable store makes reuse survive process restarts; the byte-budgeted working set coalesces concurrent misses and
///     avoids repeated SQLite reads. Failed factories are released without publishing an entry.
/// </summary>
public sealed class KnowledgeChunkEmbeddingCache : IKnowledgeChunkEmbeddingCache
{
    private const long EntryOverheadBytes = 128;

    private readonly ByteBudgetedCache<KnowledgeChunkEmbeddingCacheKey, byte[]> _entries;
    private readonly IKnowledgeChunkEmbeddingReuseStore _reuseStore;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;

    public KnowledgeChunkEmbeddingCache(IKnowledgeChunkEmbeddingReuseStore reuseStore,
        IOptions<KnowledgeBaseOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(reuseStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _reuseStore = reuseStore;
        _timeProvider = timeProvider;
        _ttl = TimeSpan.FromSeconds(Math.Max(0, options.Value.ChunkEmbeddingCacheTtlSeconds));
        var maxBytes = checked((long)Math.Max(1, options.Value.ChunkEmbeddingCacheMaxMegabytes) * 1024 * 1024);
        _entries = new ByteBudgetedCache<KnowledgeChunkEmbeddingCacheKey, byte[]>(maxBytes,
            options.Value.ChunkEmbeddingCacheMaxEntries,
            static (key, vector) => vector.LongLength
                                    + ((key.EmbeddingInputHash.Length
                                        + key.ParserVersion.Length
                                        + key.ChunkerVersion.Length
                                        + key.VectorIdentity.Length) * sizeof(char))
                                    + EntryOverheadBytes,
            _ttl,
            timeProvider);
    }

    public async Task<IReadOnlyList<byte[]>> GetOrCreateManyAsync(IReadOnlyList<KnowledgeChunkEmbeddingCacheKey> keys,
        Func<IReadOnlyList<KnowledgeChunkEmbeddingCacheKey>, CancellationToken, Task<IReadOnlyList<byte[]>>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(factory);
        if (keys.Count == 0)
        {
            return [];
        }

        if (_ttl <= TimeSpan.Zero)
        {
            return await CreateAndValidateAsync(keys, factory, cancellationToken).ConfigureAwait(false);
        }

        var resolved = await _entries.GetOrAddManyAsync(keys,
            async (missing, token) => await ResolveMissingAsync(missing, factory, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (resolved is null)
        {
            throw new InvalidOperationException("The chunk embedding cache could not resolve every requested vector.");
        }

        return resolved.Select(static vector => vector.ToArray()).ToArray();
    }

    private async Task<IReadOnlyList<byte[]>> ResolveMissingAsync(IReadOnlyList<KnowledgeChunkEmbeddingCacheKey> missing,
        Func<IReadOnlyList<KnowledgeChunkEmbeddingCacheKey>, CancellationToken, Task<IReadOnlyList<byte[]>>> factory,
        CancellationToken cancellationToken)
    {
        if (missing.Count == 0)
        {
            return [];
        }

        var durable = await _reuseStore.FindManyAsync(missing, _timeProvider.GetUtcNow() - _ttl, cancellationToken)
                                       .ConfigureAwait(false);
        var values = new byte[missing.Count][];
        var factoryKeys = new List<KnowledgeChunkEmbeddingCacheKey>();
        var factoryIndexes = new List<int>();

        for (var index = 0; index < missing.Count; index++)
        {
            var key = missing[index];
            if (durable.TryGetValue(key, out var vector) && IsValid(vector, key.Dimension))
            {
                values[index] = vector.ToArray();
            }
            else
            {
                factoryKeys.Add(key);
                factoryIndexes.Add(index);
            }
        }

        if (factoryKeys.Count > 0)
        {
            var created = await CreateAndValidateAsync(factoryKeys, factory, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < created.Count; index++)
            {
                values[factoryIndexes[index]] = created[index].ToArray();
            }
        }

        return values;
    }

    private static async Task<IReadOnlyList<byte[]>> CreateAndValidateAsync(IReadOnlyList<KnowledgeChunkEmbeddingCacheKey> keys,
        Func<IReadOnlyList<KnowledgeChunkEmbeddingCacheKey>, CancellationToken, Task<IReadOnlyList<byte[]>>> factory,
        CancellationToken cancellationToken)
    {
        var created = await factory(keys, cancellationToken).ConfigureAwait(false);
        if (created is null || created.Count != keys.Count)
        {
            throw new InvalidOperationException("The chunk embedding factory returned an incomplete result.");
        }

        var copies = new byte[created.Count][];
        for (var index = 0; index < created.Count; index++)
        {
            if (!IsValid(created[index], keys[index].Dimension))
            {
                throw new InvalidOperationException("The chunk embedding factory returned a vector with an invalid byte width.");
            }

            copies[index] = created[index].ToArray();
        }

        return copies;
    }

    private static bool IsValid(byte[]? vector, int dimension)
    {
        return vector is not null && vector.Length == checked(dimension * sizeof(float));
    }
}
