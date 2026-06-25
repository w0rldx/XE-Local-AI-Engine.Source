namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using System.Collections.Concurrent;
using System.Numerics.Tensors;
using Microsoft.Extensions.Options;
using OllamaSharp.Models.Exceptions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Embedding-backed <see cref="IPlaybookRetrievalRanker" />: ranks candidates by cosine similarity between the
///     node-local embedding of the query and of each candidate's <c>TriggerCondition</c> (falling back to
///     <c>Behavior</c>), with the same deterministic tiebreak the lexical ranker uses (Priority ascending, then
///     CreatedAtUtc ascending). Embeddings are produced by the node-local <see cref="ILocalModelProvider" /> only — never
///     a shared/cloud client — so playbook action text and the user-turn query never leave the node, and candidate
///     vectors are held in a RAM-only cache that is never persisted, logged, or returned.
///     The ranker is config-gated: with no <see cref="PlaybookRetrievalOptions.EmbeddingModelName" /> configured it
///     delegates straight to the lexical ranker without constructing any embedding client, and on any embedding failure
///     (model not pulled, Ollama unreachable, transport error) it falls back to the lexical ranker so a send never breaks
///     and CI stays deterministic without Ollama.
/// </summary>
public sealed class EmbeddingPlaybookRetrievalRanker : IPlaybookRetrievalRanker
{
    // RAM-only candidate-embedding cache keyed by (action id, version, embedding model). Version invalidates an edited
    // action automatically; the model name guards against cosine'ing a stale-dimension vector against a new model's
    // query. Insertion order is tracked separately so the bound evicts the oldest-inserted entries first.
    private readonly ConcurrentDictionary<EmbeddingCacheKey, ReadOnlyMemory<float>> _cache = new();
    private readonly Lock _cacheLock = new();
    private readonly Queue<EmbeddingCacheKey> _insertionOrder = new();
    private readonly LexicalPlaybookRetrievalRanker _lexical;
    private readonly ILogger<EmbeddingPlaybookRetrievalRanker> _logger;
    private readonly PlaybookRetrievalOptions _options;
    private readonly ILocalModelProviderResolver _providerResolver;

    public EmbeddingPlaybookRetrievalRanker(ILocalModelProviderResolver providerResolver,
        IOptions<PlaybookRetrievalOptions> options,
        LexicalPlaybookRetrievalRanker lexical,
        ILogger<EmbeddingPlaybookRetrievalRanker> logger)
    {
        ArgumentNullException.ThrowIfNull(providerResolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(logger);

        _providerResolver = providerResolver;
        _options = options.Value;
        _lexical = lexical;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlaybookActionRecord>> SelectTopKAsync(string query,
        IReadOnlyList<PlaybookActionRecord> candidates,
        int k,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Disabled gate: no embedding model configured => the lexical ranker is the effective ranker. No embedding
        // client is constructed and Ollama is never touched (the "lexical stays default" / CI behaviour).
        var model = _options.EmbeddingModelName;
        if (string.IsNullOrWhiteSpace(model))
        {
            return await _lexical.SelectTopKAsync(query, candidates, k, cancellationToken).ConfigureAwait(false);
        }

        if (k <= 0 || candidates.Count == 0)
        {
            return [];
        }

        try
        {
            return await RankByEmbeddingAsync(query, candidates, k, model, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Never swallow cancellation — let the send's own cancellation propagate.
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaException or InvalidOperationException)
        {
            // Any node-local embedding hiccup degrades gracefully to the deterministic lexical ranker so the send still
            // completes: model not pulled / Ollama or llama-server down / transport error (HttpRequestException,
            // IOException — the llama-server deferred generator wraps its LlamaRuntimeException to IOException — or
            // OllamaException), or a misconfigured/unregistered EmbeddingProviderName (InvalidOperationException from
            // the resolver). None of these are a reason to break the send.
            return await FallBackToLexicalAsync(query, candidates, k, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    // The single lexical-fallback site: logs one text-free Warning (never any playbook/query text) and delegates to the
    // deterministic lexical ranker so a send never breaks on an embedding failure. A null <paramref name="exception" />
    // is the non-exceptional degrade (e.g. a short/partial embedding response) and still logs at Warning.
    private Task<IReadOnlyList<PlaybookActionRecord>> FallBackToLexicalAsync(string query,
        IReadOnlyList<PlaybookActionRecord> candidates,
        int k,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(exception,
            "Embedding-based playbook retrieval failed; falling back to lexical ranking for this send.");
        return _lexical.SelectTopKAsync(query, candidates, k, cancellationToken);
    }

    private async Task<IReadOnlyList<PlaybookActionRecord>> RankByEmbeddingAsync(string query,
        IReadOnlyList<PlaybookActionRecord> candidates,
        int k,
        string model,
        CancellationToken cancellationToken)
    {
        // Resolve each candidate's cached vector; collect the misses (with their text) plus the query into one ordered
        // batch so the whole send costs a single embedding round-trip. The query is always re-embedded (never cached).
        var candidateVectors = new ReadOnlyMemory<float>[candidates.Count];
        var missIndexes = new List<int>();
        var batchTexts = new List<string>();

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var key = new EmbeddingCacheKey(candidate.Id, candidate.Version, model);
            if (_cache.TryGetValue(key, out var cached))
            {
                candidateVectors[index] = cached;
                continue;
            }

            missIndexes.Add(index);
            batchTexts.Add(CandidateText(candidate));
        }

        var queryBatchIndex = batchTexts.Count;
        batchTexts.Add(query);

        // Route the embedding model to the runtime named by EmbeddingProviderName (ollama or llamacpp). For "llamacpp"
        // the provider stands up a non-none-pooling embedding process on first use; an unavailable process throws a
        // caught transport type (the deferred generator wraps LlamaRuntimeException -> IOException) so retrieval still
        // degrades to lexical. A misconfigured/unregistered provider name throws InvalidOperationException,
        // also caught below as a degrade rather than a hard send failure.
        var provider = _providerResolver.ResolveProvider(_options.EmbeddingProviderName);
        using var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
        {
            ModelName = model,
            ProviderName = _options.EmbeddingProviderName
        });

        var generated = await generator.GenerateAsync(batchTexts, options: null, cancellationToken).ConfigureAwait(false);

        // A well-behaved generator returns exactly one embedding per input, in order. A short/partial response would
        // make the positional indexing below throw ArgumentOutOfRangeException (outside the narrow catch); degrade to
        // lexical instead so the send never breaks. No playbook/query text is logged.
        if (generated.Count != batchTexts.Count)
        {
            return await FallBackToLexicalAsync(query, candidates, k, exception: null, cancellationToken).ConfigureAwait(false);
        }

        var queryVector = generated[queryBatchIndex].Vector;
        for (var missPosition = 0; missPosition < missIndexes.Count; missPosition++)
        {
            var candidateIndex = missIndexes[missPosition];
            var vector = generated[missPosition].Vector;
            candidateVectors[candidateIndex] = vector;

            var candidate = candidates[candidateIndex];
            StoreInCache(new EmbeddingCacheKey(candidate.Id, candidate.Version, model), vector);
        }

        return candidates
               .Select((candidate, index) => new ScoredCandidate(candidate, CosineScore(queryVector, candidateVectors[index])))
               .OrderByDescending(scored => scored.Score)
               .ThenBy(scored => scored.Action.Priority)
               .ThenBy(scored => scored.Action.CreatedAtUtc)
               .Take(k)
               .Select(scored => scored.Action)
               .ToList();
    }

    private static string CandidateText(PlaybookActionRecord candidate)
    {
        return candidate.TriggerCondition ?? candidate.Behavior;
    }

    private static float CosineScore(ReadOnlyMemory<float> query, ReadOnlyMemory<float> candidate)
    {
        // Mismatched-dimension or empty vectors carry no comparable signal; treat as no overlap so the deterministic
        // tiebreak orders them. CosineSimilarity returns NaN for a zero-magnitude vector — guard that to 0 as well.
        if (query.IsEmpty || candidate.IsEmpty || query.Length != candidate.Length)
        {
            return 0f;
        }

        var score = TensorPrimitives.CosineSimilarity(query.Span, candidate.Span);
        return float.IsNaN(score) ? 0f : score;
    }

    private void StoreInCache(EmbeddingCacheKey key, ReadOnlyMemory<float> vector)
    {
        var bound = _options.EmbeddingCacheMaxEntries;
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

    private readonly record struct EmbeddingCacheKey(Guid ActionId, int Version, string Model);

    private readonly record struct ScoredCandidate(PlaybookActionRecord Action, float Score);
}
