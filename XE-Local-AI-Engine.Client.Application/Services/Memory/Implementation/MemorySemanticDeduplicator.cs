namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using System.Collections.Concurrent;
using System.Numerics.Tensors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp.Models.Exceptions;
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
    // RAM-only existing-memory embedding cache keyed by (memory id, version, embedding model). Version invalidates an
    // edited memory; the model name guards against cosine'ing a stale-dimension vector against a new model's candidate.
    // Insertion order is tracked separately so the bound evicts the oldest-inserted entries first.
    private readonly ConcurrentDictionary<EmbeddingCacheKey, ReadOnlyMemory<float>> _cache = new();
    private readonly Lock _cacheLock = new();
    private readonly IEmbeddingModelResolver _embeddingModelResolver;
    private readonly Queue<EmbeddingCacheKey> _insertionOrder = new();
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

        // Resolve each existing memory's cached vector (keyed by id+version+model); collect the misses (with their text)
        // plus every candidate into one ordered batch so the whole run costs a single embedding round-trip. Candidates
        // are always re-embedded (they have no stable identity yet) and never cached.
        var existingVectors = new ReadOnlyMemory<float>[existing.Count];
        var missIndexes = new List<int>();
        var batchTexts = new List<string>();

        for (var index = 0; index < existing.Count; index++)
        {
            var key = new EmbeddingCacheKey(existing[index].Id, existing[index].Version, model);
            if (_cache.TryGetValue(key, out var cached))
            {
                existingVectors[index] = cached;
                continue;
            }

            missIndexes.Add(index);
            batchTexts.Add(existing[index].Behavior);
        }

        var candidateBatchStart = batchTexts.Count;
        foreach (var candidate in candidates)
        {
            batchTexts.Add(candidate.Behavior);
        }

        using var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
        {
            ModelName = model,
            ProviderName = providerName
        });

        var generated = await generator.GenerateAsync(batchTexts, options: null, cancellationToken).ConfigureAwait(false);

        // A well-behaved generator returns exactly one embedding per input, in order. A short/partial response would make
        // the positional indexing below throw; degrade to lexical-only instead so the run never drops candidates on a
        // misbehaving embedder. No candidate/memory text is logged.
        if (generated.Count != batchTexts.Count)
        {
            _logger.LogWarning("Semantic memory dedup received a short embedding response; falling back to lexical-only dedup for this run.");
            return MemorySemanticDedupResult.NotApplied;
        }

        // Fill the missed existing vectors into their slots and cache them (RAM-only, bounded).
        for (var missPosition = 0; missPosition < missIndexes.Count; missPosition++)
        {
            var existingIndex = missIndexes[missPosition];
            var vector = generated[missPosition].Vector;
            existingVectors[existingIndex] = vector;
            StoreInCache(new EmbeddingCacheKey(existing[existingIndex].Id, existing[existingIndex].Version, model), vector);
        }

        return ClassifyCandidates(existing, candidates, existingVectors, generated, candidateBatchStart);
    }

    private MemorySemanticDedupResult ClassifyCandidates(IReadOnlyList<MemoryDedupExisting> existing,
        IReadOnlyList<MemoryDedupCandidate> candidates,
        ReadOnlyMemory<float>[] existingVectors,
        GeneratedEmbeddings<Embedding<float>> generated,
        int candidateBatchStart)
    {
        var threshold = _options.SemanticDedupSimilarityThreshold;
        var duplicateIndexes = new HashSet<int>();

        // Batch positions of candidates accepted so far (not flagged duplicate). Comparing a later candidate against
        // earlier accepted candidates collapses two paraphrases proposed in the same run; a flagged duplicate is never a
        // comparison target (it will not be persisted).
        var acceptedCandidatePositions = new List<int>(candidates.Count);

        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            var candidateVector = generated[candidateBatchStart + candidateIndex].Vector;

            var isDuplicate = MatchesExisting(existing, existingVectors, candidate, candidateVector, threshold)
                              || MatchesAcceptedCandidate(candidates, generated, candidateBatchStart, acceptedCandidatePositions, candidate, candidateVector, threshold);

            if (isDuplicate)
            {
                duplicateIndexes.Add(candidateIndex);
            }
            else
            {
                acceptedCandidatePositions.Add(candidateBatchStart + candidateIndex);
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
        GeneratedEmbeddings<Embedding<float>> generated,
        int candidateBatchStart,
        List<int> acceptedCandidatePositions,
        MemoryDedupCandidate candidate,
        ReadOnlyMemory<float> candidateVector,
        double threshold)
    {
        foreach (var acceptedPosition in acceptedCandidatePositions)
        {
            if (candidates[acceptedPosition - candidateBatchStart].Scope != candidate.Scope)
            {
                continue;
            }

            if (CosineScore(candidateVector, generated[acceptedPosition].Vector) >= threshold)
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

    private void StoreInCache(EmbeddingCacheKey key, ReadOnlyMemory<float> vector)
    {
        var bound = _options.SemanticDedupEmbeddingCacheMaxEntries;
        lock (_cacheLock)
        {
            if (!_cache.TryAdd(key, vector))
            {
                return;
            }

            _insertionOrder.Enqueue(key);

            // Evict oldest-inserted entries until the cache is back within its RAM-only bound.
            while (_cache.Count > bound && _insertionOrder.Count > 0)
            {
                var evicted = _insertionOrder.Dequeue();
                _cache.TryRemove(evicted, out _);
            }
        }
    }

    private readonly record struct EmbeddingCacheKey(Guid MemoryId, int Version, string Model);
}
