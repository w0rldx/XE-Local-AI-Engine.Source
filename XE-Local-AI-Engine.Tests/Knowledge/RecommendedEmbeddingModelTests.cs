namespace XE_Local_AI_Engine.Tests.Knowledge;

using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="RecommendedEmbeddingModel.PickInstalled" /> is the auto-pick an unpinned node embeds with and the
///     download endpoint reports, so it must never depend on inventory order.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class RecommendedEmbeddingModelTests
{
    private const string Bf16 = "ggml-org/embeddinggemma-2-GGUF:BF16";
    private const string BgeM3 = "CompendiumLabs/bge-m3-gguf:F16";
    private const string Nomic = "nomic-ai/nomic-embed-text-v1.5-GGUF:F16";

    [Test]
    public void PickInstalled_CanonicalQ8_0BeatsBf16OfTheSameRepo_InBothInventoryOrders()
    {
        // BF16 sorts before Q8_0, so only the canonical-name step can pick Q8_0 here.
        foreach (var inventory in BothOrders(Bf16, RecommendedEmbeddingModel.CanonicalModelName))
        {
            AssertEx.Equal("ggml-org/embeddinggemma-2-GGUF:Q8_0", RecommendedEmbeddingModel.PickInstalled(inventory)?.ModelName);
        }
    }

    [Test]
    public void PickInstalled_AnyQuantOfTheRecommendedRepoBeatsAnEarlierSortedEmbedder_InBothInventoryOrders()
    {
        foreach (var inventory in BothOrders(BgeM3, Bf16, Nomic))
        {
            AssertEx.Equal(Bf16, RecommendedEmbeddingModel.PickInstalled(inventory)?.ModelName);
        }
    }

    [Test]
    public void PickInstalled_WithoutTheRecommendedRepo_PicksTheFirstEmbedderByName_InBothInventoryOrders()
    {
        // bge-m3 reads as an embedder by its model segment and its owner sorts before nomic-ai.
        foreach (var inventory in BothOrders(Nomic, BgeM3, "unsloth/gpt-oss-20b-GGUF:Q5_K_M"))
        {
            AssertEx.Equal(BgeM3, RecommendedEmbeddingModel.PickInstalled(inventory)?.ModelName);
        }
    }

    [Test]
    public void PickInstalled_CaseOnlyNameVariants_ResolveToTheSameModel_InBothInventoryOrders()
    {
        // Ordinal-ignore-case ties are broken ordinally, so upper case wins in either order, for every step.
        foreach (var inventory in BothOrders("ggml-org/embeddinggemma-2-GGUF:Q8_0", "ggml-org/EmbeddingGemma-2-GGUF:Q8_0"))
        {
            AssertEx.Equal("ggml-org/EmbeddingGemma-2-GGUF:Q8_0", RecommendedEmbeddingModel.PickInstalled(inventory)?.ModelName);
        }

        foreach (var inventory in BothOrders("mixedbread-ai/mxbai-embed-large-v1-GGUF:Q4_K_M", "Mixedbread-ai/mxbai-embed-large-v1-GGUF:Q4_K_M"))
        {
            AssertEx.Equal("Mixedbread-ai/mxbai-embed-large-v1-GGUF:Q4_K_M", RecommendedEmbeddingModel.PickInstalled(inventory)?.ModelName);
        }
    }

    [Test]
    public void PickInstalled_WithNoEmbedderInstalled_ReturnsNull()
    {
        var inventory = Descriptors("unsloth/gpt-oss-20b-GGUF:Q5_K_M", RecommendedRerankerModel.CanonicalModelName);

        AssertEx.Null(RecommendedEmbeddingModel.PickInstalled(inventory));
    }

    private static IEnumerable<LocalModelDescriptor[]> BothOrders(params string[] modelNames)
    {
        yield return Descriptors(modelNames);
        yield return Descriptors([.. Enumerable.Reverse(modelNames)]);
    }

    private static LocalModelDescriptor[] Descriptors(params string[] modelNames)
    {
        return
        [
            .. modelNames.Select(modelName => new LocalModelDescriptor
            {
                ModelName = modelName,
                ProviderName = "llamacpp",
                IsAvailable = true,
                SizeBytes = 1024,
                ModifiedAt = DateTimeOffset.UnixEpoch,
                MaxContextTokens = null,
                Capabilities = []
            })
        ];
    }
}
