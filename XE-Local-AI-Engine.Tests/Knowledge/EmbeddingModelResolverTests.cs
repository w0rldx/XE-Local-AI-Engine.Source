namespace XE_Local_AI_Engine.Tests.Knowledge;

using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The embedding-model resolver maps the configured (Ollama-style) embedding name to a model actually installed on
///     the resolved provider so knowledge-base embedding works out of the box on either runtime:
///     - exact configured name installed → keep it (an Ollama node is unaffected);
///     - no exact match but an embedding-named GGUF installed → use that GGUF (a llama.cpp node);
///     - nothing matching installed → keep the configured name (the caller's graceful failure path fires);
///     - a transport failure while listing models degrades to the configured name, never throwing.
/// </summary>
public sealed class EmbeddingModelResolverTests
{
    private const string ConfiguredName = "nomic-embed-text";

    [Test]
    public async Task ResolveAsync_WhenExactConfiguredNameInstalled_ReturnsConfiguredName()
    {
        var provider = ProviderWithModels(Descriptor("nomic-embed-text"), Descriptor("qwen2.5:Q4_K_M"));
        var resolver = CreateResolver();

        var resolved = await resolver.ResolveAsync(provider, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ConfiguredName, resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenNoExactMatchButEmbeddingGgufInstalled_ReturnsGgufName()
    {
        var provider = ProviderWithModels(Descriptor("qwen2.5:Q4_K_M"),
            Descriptor("nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M"));
        var resolver = CreateResolver();

        var resolved = await resolver.ResolveAsync(provider, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal("nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenMultipleEmbeddingGgufsInstalled_PicksFirstByOrdinalName()
    {
        var provider = ProviderWithModels(Descriptor("mxbai-embed-large:Q8_0"),
            Descriptor("bge-small:Q4"));
        var resolver = CreateResolver();

        var resolved = await resolver.ResolveAsync(provider, CancellationToken.None).ConfigureAwait(false);

        // Deterministic ordinal-ignore-case order: "bge-small:Q4" sorts before "mxbai-embed-large:Q8_0".
        AssertEx.Equal("bge-small:Q4", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenNothingMatchingInstalled_ReturnsConfiguredName()
    {
        var provider = ProviderWithModels(Descriptor("qwen2.5:Q4_K_M"), Descriptor("llama-3.1-8b-instruct:Q6_K"));
        var resolver = CreateResolver();

        var resolved = await resolver.ResolveAsync(provider, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ConfiguredName, resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenNoModelsInstalled_ReturnsConfiguredName()
    {
        var provider = ProviderWithModels();
        var resolver = CreateResolver();

        var resolved = await resolver.ResolveAsync(provider, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ConfiguredName, resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenListModelsThrowsTransportError_DegradesToConfiguredName()
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyList<LocalModelDescriptor>>>(_ => throw new HttpRequestException("provider down"));
        var resolver = CreateResolver();

        var resolved = await resolver.ResolveAsync(provider, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ConfiguredName, resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenCallerCancels_PropagatesOperationCanceledException()
    {
        // A genuine caller cancellation (the token that fired IS the caller's token) must propagate — it is not a
        // transport timeout and must not be swallowed into a silent "degrade to configured name".
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyList<LocalModelDescriptor>>>(_ => throw new OperationCanceledException(cts.Token));
        var resolver = CreateResolver();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(provider, cts.Token)).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveAsync_WhenListModelsThrowsTaskCanceledWithoutCallerCancellation_DegradesToConfiguredName()
    {
        // TaskCanceledException (an OperationCanceledException subtype) with the CALLER's token still unset is an
        // HttpClient/provider request timeout, not a real cancellation — it must degrade like the other transport
        // failures, never propagate and crash ingestion.
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyList<LocalModelDescriptor>>>(_ => throw new TaskCanceledException("provider request timed out"));
        var resolver = CreateResolver();

        var resolved = await resolver.ResolveAsync(provider, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(ConfiguredName, resolved);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static EmbeddingModelResolver CreateResolver()
    {
        return new EmbeddingModelResolver(Options.Create(new KnowledgeBaseOptions { EmbeddingModelName = ConfiguredName }));
    }

    private static ILocalModelProvider ProviderWithModels(params LocalModelDescriptor[] models)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>(models));
        return provider;
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
}
