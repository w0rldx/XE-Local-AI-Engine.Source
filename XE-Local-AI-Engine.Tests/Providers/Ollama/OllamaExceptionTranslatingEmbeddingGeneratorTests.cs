namespace XE_Local_AI_Engine.Tests.Providers.Ollama;

using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OllamaSharp.Models.Exceptions;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The embedding decorator is the boundary that keeps OllamaSharp inside the provider project: the two transport
///     failures the SDK raises must reach the application layer as <see cref="OllamaUnavailableException" />
///     with the original cause preserved, while every other exception — and the success path — passes through
///     untouched, so the translation cannot mask a genuine caller error.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class OllamaExceptionTranslatingEmbeddingGeneratorTests
{
    [Test]
    public async Task GenerateAsync_WhenInnerThrowsHttpRequestException_TranslatesAndKeepsTheCause()
    {
        var cause = new HttpRequestException("connection refused");
        using var generator = new OllamaExceptionTranslatingEmbeddingGenerator(ThrowingInner(cause));

        var thrown = await AssertEx.ThrowsAsync<OllamaUnavailableException>(() => generator.GenerateAsync(["text"]));

        AssertEx.True(ReferenceEquals(cause, thrown.InnerException), "the transport failure must be preserved as the inner exception.");
    }

    [Test]
    public async Task GenerateAsync_WhenInnerThrowsOllamaException_TranslatesAndKeepsTheCause()
    {
        var cause = new OllamaException("model is currently loading");
        using var generator = new OllamaExceptionTranslatingEmbeddingGenerator(ThrowingInner(cause));

        var thrown = await AssertEx.ThrowsAsync<OllamaUnavailableException>(() => generator.GenerateAsync(["text"]));

        AssertEx.True(ReferenceEquals(cause, thrown.InnerException), "the SDK failure must be preserved as the inner exception.");
    }

    [Test]
    public async Task GenerateAsync_WhenInnerThrowsAnUnrelatedException_PropagatesUntranslated()
    {
        // A misconfigured model name is a caller error, not a transport outage: translating it would hide the reason
        // behind a "daemon unreachable" message the consumers treat as a degrade-gracefully signal.
        var cause = new InvalidOperationException("no embedding provider registered");
        using var generator = new OllamaExceptionTranslatingEmbeddingGenerator(ThrowingInner(cause));

        var thrown = await AssertEx.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync(["text"]));

        AssertEx.True(ReferenceEquals(cause, thrown), "the unrelated exception must reach the caller as itself.");
    }

    [Test]
    public async Task GenerateAsync_WhenInnerSucceeds_PassesTheEmbeddingsThrough()
    {
        var expected = new GeneratedEmbeddings<Embedding<float>>([
            new Embedding<float>(new float[]
            {
                0.1f,
                0.2f
            })
        ]);
        var inner = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        inner.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(expected));
        using var generator = new OllamaExceptionTranslatingEmbeddingGenerator(inner);

        var embeddings = await generator.GenerateAsync(["text"]);

        AssertEx.Equal(expected: 1, embeddings.Count, "the inner generator's single embedding must pass through.");
        AssertEx.True(ReferenceEquals(expected, embeddings), "the decorator must not re-wrap the inner result.");
    }

    private static IEmbeddingGenerator<string, Embedding<float>> ThrowingInner(Exception failure)
    {
        var inner = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        inner.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(failure);
        return inner;
    }
}
