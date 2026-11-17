namespace XE_Local_AI_Engine.Tests.Knowledge;

using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The prefixer formats text the way the resolved model was trained (nomic, EmbeddingGemma and Qwen3-Embedding
///     prompts, raw text for bge-m3 and unprofiled models), so no model receives another model's instruction.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class KnowledgeEmbeddingPrefixerTests
{
    private readonly KnowledgeEmbeddingPrefixer _prefixer = new();

    [Test]
    [Arguments("nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M", "search_document: hello world", "search_query: hello world")]
    [Arguments("ggml-org/embeddinggemma-2-GGUF:Q8_0", "title: none | text: hello world", "task: search result | query: hello world")]
    [Arguments("CompendiumLabs/bge-m3-gguf:F16", "hello world", "hello world")]
    [Arguments("Qwen/Qwen3-Embedding-0.6B-GGUF:Q8_0", "hello world", "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:hello world")]
    [Arguments("my-embed:Q8_0", "hello world", "hello world")]
    public void ForDocumentAndForQuery_FormatThroughTheResolvedModelsProfile(string modelName, string expectedDocument, string expectedQuery)
    {
        var resolution = new EmbeddingModelResolution
        {
            Name = modelName,
            IsConfident = true
        };

        AssertEx.Equal(expectedDocument, _prefixer.ForDocument(resolution, "hello world"));
        AssertEx.Equal(expectedQuery, _prefixer.ForQuery(resolution, "hello world"));
    }
}
