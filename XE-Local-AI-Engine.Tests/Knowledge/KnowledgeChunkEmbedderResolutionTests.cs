namespace XE_Local_AI_Engine.Tests.Knowledge;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The chunk embedder resolves the configured (Ollama-style) embedding name against the models installed on the
///     resolved provider before creating the generator, so the same nomic weights work whether they are installed under
///     the Ollama name or a llama.cpp <c>&lt;repo&gt;:&lt;quant&gt;</c> GGUF name. The name actually handed to
///     <see cref="ILocalModelProvider.CreateEmbeddingGenerator" /> is asserted, and the graceful content-free failure is
///     preserved when the generator itself fails.
/// </summary>
public sealed class KnowledgeChunkEmbedderResolutionTests
{
    private const int Dimensions = 768;
    private const string ConfiguredName = "nomic-embed-text";

    [Test]
    public async Task EmbedAsync_WhenExactConfiguredNameInstalled_UsesConfiguredName()
    {
        var provider = new CapturingProvider(Descriptor(ConfiguredName), Descriptor("qwen2.5:Q4_K_M"));
        var embedder = CreateEmbedder(provider);

        var result = await embedder.EmbedAsync(["chunk one"], CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ConfiguredName, provider.LastSelectedModelName);
        AssertEx.Equal(ConfiguredName, result.ResolvedModel);
        AssertEx.Equal(1, result.Vectors.Count);
        // Dimension is derived from the produced vector, not a static config constant.
        AssertEx.Equal(Dimensions, result.Dimension);
    }

    [Test]
    public async Task EmbedAsync_WhenModelWidthIsNotTheLegacyDefault_DerivesItAndSucceeds()
    {
        // Regression: a 1024-wide model (bge-m3/mxbai) previously failed every chunk against the static 768 constant.
        const int width = 1024;
        var provider = new CapturingProvider(Descriptor(ConfiguredName))
        {
            VectorDimensions = [width]
        };
        var embedder = CreateEmbedder(provider);

        var result = await embedder.EmbedAsync(["chunk one", "chunk two"], CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(2, result.Vectors.Count);
        AssertEx.Equal(width, result.Dimension);
        // Each blob is width float32 values → width * 4 bytes.
        AssertEx.Equal(width * sizeof(float), result.Vectors[0].Length);
    }

    [Test]
    public async Task EmbedAsync_WhenModelReturnsInconsistentWidths_ThrowsContentFreeFailure()
    {
        // A dimension-stable model never does this; an inconsistent width within one run is a genuinely broken model.
        var provider = new CapturingProvider(Descriptor(ConfiguredName))
        {
            VectorDimensions = [1024, 512]
        };
        var embedder = CreateEmbedder(provider);

        var exception = await AssertEx.ThrowsAsync<KnowledgeIngestionException>(() => embedder.EmbedAsync(["chunk one", "chunk two"], CancellationToken.None)).ConfigureAwait(false);

        // Reason names the mismatch (integers only, content-free) so an operator can act.
        AssertEx.True(exception.Reason.Contains("1024", StringComparison.Ordinal), "Reason should name the expected width.");
        AssertEx.True(exception.Reason.Contains("512", StringComparison.Ordinal), "Reason should name the observed width.");
    }

    [Test]
    public async Task EmbedAsync_WhenEmbeddingGgufInstalled_UsesGgufName()
    {
        const string ggufName = "nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M";
        var provider = new CapturingProvider(Descriptor("qwen2.5:Q4_K_M"), Descriptor(ggufName));
        var embedder = CreateEmbedder(provider);

        var result = await embedder.EmbedAsync(["chunk one"], CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ggufName, provider.LastSelectedModelName);
        // The resolved (GGUF) name — not the configured name — is what the ingestion lane stamps as the vector scope key.
        AssertEx.Equal(ggufName, result.ResolvedModel);
        AssertEx.Equal(512, result.Dimension);
        AssertEx.Equal(512 * sizeof(float), result.Vectors[0].Length);
        AssertEx.True(result.VectorIdentity.Contains("layernorm-population-eps1e-5-truncate-l2:v1:512", StringComparison.Ordinal));
    }

    [Test]
    public async Task EmbedAsync_WhenNothingInstalledAndGeneratorFails_UsesConfiguredNameAndSurfacesGracefulFailure()
    {
        var provider = new CapturingProvider
        {
            ThrowOnGenerate = true
        };
        var embedder = CreateEmbedder(provider);

        _ = await AssertEx.ThrowsAsync<KnowledgeIngestionException>(() => embedder.EmbedAsync(["chunk one"], CancellationToken.None)).ConfigureAwait(false);

        AssertEx.Equal(ConfiguredName, provider.LastSelectedModelName);
    }

    [Test]
    public async Task ResolveEmbeddingContextWindowAsync_WhenResolvedModelAdvertisesAWindow_ReturnsIt()
    {
        var provider = new CapturingProvider(Descriptor(ConfiguredName, maxContextTokens: 2048));
        var embedder = CreateEmbedder(provider);

        var window = await embedder.ResolveEmbeddingContextWindowAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(2048, window);
    }

    [Test]
    public async Task ResolveEmbeddingContextWindowAsync_WhenResolvedModelAdvertisesNoWindow_ReturnsNull()
    {
        var provider = new CapturingProvider(Descriptor(ConfiguredName));
        var embedder = CreateEmbedder(provider);

        var window = await embedder.ResolveEmbeddingContextWindowAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(window);
    }

    [Test]
    public async Task ResolveEmbeddingContextWindowAsync_WhenResolutionIsNotConfident_ReturnsNull()
    {
        // Nothing installed matches the configured name and no embedding-named model is present, so the resolution is a
        // bare fallback (not confident) — its window is unknown and must not be guessed from an unrelated model.
        var provider = new CapturingProvider(Descriptor("qwen2.5:Q4_K_M", maxContextTokens: 32768));
        var embedder = CreateEmbedder(provider);

        var window = await embedder.ResolveEmbeddingContextWindowAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(window);
    }

    private static KnowledgeChunkEmbedder CreateEmbedder(ILocalModelProvider provider)
    {
        var options = Options.Create(new KnowledgeBaseOptions
        {
            EmbeddingModelName = ConfiguredName
        });

        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProvider(Arg.Any<string>()).Returns(provider);

        return new KnowledgeChunkEmbedder(providerResolver,
            new EmbeddingModelResolver(options),
            new KnowledgeEmbeddingPrefixer(),
            options);
    }

    private static LocalModelDescriptor Descriptor(string modelName, int? maxContextTokens = null)
    {
        return new LocalModelDescriptor
        {
            ModelName = modelName,
            ProviderName = "llamacpp",
            IsAvailable = true,
            SizeBytes = 1024,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = maxContextTokens,
            Capabilities = []
        };
    }

    // A node-local provider fake that records the model name its embedding generator was created with and returns
    // fixed-dimension zero vectors (or throws a transport error), so the resolution wiring can be asserted without Ollama.
    private sealed class CapturingProvider(params LocalModelDescriptor[] models) : ILocalModelProvider
    {
        public string? LastSelectedModelName { get; private set; }

        public bool ThrowOnGenerate { get; init; }

        // Width of each produced vector, by position; the last entry repeats when more vectors than entries are generated.
        // Defaults to the shipped nomic width so the resolution tests are unaffected; overridden to exercise arbitrary and
        // inconsistent widths.
        public IReadOnlyList<int> VectorDimensions { get; init; } = [Dimensions];

        public string ProviderName => "llamacpp";

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
        {
            LastSelectedModelName = selection.ModelName;
            return new FixedEmbeddingGenerator(ThrowOnGenerate, VectorDimensions);
        }

        public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LocalModelDescriptor>>(models);
        }

        public IChatClient CreateChatClient(LocalModelSelection selection) =>
            throw new NotSupportedException();

        public Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteModelAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task WarmModelAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UnloadModelAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();

        private sealed class FixedEmbeddingGenerator(bool throwOnGenerate, IReadOnlyList<int> dimensions) : IEmbeddingGenerator<string, Embedding<float>>
        {
            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                if (throwOnGenerate)
                {
                    throw new HttpRequestException("fake embedding transport failure");
                }

                var embeddings = values.Select((_, index) =>
                    new Embedding<float>(new float[dimensions[Math.Min(index, dimensions.Count - 1)]]));
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
            }

            public object? GetService(Type serviceType, object? serviceKey = null) =>
                null;

            public void Dispose()
            {
            }
        }
    }
}
