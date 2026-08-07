namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using System.Numerics.Tensors;
using Microsoft.Extensions.Options;
using OllamaSharp.Models.Exceptions;
using XE_Local_AI_Engine.Client.Common.Caching;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Default <see cref="IMemorySemanticDeduplicator" />. Embeds lexically-surviving candidates with the node-local
///     embedding model (resolved on the configured provider via the shared <see cref="IEmbeddingModelResolver" />, the
///     same seam the knowledge-base and playbook-retrieval lanes use) and drops any candidate whose cosine similarity to
///     an existing live memory of the same scope reaches the configured threshold.
///     Privacy/robustness invariants (mirroring <c>EmbeddingPlaybookRetrievalRanker</c> and the KB staleness guard):
///     the embedding provider is node-local only (never a shared/cloud client); existing-memory vectors live in a
///     RAM-only, bounded cache keyed by (id, version, resolved-model) and are never persisted or logged; the candidate is
///     re-embedded every run; and semantic dedup is skipped entirely unless the resolution
///     <see cref="EmbeddingModelResolution.IsConfident" /> — so a transient provider outage degrades to lexical-only
///     rather than mass-deduping (silently swallowing) legitimate new candidates. Registered as a singleton so the cache
///     is long-lived.
/// </summary>
internal sealed class MemorySemanticDeduplicator : IMemorySemanticDeduplicator
{
    // Byte ceiling on the existing-memory vector cache, alongside the configured entry bound. 4 MiB holds well over the
    // default 512 entries at 768 dimensions and caps a 4096-dimension model at ~256 — the entry bound alone would let
    // the same configuration retain 8 MB. Mirrors the playbook ranker's ceiling.
    private const long EmbeddingCacheMaxBytes = 4L * 1024 * 1024;

    // Flat allowance per entry for the key struct plus dictionary node — the budget bounds RAM, it does not measure it.
    private const long EntryOverheadBytes = 64;

    // RAM-only existing-memory embedding cache keyed by (memory id, version, embedding model). Version invalidates an
    // edited memory; the model name guards against cosine'ing a stale-dimension vector against a new model's candidate.
    // Eviction is coldest-first within both bounds, and concurrent extraction runs missing on the same memory share one
    // embedding round-trip rather than each paying the single-slot embedding server.
    private readonly ByteBudgetedCache<EmbeddingCacheKey, ReadOnlyMemory<float>> _cache;
    private readonly IEmbeddingModelResolver _embeddingModelResolver;
    private readonly ILogger<MemorySemanticDeduplicator> _logger;
    private readonly MemoryExtractionOptions _options;
    private readonly ILocalModelProviderResolver _providerResolver;

    public MemorySemanticDeduplicator(ILocalModelProviderResolver providerResolver,
        IEmbeddingModelResolver embeddingModelResolver,
        IOptions<MemoryExtractionOptions> options,
        ILogger<MemorySemanticDeduplicator> logger)
    {
        ArgumentNullException.ThrowIfNull(providerResolver);
        ArgumentNullException.ThrowIfNull(embeddingModelResolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _providerResolver = providerResolver;
        _embeddingModelResolver = embeddingModelResolver;
        _options = options.Value;
        _logger = logger;
        _cache = new ByteBudgetedCache<EmbeddingCacheKey, ReadOnlyMemory<float>>(EmbeddingCacheMaxBytes,
            _options.SemanticDedupEmbeddingCacheMaxEntries,
            static (key, vector) => (vector.Length * sizeof(float)) + (key.Model.Length * sizeof(char)) + EntryOverheadBytes);
    }

    /// <inheritdoc />
    public async Task<MemorySemanticDedupResult> FindSemanticDuplicatesAsync(IReadOnlyList<MemoryDedupExisting> existing,
        IReadOnlyList<MemoryDedupCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidates);

        // Disabled gate: the master switch off, or no embedding provider configured => semantic dedup is a clean no-op
        // and the caller keeps its lexical-only result. Mirrors the ranker's "lexical stays default" disabled gate: no
        // provider is resolved and no embedding client is constructed.
        if (!_options.SemanticDedupEnabled || string.IsNullOrWhiteSpace(_options.SemanticDedupEmbeddingProviderName))
        {
            return MemorySemanticDedupResult.NotApplied;
        }

        if (candidates.Count == 0)
        {
            return MemorySemanticDedupResult.NotApplied;
        }

        try
        {
            return await FindSemanticDuplicatesCoreAsync(existing, candidates, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Never swallow a genuine caller cancellation (the extraction run's own token fired).
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaException or InvalidOperationException)
        {
            // Any node-local embedding hiccup (model not pulled, Ollama/llama-server down, transport error, or an
            // unregistered provider name -> InvalidOperationException from the resolver) degrades to lexical-only
            // (NOT-applied) so the run never mass-dedups legitimate new candidates. No candidate/memory text is logged.
            _logger.LogWarning(exception, "Semantic memory dedup failed; falling back to lexical-only dedup for this run.");
            return MemorySemanticDedupResult.NotApplied;
        }
    }

    private async Task<MemorySemanticDedupResult> FindSemanticDuplicatesCoreAsync(IReadOnlyList<MemoryDedupExisting> existing,
        IReadOnlyList<MemoryDedupCandidate> candidates,
        CancellationToken cancellationToken)
    {
        // Resolve the provider, then the ACTUAL installed embedding model on it. IsConfident is the outage guard: a
        // non-confident resolution (provider unreachable, or nothing installed matched) skips semantic dedup entirely so
        // a transient outage can never silently swallow legitimate new candidates — exactly the KB corpus-reset guard.
        var providerName = _options.SemanticDedupEmbeddingProviderName;
        var provider = _providerResolver.ResolveProvider(providerName);
        var resolution = await _embeddingModelResolver.ResolveAsync(provider, cancellationToken).ConfigureAwait(false);
        if (!resolution.IsConfident)
        {
            _logger.LogDebug("Semantic memory dedup skipped: no confident node-local embedding model; lexical-only for this run.");
            return MemorySemanticDedupResult.NotApplied;
        }

        var model = resolution.Name;

        using var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
        {
            ModelName = model,
            ProviderName = providerName
        });

        var keys = new EmbeddingCacheKey[existing.Count];
        var textByKey = new Dictionary<EmbeddingCacheKey, string>(existing.Count);
        for (var index = 0; index < existing.Count; index++)
        {
            keys[index] = new EmbeddingCacheKey(existing[index].Id, existing[index].Version, model);
            textByKey[keys[index]] = existing[index].Behavior;
        }

        // The cache resolves the existing memories it can (and waits on a concurrent run already embedding the same
        // memory); the remaining misses plus every candidate go out as ONE batch, so a run still costs a single
        // embedding round-trip. Candidates are always re-embedded (they have no stable identity yet) and never cached.
        var candidateVectors = new ReadOnlyMemory<float>[candidates.Count];
        var existingVectors = await _cache.GetOrAddManyAsync(keys, EmbedMissingExistingAsync, cancellationToken).ConfigureAwait(false);

        if (existingVectors is null)
        {
            // A short/partial embedding response, here or in a concurrent run this one coalesced onto. No
            // candidate/memory text is logged.
            _logger.LogWarning("Semantic memory dedup could not resolve every existing-memory vector; falling back to lexical-only dedup for this run.");
            return MemorySemanticDedupResult.NotApplied;
        }

        return ClassifyCandidates(existing, candidates, existingVectors, candidateVectors);

        async Task<IReadOnlyList<ReadOnlyMemory<float>>?> EmbedMissingExistingAsync(IReadOnlyList<EmbeddingCacheKey> missing,
            CancellationToken token)
        {
            var batchTexts = new List<string>(missing.Count + candidates.Count);
            foreach (var key in missing)
            {
                batchTexts.Add(textByKey[key]);
            }

            foreach (var candidate in candidates)
            {
                batchTexts.Add(candidate.Behavior);
            }

            var generated = await generator.GenerateAsync(batchTexts, options: null, token).ConfigureAwait(false);

            // A well-behaved generator returns exactly one embedding per input, in order. A short/partial response would
            // make the positional indexing throw; signal a degrade instead so the run never drops candidates on a
            // misbehaving embedder.
            if (generated.Count != batchTexts.Count)
            {
                return null;
            }

            for (var index = 0; index < candidates.Count; index++)
            {
                candidateVectors[index] = generated[missing.Count + index].Vector;
            }

            var vectors = new ReadOnlyMemory<float>[missing.Count];
            for (var position = 0; position < missing.Count; position++)
            {
                vectors[position] = generated[position].Vector;
            }

            return vectors;
        }
    }

    private MemorySemanticDedupResult ClassifyCandidates(IReadOnlyList<MemoryDedupExisting> existing,
        IReadOnlyList<MemoryDedupCandidate> candidates,
        ReadOnlyMemory<float>[] existingVectors,
        ReadOnlyMemory<float>[] candidateVectors)
    {
        var threshold = _options.SemanticDedupSimilarityThreshold;
        var duplicateIndexes = new HashSet<int>();

        // Indexes of candidates accepted so far (not flagged duplicate). Comparing a later candidate against earlier
        // accepted candidates collapses two paraphrases proposed in the same run; a flagged duplicate is never a
        // comparison target (it will not be persisted).
        var acceptedCandidateIndexes = new List<int>(candidates.Count);

        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            var candidateVector = candidateVectors[candidateIndex];

            var isDuplicate = MatchesExisting(existing, existingVectors, candidate, candidateVector, threshold)
                              || MatchesAcceptedCandidate(candidates, candidateVectors, acceptedCandidateIndexes, candidate, candidateVector, threshold);

            if (isDuplicate)
            {
                duplicateIndexes.Add(candidateIndex);
            }
            else
            {
                acceptedCandidateIndexes.Add(candidateIndex);
            }
        }

        return new MemorySemanticDedupResult(Applied: true, duplicateIndexes);
    }

    private static bool MatchesExisting(IReadOnlyList<MemoryDedupExisting> existing,
        ReadOnlyMemory<float>[] existingVectors,
        MemoryDedupCandidate candidate,
        ReadOnlyMemory<float> candidateVector,
        double threshold)
    {
        for (var index = 0; index < existing.Count; index++)
        {
            if (existing[index].Scope != candidate.Scope)
            {
                continue;
            }

            if (CosineScore(candidateVector, existingVectors[index]) >= threshold)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAcceptedCandidate(IReadOnlyList<MemoryDedupCandidate> candidates,
        ReadOnlyMemory<float>[] candidateVectors,
        List<int> acceptedCandidateIndexes,
        MemoryDedupCandidate candidate,
        ReadOnlyMemory<float> candidateVector,
        double threshold)
    {
        foreach (var acceptedIndex in acceptedCandidateIndexes)
        {
            if (candidates[acceptedIndex].Scope != candidate.Scope)
            {
                continue;
            }

            if (CosineScore(candidateVector, candidateVectors[acceptedIndex]) >= threshold)
            {
                return true;
            }
        }

        return false;
    }

    private static float CosineScore(ReadOnlyMemory<float> left, ReadOnlyMemory<float> right)
    {
        // Mismatched-dimension or empty vectors carry no comparable signal; treat as no overlap so the candidate is kept
        // (never dropped on a non-comparison). CosineSimilarity returns NaN for a zero-magnitude vector — guard that too.
        if (left.IsEmpty || right.IsEmpty || left.Length != right.Length)
        {
            return 0f;
        }

        var score = TensorPrimitives.CosineSimilarity(left.Span, right.Span);
        return float.IsNaN(score) ? 0f : score;
    }

    private readonly record struct EmbeddingCacheKey(Guid MemoryId, int Version, string Model);
}
