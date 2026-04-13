namespace XE_Local_AI_Engine.Tests.Integration;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class EmbeddingSmokeTests
{
    [Test]
    public async Task IEmbeddingGenerator_GeneratesEmbedding_WhenLocalOllamaIsEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip.Test("Set RUN_LOCAL_INTEGRATION=true to execute the local Ollama embedding smoke test.");
        }

        var chatConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__chat");
        if (string.IsNullOrWhiteSpace(chatConnectionString))
        {
            Skip.Test("Set ConnectionStrings__chat to a valid Ollama chat connection string.");
        }

        var embeddingsConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__embeddings");
        if (string.IsNullOrWhiteSpace(embeddingsConnectionString))
        {
            Skip.Test("Set ConnectionStrings__embeddings to a valid Ollama embeddings connection string.");
        }

        await using var rootFactory = new TestingWebAppFactory();
        await using var factory = rootFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:chat"] = chatConnectionString,
                    ["ConnectionStrings:embeddings"] = embeddingsConnectionString
                });
            });
        });

        var generator = factory.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        var embeddings = await generator.GenerateAsync(["local embedding smoke test"]);
        var embedding = embeddings[0];

        AssertEx.True(embedding.Dimensions > 0, "Expected a non-empty embedding vector.");
        AssertEx.Equal(768, embedding.Dimensions, "Expected nomic-embed-text to produce 768 dimensions.");
        AssertEx.True(embedding.Vector.Length == embedding.Dimensions, "Expected vector length to match the embedding dimensions.");
    }
}
