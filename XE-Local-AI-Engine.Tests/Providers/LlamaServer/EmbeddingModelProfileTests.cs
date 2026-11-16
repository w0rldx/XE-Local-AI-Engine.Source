namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the embedding profile table (prompt strings, pooling and identity tag per profile) and the one name matcher
///     that picks a profile from the model segment of an installed name.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class EmbeddingModelProfileTests
{
    // One installed name per profile, so the table is reachable without exposing it.
    private static readonly string[] OneNamePerProfile =
    [
        "nomic-ai/nomic-embed-text-v1.5-GGUF:F16",
        "ggml-org/embeddinggemma-2-GGUF:Q8_0",
        "CompendiumLabs/bge-m3-gguf:F16",
        "my-embed:Q8_0"
    ];

    [Test]
    [Arguments("nomic-ai/nomic-embed-text-v1.5-GGUF:F16", "nomic", "search_document: ", "search_query: ", "mean", null)]
    [Arguments("ggml-org/embeddinggemma-2-GGUF:Q8_0", "embeddinggemma", "title: none | text: ", "task: search result | query: ", "mean", "prompt-embeddinggemma+pool-mean:v1")]
    [Arguments("CompendiumLabs/bge-m3-gguf:F16", "bge-m3", "", "", "cls", "prompt-none+pool-cls:v1")]
    [Arguments("my-embed:Q8_0", "plain", "", "", "mean", "prompt-none+pool-mean:v1")]
    public void For_PinsEachProfilesPromptPoolingAndTag(string modelName, string id, string documentPrefix, string queryPrefix, string pooling, string? tag)
    {
        var profile = EmbeddingModelProfile.For(modelName);

        const string Bump = "a prefix or pooling change bumps the profile's tag version (:vN); a nomic change needs a tag and reindexes every nomic corpus";
        AssertEx.Equal(id, profile.Id);
        AssertEx.Equal(documentPrefix, profile.DocumentPrefix, Bump);
        AssertEx.Equal(queryPrefix, profile.QueryPrefix, Bump);
        AssertEx.Equal(pooling, profile.Pooling, Bump);
        AssertEx.Equal<string?>(tag, profile.IdentityTag, Bump);
    }

    [Test]
    public void EveryTag_NamesItsPooling_SoAPoolingChangeCannotKeepItsTag()
    {
        foreach (var profile in OneNamePerProfile.Select(EmbeddingModelProfile.For).Where(static profile => profile.IdentityTag is not null))
        {
            AssertEx.Contains(profile.IdentityTag, $"pool-{profile.Pooling}", StringComparison.Ordinal, $"profile '{profile.Id}'");
        }
    }

    [Test]
    public void EveryDocumentPrefix_FitsTheChunkerReserve()
    {
        foreach (var profile in OneNamePerProfile.Select(EmbeddingModelProfile.For))
        {
            AssertEx.True(ChunkTokenApproximation.EstimateTokens(profile.DocumentPrefix) <= 16,
                $"profile '{profile.Id}' document prefix outgrows the chunker's embedding window reserve.");
        }
    }

    [Test]
    [Arguments("nomic-ai/nomic-embed-text-v1-GGUF:F16", "nomic")]
    [Arguments("nomic-ai/nomic-embed-text-v1.5-GGUF:Q8_0", "nomic")]
    [Arguments("nomic-ai/nomic-embed-text-v2-moe-GGUF:Q8_0", "nomic")]
    [Arguments("ggml-org/embeddinggemma-300M-GGUF:Q8_0", "embeddinggemma")]
    [Arguments("ggml-org/embeddinggemma-2-GGUF:BF16", "embeddinggemma")]
    [Arguments("hf.co/ggml-org/embeddinggemma-2-GGUF:Q8_0", "embeddinggemma")]
    [Arguments("embeddinggemma-2-Q8_0", "embeddinggemma")]
    [Arguments("embeddinggemma-2-Q8_0:Q8_0", "embeddinggemma")]
    [Arguments("EMBEDDINGGEMMA-300M:Q8_0", "embeddinggemma")]
    [Arguments("Embeddinggemma-300m:Q8_0", "embeddinggemma")]
    [Arguments("CompendiumLabs/bge-m3-gguf:Q8_0", "bge-m3")]
    // Qwen3-Embedding stays plain until its GGUF's add_eos_token is verified for last-token pooling.
    [Arguments("Qwen/Qwen3-Embedding-0.6B-GGUF:Q8_0", "plain")]
    // A token only in the owner, only after the first colon, or missing from a renamed file or adapter name does not match.
    [Arguments("nomic-ai/other-model:Q8_0", "plain")]
    [Arguments("nomic-embed-text-fan/some-chat-model:Q4_K_M", "plain")]
    [Arguments("my-embed:nomic-embed-text", "plain")]
    [Arguments("my-embed:Q8_0", "plain")]
    [Arguments("my-kb-lora-adapter", "plain")]
    public void For_MatchesTheModelSegmentOnly(string modelName, string expectedId)
    {
        var profile = EmbeddingModelProfile.For(modelName);

        AssertEx.Equal(expectedId, profile.Id);
        AssertEx.Equal(expectedId == "plain", profile.IsFallback);
    }

    [Test]
    [Arguments("org/repo-GGUF:Q8_0", "repo-GGUF")]
    [Arguments("hf.co/org/repo:Q8_0", "repo")]
    [Arguments("a:b:c", "a")]
    [Arguments("bare-name", "bare-name")]
    public void ModelSegment_IsAfterTheLastSlash_CutAtTheFirstColon(string modelName, string expected)
    {
        AssertEx.Equal(expected, EmbeddingModelProfile.ModelSegment(modelName));
    }

    [Test]
    public void EveryBundledCatalogEmbedder_MapsToItsExpectedProfile()
    {
        // A new catalog embedder must be given its profile here deliberately, or it silently embeds as plain.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nomic-embed-text-v1.5"] = "nomic",
            ["nomic-embed-text-v2-moe"] = "nomic",
            ["embeddinggemma-300m"] = "embeddinggemma",
            ["bge-m3"] = "bge-m3",
            ["qwen3-embedding-0.6b"] = "plain",
            ["qwen3-embedding-4b"] = "plain"
        };
        var embedders = ModelCatalogBundledLoader.Load(NullLogger.Instance)
                                                 .Models
                                                 .Where(static entry => entry.UseCases.Contains("embedding"))
                                                 .ToList();

        AssertEx.Equal(string.Join(", ", expected.Keys.Order(StringComparer.Ordinal)),
            string.Join(", ", embedders.Select(static entry => entry.Id).Order(StringComparer.Ordinal)));
        foreach (var entry in embedders)
        {
            AssertEx.Equal(expected[entry.Id], EmbeddingModelProfile.For($"{entry.GgufRepo}:Q8_0").Id, entry.Id);
        }
    }
}
