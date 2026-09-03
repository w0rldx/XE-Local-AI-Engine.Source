namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using System.Numerics.Tensors;
using Microsoft.Extensions.Options;
using OllamaSharp.Models.Exceptions;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Common.Caching;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Embedding-backed <see cref="IToolRelevanceSelector" />, in the same shape as
///     <see cref="EmbeddingPlaybookRetrievalRanker" />: ranks the NON-CORE candidates by cosine similarity between the
///     node-local embedding of the turn's query and of each candidate's <c>name + " " + description</c>. Embeddings come
///     from the node-local <see cref="ILocalModelProvider" /> only — never a shared or cloud client — so tool
///     descriptions and the user's message never leave the node, and the vectors live in a RAM-only cache that is never
///     persisted, logged or returned.
///     <para>
///         Config-gated the same way: with no <see cref="ToolRelevanceOptions.EmbeddingModelName" /> it delegates
///         straight to the lexical selector and constructs no embedding client, so the shipped default is model-free and
///         CI stays deterministic without an embedding process.
///     </para>
///     <para>
///         <b>Bounded, because this one runs inside the send.</b> The playbook ranker ranks at resolve time; this ranks
///         in front of the first token, and for <c>llamacpp</c> the first call stands up an embedding process — which,
///         since the profiling-lease change, may additionally wait out a benchmark body up to three times.
///         <see cref="ToolRelevanceOptions.EmbeddingTimeout" /> is applied here with a linked <c>CancelAfter</c> and its
///         expiry DEGRADES to the lexical ranking. That inverts the playbook ranker's cancellation clause on purpose:
///         there the token is the send's and a cancelled send must not be swallowed, whereas the relevance hop runs this
///         under <c>CancellationToken.None</c>, so the only cancellation that can arrive is this selector's own timeout —
///         the very thing that must degrade rather than throw.
///     </para>
/// </summary>
public sealed class EmbeddingToolRelevanceSelector : IToolRelevanceSelector
{
    // Byte ceiling on the vector cache, alongside the configured entry bound — the same pair of bounds, and for the
    // same reason, as the playbook ranker's cache: the entry bound alone lets a wide-vector model multiply the
    // footprint silently.
    private const long EmbeddingCacheMaxBytes = 4L * 1024 * 1024;

    // Flat allowance per entry for the key struct plus dictionary node — the budget bounds RAM, it does not measure it.
    private const long EntryOverheadBytes = 64;

    // RAM-only tool-vector cache keyed by (tool name, description, embedding model). The description stands in for the
    // playbook cache's Version — an edited description is a different key, so a stale vector can never be scored — and
    // the model name guards against cosine'ing a stale-dimension vector against a new model's query.
    private readonly ByteBudgetedCache<EmbeddingCacheKey, ReadOnlyMemory<float>> _cache;
    private readonly LexicalToolRelevanceSelector _lexical;
    private readonly ILogger<EmbeddingToolRelevanceSelector> _logger;
    private readonly ToolRelevanceOptions _options;
    private readonly ILocalModelProviderResolver _providerResolver;

    public EmbeddingToolRelevanceSelector(ILocalModelProviderResolver providerResolver,
        IOptions<ToolRelevanceOptions> options,
        LexicalToolRelevanceSelector lexical,
        ILogger<EmbeddingToolRelevanceSelector> logger)
    {
        ArgumentNullException.ThrowIfNull(providerResolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(logger);

        _providerResolver = providerResolver;
        _options = options.Value;
        _lexical = lexical;
        _logger = logger;
        _cache = new ByteBudgetedCache<EmbeddingCacheKey, ReadOnlyMemory<float>>(EmbeddingCacheMaxBytes,
            _options.EmbeddingCacheMaxEntries,
            static (key, vector) => (vector.Length * sizeof(float))
                                    + ((key.Model.Length + key.ToolName.Length + (key.Description?.Length ?? 0)) * sizeof(char))
                                    + EntryOverheadBytes);
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
            return await _lexical.SelectAsync(query, candidates, threshold, cancellationToken).ConfigureAwait(false);
        }

        // The selector's OWN bound. The relevance hop calls this under CancellationToken.None (the decision is shared
        // between concurrent rounds and must not be cancellable by whichever caller arrived first), so this linked
        // source is normally the only token in play.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.EmbeddingTimeout);

        try
        {
            return await RankByEmbeddingAsync(query, candidates, threshold, model, timeout.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The bound fired: a cold model load, or a re-ensure parked behind a profiling run. Degrade — the turn is
            // already streaming towards its first token and must not fail for a ranking.
            return await FallBackToLexicalAsync(query, candidates, threshold, exception: null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A real caller cancel, as the playbook ranker does. Unreachable while the hop passes None, and written so
            // the guard stays honest if a future revision flows a token in.
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaException or InvalidOperationException)
        {
            // Every node-local embedding hiccup lands here: model not pulled, runtime down or ejected, a spent
            // profiling retry, a transport error (the deferred llama-server generator wraps its LlamaRuntimeException
            // and its refusals to IOException), or an unregistered EmbeddingProviderName. None is a reason to break a
            // send, and none of them is a reason to hide a tool either — the lexical ranking is a complete answer.
            return await FallBackToLexicalAsync(query, candidates, threshold, exception, cancellationToken).ConfigureAwait(false);
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

    // The single lexical-fallback site: one text-free Warning — no tool name, no description, no query text — and the
    // deterministic lexical selection, which offers exactly what it would have offered had the node never configured an
    // embedding model. A null exception is the non-exceptional degrade (a short or partial embedding response).
    private Task<ToolRelevanceSelection> FallBackToLexicalAsync(string? query,
        IReadOnlyList<ToolRelevanceCandidate> candidates,
        int threshold,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        // The exception OBJECT never reaches the sink: sinks render Message and every inner exception, and this call
        // failed while carrying the query and the tool descriptions, so a transport error that echoes its request body
        // would write raw trajectory content to disk under a template that was scrubbed to counts. The TYPE name is
        // the whole allow-listed diagnosis; "none" is the non-exceptional degrade.
        _logger.LogWarning("Embedding-based tool-relevance selection failed for {CandidateCount} candidates ({FailureType}); falling back to lexical ranking for this turn.",
            candidates.Count,
            exception?.GetType().Name ?? "none");
        return _lexical.SelectAsync(query, candidates, threshold, cancellationToken);
    }

    /// <param name="cancellationToken">The selector's own bounded token — the one the embedding round-trip runs under.</param>
    /// <param name="callerToken">
    ///     The token <see cref="SelectAsync" /> was handed, used for the lexical degrade below. The bounded token would
    ///     make an in-bound degrade throw once the bound had already expired, and the caller's catch would then degrade
    ///     a SECOND time with the right token — one degrade, two warnings.
    /// </param>
    private async Task<ToolRelevanceSelection> RankByEmbeddingAsync(string query,
        IReadOnlyList<ToolRelevanceCandidate> candidates,
        int threshold,
        string model,
        CancellationToken cancellationToken,
        CancellationToken callerToken)
    {
        // Node-local BY CONSTRUCTION: the resolver hands back an ILocalModelProvider, so there is no cloud client this
        // path could reach even if EmbeddingProviderName were mis-set — an unregistered name throws
        // InvalidOperationException, which the caller catches as a degrade.
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

        // The cache resolves what it can (and waits on a concurrent turn already embedding the same tool); the
        // remaining misses plus the query go out as ONE batch, so a turn costs a single embedding round-trip. The query
        // is always re-embedded and never cached.
        var queryVector = ReadOnlyMemory<float>.Empty;
        var candidateVectors = await _cache.GetOrAddManyAsync(keys, EmbedMissingCandidatesAsync, cancellationToken).ConfigureAwait(false);

        if (candidateVectors is null)
        {
            return await FallBackToLexicalAsync(query, candidates, threshold, exception: null, callerToken).ConfigureAwait(false);
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

            var generated = await generator.GenerateAsync(batchTexts, options: null, token).ConfigureAwait(false);

            // A well-behaved generator returns exactly one embedding per input, in order. A short or partial response
            // would make the positional indexing throw outside the narrow catch set; signal a degrade instead. No tool
            // or query text is logged.
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
