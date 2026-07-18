namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Deterministic, model-free embedding generator for the hermetic retrieval-eval harness (RAG-01). It builds a
///     fixed-width "bag of concepts" vector: every input string is tokenized, each token is mapped through an explicit
///     fixture-supplied synonym→concept dictionary (default: the token maps to itself), and each concept accumulates into
///     the dimension its stable FNV-1a hash selects. Two texts therefore score a high cosine similarity exactly when they
///     share concepts — which, because the mapping is explicit, is fully controllable from the fixture. The
///     <c>search_query:</c> / <c>search_document:</c> intent-prefix tokens the embedding prefixer prepends are dropped as
///     stopwords so a query vector and a document vector are compared on their content concepts only.
///     <para>
///         SCOPE: this measures retrieval MECHANICS and LEXICAL/concept overlap deterministically. It is NOT a real
///         embedding model and does not measure genuine semantic quality — the synonym map is the only "semantics" it
///         has. A model-backed semantic eval is out of scope for this harness (a separate, opt-in, model-gated run).
///     </para>
/// </summary>
internal sealed class DeterministicConceptEmbedder : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly int _dimensions;
    private readonly IReadOnlyDictionary<string, string> _synonymToConcept;

    public DeterministicConceptEmbedder(int dimensions, IReadOnlyDictionary<string, string> synonymToConcept)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentNullException.ThrowIfNull(synonymToConcept);
        _dimensions = dimensions;
        _synonymToConcept = synonymToConcept;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var embeddings = values.Select(Embed).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    private Embedding<float> Embed(string text)
    {
        var vector = new float[_dimensions];
        foreach (var token in RetrievalTokens.Split(text))
        {
            if (RetrievalTokens.IsIntentPrefixStopword(token))
            {
                continue;
            }

            var concept = _synonymToConcept.TryGetValue(token, out var mapped) ? mapped : token;
            var dimension = (int)(RetrievalTokens.Fnv1a(concept) % (uint)_dimensions);
            vector[dimension] += 1f;
        }

        return new Embedding<float>(vector);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
///     Node-local provider fake that hands out the <see cref="DeterministicConceptEmbedder" /> and advertises the
///     configured embedding model so <c>EmbeddingModelResolver</c> resolves it confidently (an exact-name match). Every
///     other provider capability is unused by the ingestion/search embedding path and throws if reached.
/// </summary>
internal sealed class DeterministicEmbeddingProvider : ILocalModelProvider
{
    public const string ModelName = "nomic-embed-text";

    private readonly int _dimensions;
    private readonly IReadOnlyDictionary<string, string> _synonymToConcept;

    public DeterministicEmbeddingProvider(int dimensions, IReadOnlyDictionary<string, string> synonymToConcept)
    {
        _dimensions = dimensions;
        _synonymToConcept = synonymToConcept;
    }

    public string ProviderName => "llamacpp";

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection) =>
        new DeterministicConceptEmbedder(_dimensions, _synonymToConcept);

    public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([
            new LocalModelDescriptor
            {
                ModelName = ModelName,
                ProviderName = "llamacpp",
                IsAvailable = true,
                SizeBytes = 1024,
                ModifiedAt = DateTimeOffset.UnixEpoch,
                MaxContextTokens = null,
                Capabilities = []
            }
        ]);

    public IChatClient CreateChatClient(LocalModelSelection selection) => throw new NotSupportedException();

    public Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct) => throw new NotSupportedException();

    public Task DeleteModelAsync(string modelName, CancellationToken ct) => throw new NotSupportedException();

    public Task WarmModelAsync(string modelName, CancellationToken ct) => throw new NotSupportedException();

    public Task UnloadModelAsync(string modelName, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>
///     Minimal <see cref="ILocalModelProviderResolver" /> that always resolves to a single fixed provider. Only
///     <see cref="ResolveProvider" /> and <see cref="DefaultProvider" /> are exercised by the embedding path; the
///     model→provider routing members are irrelevant to a single-embedding-model harness and throw if reached.
/// </summary>
internal sealed class SingleProviderResolver : ILocalModelProviderResolver
{
    private readonly ILocalModelProvider _provider;

    public SingleProviderResolver(ILocalModelProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public int MaxLoadedProcesses => 1;

    public ILocalModelProvider DefaultProvider => _provider;

    public ILocalModelProvider ResolveProvider(string providerName) => _provider;

    public Task<string> ResolveProviderNameForModelAsync(string modelName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_provider.ProviderName);

    public Task<ILocalModelProvider> ResolveProviderForModelAsync(string modelName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_provider);

    public void InvalidateModelProviderMap()
    {
    }
}

/// <summary>
///     An <see cref="ILocalModelProviderResolver" /> whose <see cref="ResolveProvider" /> throws, forcing the query
///     embedding to fail so the search degrades to the lexical (FTS) arm only. Used to prove the RRF-degrades-to-lexical
///     path and to isolate what the vector arm alone contributes.
/// </summary>
internal sealed class UnavailableProviderResolver : ILocalModelProviderResolver
{
    public int MaxLoadedProcesses => 1;

    public ILocalModelProvider DefaultProvider => throw new InvalidOperationException("No embedding provider is available in this scenario.");

    public ILocalModelProvider ResolveProvider(string providerName) =>
        throw new InvalidOperationException("No embedding provider is available in this scenario.");

    public Task<string> ResolveProviderNameForModelAsync(string modelName, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No embedding provider is available in this scenario.");

    public Task<ILocalModelProvider> ResolveProviderForModelAsync(string modelName, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No embedding provider is available in this scenario.");

    public void InvalidateModelProviderMap()
    {
    }
}
