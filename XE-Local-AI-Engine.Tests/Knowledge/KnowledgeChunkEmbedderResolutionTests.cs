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

        _ = await embedder.EmbedAsync(["chunk one"], CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ConfiguredName, provider.LastSelectedModelName);
    }

    [Test]
    public async Task EmbedAsync_WhenEmbeddingGgufInstalled_UsesGgufName()
    {
        const string ggufName = "nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M";
        var provider = new CapturingProvider(Descriptor("qwen2.5:Q4_K_M"), Descriptor(ggufName));
        var embedder = CreateEmbedder(provider);

        _ = await embedder.EmbedAsync(["chunk one"], CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ggufName, provider.LastSelectedModelName);
    }

    [Test]
    public async Task EmbedAsync_WhenNothingInstalledAndGeneratorFails_UsesConfiguredNameAndSurfacesGracefulFailure()
    {
        var provider = new CapturingProvider { ThrowOnGenerate = true };
        var embedder = CreateEmbedder(provider);

        _ = await AssertEx.ThrowsAsync<KnowledgeIngestionException>(
            () => embedder.EmbedAsync(["chunk one"], CancellationToken.None)).ConfigureAwait(false);

        AssertEx.Equal(ConfiguredName, provider.LastSelectedModelName);
    }

    private static KnowledgeChunkEmbedder CreateEmbedder(ILocalModelProvider provider)
    {
        var options = Options.Create(new KnowledgeBaseOptions
        {
            EmbeddingModelName = ConfiguredName,
            EmbeddingDimension = Dimensions
        });

        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProvider(Arg.Any<string>()).Returns(provider);

        return new KnowledgeChunkEmbedder(providerResolver,
            new EmbeddingModelResolver(options),
            new KnowledgeEmbeddingPrefixer(),
            options);
    }

    private static LocalModelDescriptor Descriptor(string modelName)
    {
        return new LocalModelDescriptor
        {
            ModelName = modelName,
            ProviderName = "llamacpp",
            IsAvailable = true,
            SizeBytes = 1024,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = null,
            Capabilities = []
        };
    }

    // A node-local provider fake that records the model name its embedding generator was created with and returns
    // fixed-dimension zero vectors (or throws a transport error), so the resolution wiring can be asserted without Ollama.
    private sealed class CapturingProvider(params LocalModelDescriptor[] models) : ILocalModelProvider
    {
        public string? LastSelectedModelName { get; private set; }

        public bool ThrowOnGenerate { get; init; }

        public string ProviderName => "llamacpp";

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
        {
            LastSelectedModelName = selection.ModelName;
            return new FixedEmbeddingGenerator(ThrowOnGenerate);
        }

        public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LocalModelDescriptor>>(models);
        }

        public IChatClient CreateChatClient(LocalModelSelection selection) => throw new NotSupportedException();

        public Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteModelAsync(string modelName, CancellationToken ct) => throw new NotSupportedException();

        public Task WarmModelAsync(string modelName, CancellationToken ct) => throw new NotSupportedException();

        public Task UnloadModelAsync(string modelName, CancellationToken ct) => throw new NotSupportedException();

        private sealed class FixedEmbeddingGenerator(bool throwOnGenerate) : IEmbeddingGenerator<string, Embedding<float>>
        {
            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                if (throwOnGenerate)
                {
                    throw new HttpRequestException("fake embedding transport failure");
                }

                var embeddings = values.Select(static _ => new Embedding<float>(new float[Dimensions]));
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
