namespace XE_Local_AI_Engine.Tests.Integration;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class EmbeddingSmokeTests
{
    private const string ChatModel = "qwen3.5:0.8b";
    private const string EmbeddingModel = "qwen3-embedding:0.6b";
    private const int ExpectedEmbeddingDimensions = 4096;

    [Test]
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
                    await using var rootFactory = new TestingWebAppFactory();
                    await using var factory = rootFactory.WithWebHostBuilder(builder =>
                    {
                        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                        {
                            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["ConnectionStrings:chat"] = chatConnectionString,
                                ["ConnectionStrings:embeddings"] = embeddingsConnectionString,
                                ["Aspire:OllamaSharp:chat:Endpoint"] = chatEndpoint,
                                ["Aspire:OllamaSharp:chat:SelectedModel"] = ChatModel,
                                ["Aspire:OllamaSharp:embeddings:Endpoint"] = embeddingsEndpoint,
                                ["Aspire:OllamaSharp:embeddings:SelectedModel"] = EmbeddingModel
                            });
                        });
                    });

                    var generator = factory.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

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
            var endpoint = configuredConnectionString.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
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
