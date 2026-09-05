namespace XE_Local_AI_Engine.Tests.Integration;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class EmbeddingSmokeTests
{
    private const string ChatModel = "qwen3.5:0.8b";
    private const string EmbeddingModel = "qwen3-embedding:0.6b";
    private const int ExpectedEmbeddingDimensions = 4096;

    [Test]
    // Sets ConnectionStrings__chat / ConnectionStrings__embeddings, which are process-global; serialize on both.
    [NotInParallel(["ConnectionStrings__chat", "ConnectionStrings__embeddings"])]
    public async Task IEmbeddingGenerator_GeneratesEmbedding_WhenLocalOllamaIsEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip.Test("Set RUN_LOCAL_INTEGRATION=true to execute the local Ollama embedding smoke test.");
        }

        await using var fakeOllamaServer = await StartFakeOllamaWhenConnectionStringsAreMissingAsync().ConfigureAwait(false);
        var fallbackEndpoint = fakeOllamaServer?.BaseAddress;
        var chatConnectionString = ResolveOllamaConnectionString("ConnectionStrings__chat", fallbackEndpoint, ChatModel);
        var embeddingsConnectionString = ResolveOllamaConnectionString("ConnectionStrings__embeddings", fallbackEndpoint, EmbeddingModel);
        var chatEndpoint = ResolveOllamaEndpoint("ConnectionStrings__chat", fallbackEndpoint);
        var embeddingsEndpoint = ResolveOllamaEndpoint("ConnectionStrings__embeddings", fallbackEndpoint);

        await WithTemporaryEnvironmentVariableAsync("ConnectionStrings__chat",
            chatConnectionString,
            async () => await WithTemporaryEnvironmentVariableAsync("ConnectionStrings__embeddings",
                embeddingsConnectionString,
                async () =>
                {
                    await using var factory = new TestServerWebAppFactory
                    {
                        AdditionalConfiguration = new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:chat"] = chatConnectionString,
                            ["ConnectionStrings:embeddings"] = embeddingsConnectionString,
                            ["Aspire:OllamaSharp:chat:Endpoint"] = chatEndpoint,
                            ["Aspire:OllamaSharp:chat:SelectedModel"] = ChatModel,
                            ["Aspire:OllamaSharp:embeddings:Endpoint"] = embeddingsEndpoint,
                            ["Aspire:OllamaSharp:embeddings:SelectedModel"] = EmbeddingModel
                        }
                    };

                    // Embeddings are provider-routed now — there is no standalone DI IEmbeddingGenerator.
                    // Resolve the embedding generator through ILocalModelProviderResolver exactly as the production
                    // retrieval path does: pick the embedding model's provider (ollama here) and create its generator.
                    var resolver = factory.Services.GetRequiredService<ILocalModelProviderResolver>();
                    var embeddingProvider = await resolver.ResolveProviderForModelAsync(EmbeddingModel, CancellationToken.None).ConfigureAwait(false);
                    using var generator = embeddingProvider.CreateEmbeddingGenerator(new LocalModelSelection
                    {
                        ModelName = EmbeddingModel,
                        ProviderName = embeddingProvider.ProviderName
                    });

                    var embeddings = await generator.GenerateAsync(["local embedding smoke test"]).ConfigureAwait(false);
                    var embedding = embeddings[0];

                    AssertEx.True(embedding.Dimensions > 0, "Expected a non-empty embedding vector.");
                    AssertEx.Equal(ExpectedEmbeddingDimensions, embedding.Dimensions, "Expected qwen3-embedding:0.6b to produce 4096 dimensions.");
                    AssertEx.True(embedding.Vector.Length == embedding.Dimensions, "Expected vector length to match the embedding dimensions.");
                }).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task WithTemporaryEnvironmentVariableAsync(string name, string value, Func<Task> action)
    {
        var previousValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);

        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previousValue);
        }
    }

    private static async Task<FakeOllamaServer?> StartFakeOllamaWhenConnectionStringsAreMissingAsync()
    {
        var hasChatConnectionString = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__chat"));
        var hasEmbeddingsConnectionString = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__embeddings"));

        if (hasChatConnectionString && hasEmbeddingsConnectionString)
        {
            return null;
        }

        return await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = [ChatModel, EmbeddingModel],
            EmbeddingDimensions = ExpectedEmbeddingDimensions
        }).ConfigureAwait(false);
    }

    private static string ResolveOllamaConnectionString(string environmentVariableName, Uri? fallbackEndpoint, string model)
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable(environmentVariableName);
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        return fallbackEndpoint is null
            ? throw new InvalidOperationException($"Set {environmentVariableName} to 'Endpoint=<url>;Model={model}' or allow the test to start fake Ollama.")
            : $"Endpoint={fallbackEndpoint};Model={model}";
    }

    private static string ResolveOllamaEndpoint(string environmentVariableName, Uri? fallbackEndpoint)
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable(environmentVariableName);
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            var endpointPrefix = "Endpoint=";
            var endpoint = configuredConnectionString.Split(separator: ';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                                     .FirstOrDefault(part => part.StartsWith(endpointPrefix, StringComparison.OrdinalIgnoreCase));

            if (endpoint is not null)
            {
                return endpoint[endpointPrefix.Length..];
            }
        }

        return fallbackEndpoint?.ToString()
               ?? throw new InvalidOperationException($"Set {environmentVariableName} to include Endpoint=<url> or allow the test to start fake Ollama.");
    }
}
