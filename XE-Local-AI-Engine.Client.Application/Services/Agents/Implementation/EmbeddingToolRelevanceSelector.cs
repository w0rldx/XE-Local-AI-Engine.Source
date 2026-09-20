namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using System.Numerics.Tensors;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Common.Caching;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     Embedding-backed <see cref="IToolRelevanceSelector" />, in the shape of
///     <see cref="EmbeddingPlaybookRetrievalRanker" />: it ranks the NON-CORE candidates by cosine similarity between
///     the node-local embedding of the turn's query and of each candidate's name and description.
/// </summary>
/// <remarks>
///     Embeddings come from the node-local <see cref="ILocalModelProvider" /> ONLY, so tool descriptions and the
///     user's message never leave the node, and the vectors live in a RAM-only cache. Gated on
///     <see cref="ToolRelevanceOptions.EmbeddingModelName" />, so the shipped default is model-free. BOUNDED, running
///     inside the send: <see cref="ToolRelevanceOptions.EmbeddingTimeout" /> rides a linked <c>CancelAfter</c> whose
///     expiry DEGRADES to lexical — the hop passes <c>CancellationToken.None</c>, the only cancellation possible.
/// </remarks>
public sealed class EmbeddingToolRelevanceSelector : IToolRelevanceSelector
{
    // Byte ceiling on the vector cache beside the configured entry bound — the same pair, and reason, as the playbook
    // ranker's: an entry bound alone lets a wide-vector model multiply the footprint silently.
    private const long EmbeddingCacheMaxBytes = 4L * 1024 * 1024;

    // Flat allowance per entry for the key struct plus dictionary node — the budget bounds RAM, it does not measure it.
    private const long EntryOverheadBytes = 64;

    // RAM-only, keyed by tool name, description and embedding model. The description stands in for the playbook
    // cache's Version, so an edited one cannot score a stale vector; the model name guards the dimension.
    private readonly ByteBudgetedCache<EmbeddingCacheKey, ReadOnlyMemory<float>> _cache;
    private readonly LexicalToolRelevanceSelector _lexical;
    private readonly ILogger<EmbeddingToolRelevanceSelector> _logger;
    private readonly ToolRelevanceOptions _options;
    private readonly ILocalModelProviderResolver _providerResolver;

    public EmbeddingToolRelevanceSelector(ILocalModelProviderResolver providerResolver,
        IOptions<ToolRelevanceOptions> options,
        LexicalToolRelevanceSelector lexical,
        ILogger<EmbeddingToolRelevanceSelector> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(providerResolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _providerResolver = providerResolver;
        _options = options.Value;
        _lexical = lexical;
        _logger = logger;
        _cache = new ByteBudgetedCache<EmbeddingCacheKey, ReadOnlyMemory<float>>(EmbeddingCacheMaxBytes,
            _options.EmbeddingCacheMaxEntries,
            static (key, vector) => (vector.Length * sizeof(float))
                                    + ((key.Model.Length + key.ToolName.Length + (key.Description?.Length ?? 0)) * sizeof(char))
                                    + EntryOverheadBytes,
            timeProvider);
    }

    /// <inheritdoc />
    public async Task<ToolRelevanceSelection> SelectAsync(string? query,
        IReadOnlyList<ToolRelevanceCandidate> candidates,
        int threshold,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Disabled gate, and the two fast paths: no embedding model, at or below the threshold, or a blank query all
        // resolve without constructing any embedding client and without touching the runtime.
        var model = _options.EmbeddingModelName;
        if (string.IsNullOrWhiteSpace(model) || candidates.Count <= threshold || string.IsNullOrWhiteSpace(query))
        {
            return await _lexical.SelectAsync(query, candidates, threshold, cancellationToken);
        }

        // The selector's OWN bound. The relevance hop calls this under CancellationToken.None — the decision is shared
        // between rounds and must not die with whichever caller arrived first — so this is the only token in play.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.EmbeddingTimeout);

        try
        {
            return await RankByEmbeddingAsync(query, candidates, threshold, model, timeout.Token, cancellationToken);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The bound fired: a cold model load, or a re-ensure parked behind a profiling run. Degrade — the turn is
            // already streaming towards its first token and must not fail for a ranking.
            return await FallBackToLexicalAsync(query, candidates, threshold, exception: null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A real caller cancel, as the playbook ranker does. Unreachable while the hop passes None, and written so
            // the guard stays honest if a future revision flows a token in.
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaUnavailableException or InvalidOperationException)
        {
            // Every node-local embedding hiccup lands here: an unpulled model, a downed runtime, a spent profiling
            // retry, a transport error, an unregistered provider. None may break a send or hide a tool.
            return await FallBackToLexicalAsync(query, candidates, threshold, exception, cancellationToken);
        }
    }

    private static string CandidateText(ToolRelevanceCandidate candidate)
    {
        return $"{candidate.Name} {candidate.Description}";
    }

    private static float CosineScore(ReadOnlyMemory<float> query, ReadOnlyMemory<float> candidate)
    {
        // Mismatched-dimension or empty vectors carry no comparable signal; treat as no overlap so the deterministic
        // index tiebreak orders them. CosineSimilarity returns NaN for a zero-magnitude vector — guard that to 0 too.
        if (query.IsEmpty || candidate.IsEmpty || query.Length != candidate.Length)
        {
            return 0f;
        }

        var score = TensorPrimitives.CosineSimilarity(query.Span, candidate.Span);
        return float.IsNaN(score) ? 0f : score;
    }

    // The single lexical-fallback site: one text-free Warning, then the deterministic lexical selection, which offers
    // what an unconfigured node would have. A null exception is the non-exceptional degrade, such as a short response.
    private Task<ToolRelevanceSelection> FallBackToLexicalAsync(string? query,
        IReadOnlyList<ToolRelevanceCandidate> candidates,
        int threshold,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        // The exception OBJECT never reaches the sink: it failed carrying the query and the tool descriptions, so a
        // transport error echoing its request body would write raw trajectory content. The TYPE name is the diagnosis.
        _logger.LogWarning("Embedding-based tool-relevance selection failed for {CandidateCount} candidates ({FailureType}); falling back to lexical ranking for this turn.",
            candidates.Count,
            exception?.GetType().Name ?? "none");
        return _lexical.SelectAsync(query, candidates, threshold, cancellationToken);
    }

    /// <param name="cancellationToken">The selector's own bounded token, which the embedding round-trip runs under.</param>
    /// <param name="callerToken">
    ///     The token <see cref="SelectAsync" /> was handed, for the lexical degrade: the bounded one would throw once
    ///     expired and the caller's catch would degrade twice.'
    /// </param>
    private async Task<ToolRelevanceSelection> RankByEmbeddingAsync(string query,
        IReadOnlyList<ToolRelevanceCandidate> candidates,
        int threshold,
        string model,
        CancellationToken cancellationToken,
        CancellationToken callerToken)
    {
        // Node-local BY CONSTRUCTION: the resolver hands back an ILocalModelProvider, so no cloud client is reachable
        // even from a mis-set EmbeddingProviderName — an unregistered name throws and the caller degrades.
        var provider = _providerResolver.ResolveProvider(_options.EmbeddingProviderName);
        using var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
        {
            ModelName = model,
            ProviderName = _options.EmbeddingProviderName
        });

        // Core is never ranked and never trimmed, so it is never embedded either: the round-trip carries only the
        // candidates a ranking can actually move.
        var rankable = new List<int>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            if (!candidates[index].IsCore)
            {
                rankable.Add(index);
            }
        }

        var rankedSlots = Math.Max(threshold - (candidates.Count - rankable.Count), _options.MinimumRankedSlots);
        if (rankable.Count <= rankedSlots)
        {
            // Nothing can be hidden, so there is nothing to rank: skip the embedding round-trip entirely rather than
            // stand up a model to reorder a set that will be offered whole.
            return ToolRelevanceSelection.Compose(candidates, rankable.ToHashSet());
        }

        var keys = new EmbeddingCacheKey[rankable.Count];
        var textByKey = new Dictionary<EmbeddingCacheKey, string>(rankable.Count);
        for (var position = 0; position < rankable.Count; position++)
        {
            var candidate = candidates[rankable[position]];
            keys[position] = new EmbeddingCacheKey(candidate.Name, candidate.Description, model);
            textByKey[keys[position]] = CandidateText(candidate);
        }

        // The cache resolves what it can, waiting on a concurrent turn already embedding the same tool, and the
        // remaining misses plus the query go out as ONE batch. The query is always re-embedded and never cached.
        var queryVector = ReadOnlyMemory<float>.Empty;
        var candidateVectors = await _cache.GetOrAddManyAsync(keys, EmbedMissingCandidatesAsync, cancellationToken);

        if (candidateVectors is null)
        {
            return await FallBackToLexicalAsync(query, candidates, threshold, exception: null, callerToken);
        }

        var selected = rankable
                       .Select((candidateIndex, position) => (Index: candidateIndex, Score: CosineScore(queryVector, candidateVectors[position])))
                       .OrderByDescending(static scored => scored.Score)
                       .ThenBy(static scored => scored.Index)
                       .Take(rankedSlots)
                       .Select(static scored => scored.Index)
                       .ToHashSet();

        return ToolRelevanceSelection.Compose(candidates, selected);

        async Task<IReadOnlyList<ReadOnlyMemory<float>>?> EmbedMissingCandidatesAsync(IReadOnlyList<EmbeddingCacheKey> missing,
            CancellationToken token)
        {
            var batchTexts = new List<string>(missing.Count + 1);
            foreach (var key in missing)
            {
                batchTexts.Add(textByKey[key]);
            }

            batchTexts.Add(query);

            var generated = await generator.GenerateAsync(batchTexts, options: null, token);

            // A well-behaved generator returns one embedding per input, in order; a short response would make the
            // positional indexing throw outside the narrow catch set, so signal a degrade instead. Nothing is logged.
            if (generated.Count != batchTexts.Count)
            {
                return null;
            }

            queryVector = generated[^1].Vector;
            var vectors = new ReadOnlyMemory<float>[missing.Count];
            for (var position = 0; position < missing.Count; position++)
            {
                vectors[position] = generated[position].Vector;
            }

            return vectors;
        }
    }

    private readonly record struct EmbeddingCacheKey(string ToolName, string? Description, string Model);
}
