namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.Caching;
using XE_Local_AI_Engine.Client.Common.Telemetry;

/// <summary>
///     Default <see cref="IKnowledgeQueryEmbeddingCache" />. A thin policy shell over the shared
///     <see cref="ByteBudgetedCache{TKey,TValue}" /> — the same component the playbook-retrieval ranker and semantic
///     memory dedup use — carrying this site's own semantics: the TTL is the cache's master switch (a zero TTL disables
///     caching outright), the key is the resolved model's policy-family identity plus a SHA-256 hash of the query so the
///     raw query text is never retained, and each value keeps its exact canonical identity and vector for caller-side
///     policy/width validation. Registered as a singleton so one cache serves every request.
/// </summary>
public sealed class KnowledgeQueryEmbeddingCache : IKnowledgeQueryEmbeddingCache
{
    // Byte ceiling alongside the configured entry bound: 128 entries is ~0.4 MB at 768 dimensions but ~2 MB at 4096, so
    // the entry bound alone does not bound RAM. Search-query vectors are small and short-lived; 2 MiB is headroom.
    private const long MaxBytes = 2L * 1024 * 1024;

    // Flat allowance per entry for the dictionary node and entry object — the budget bounds RAM, it does not measure it.
    private const long EntryOverheadBytes = 96;

    private readonly ByteBudgetedCache<string, KnowledgeQueryEmbeddingCacheEntry> _entries;
    private readonly TimeSpan _ttl;

    public KnowledgeQueryEmbeddingCache(IOptions<KnowledgeBaseOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ttl = TimeSpan.FromSeconds(Math.Max(0, options.Value.QueryEmbeddingCacheTtlSeconds));
        _entries = new ByteBudgetedCache<string, KnowledgeQueryEmbeddingCacheEntry>(MaxBytes,
            options.Value.QueryEmbeddingCacheMaxEntries,
            static (key, entry) => ((key.Length + entry.VectorIdentity.Length) * sizeof(char))
                                   + (entry.Vector.Length * sizeof(float))
                                   + EntryOverheadBytes,
            _ttl,
            timeProvider,
            RecordEvictedBytes,
            StringComparer.Ordinal);
    }

    public bool TryGet(string policyFamilyIdentity, string query, out KnowledgeQueryEmbeddingCacheEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyFamilyIdentity);
        ArgumentNullException.ThrowIfNull(query);

        if (_ttl > TimeSpan.Zero && _entries.TryGet(BuildKey(policyFamilyIdentity, query), out var cached))
        {
            entry = cached;
            RecordLookup(hit: true);
            return true;
        }

        entry = default!;
        RecordLookup(hit: false);
        return false;
    }

    public void Store(string policyFamilyIdentity, string query, KnowledgeQueryEmbeddingCacheEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyFamilyIdentity);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.VectorIdentity))
        {
            throw new ArgumentException("The cached vector identity is required.", nameof(entry));
        }

        if (_ttl <= TimeSpan.Zero || entry.Vector.IsEmpty)
        {
            return;
        }

        _entries.Set(BuildKey(policyFamilyIdentity, query), entry);
    }

    private static string BuildKey(string policyFamilyIdentity, string query)
    {
        // Hash the query so the raw (potentially sensitive) text is never retained as a dictionary key. The policy-family
        // identity is not sensitive and stays in the clear so model/policy changes yield distinct key spaces.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(query));
        return string.Concat(policyFamilyIdentity, "\n", Convert.ToHexStringLower(hash));
    }

    private static void RecordLookup(bool hit)
    {
        NodeMetrics.KnowledgeQueryEmbeddingCacheLookupsTotal.Add(1,
            new KeyValuePair<string, object?>("result", hit ? "hit" : "miss"));
    }

    private static void RecordEvictedBytes(long bytes)
    {
        NodeMetrics.KnowledgeQueryEmbeddingCacheEvictedBytesTotal.Add(bytes);
    }
}
