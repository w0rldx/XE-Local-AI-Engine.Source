namespace XE_Local_AI_Engine.Tests.Services.Chat;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ModelKindDetectorTests
{
    [Test]
    [Arguments("nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M")]
    [Arguments("nomic-embed-text")]
    [Arguments("mxbai-embed-large:Q8_0")]
    [Arguments("bge-small:Q4")]
    [Arguments("bge-large")]
    [Arguments("bge:latest")]
    [Arguments("all-minilm:Q4_K_M")]
    [Arguments("all-minilm")]
    [Arguments("my-EMBEDDING-model")]
    [Arguments("CompendiumLabs/bge-m3-gguf:Q8_0")]
    [Arguments("compendiumlabs/BGE-M3-GGUF:F16")]
    [Arguments("rerank-org/bge-m3")]
    public void IsEmbeddingName_WhenEmbeddingName_ReturnsTrue(string modelName)
    {
        var result = ModelKindDetector.IsEmbeddingName(modelName);

        AssertEx.True(result, "an embedding-named model must be recognized by name alone");
    }

    [Test]
    [Arguments("qwen2.5:Q4_K_M")]
    [Arguments("llama-3.1-8b-instruct:Q6_K")]
    [Arguments("mistral")]
    [Arguments("")]
    [Arguments("   ")]
    public void IsEmbeddingName_WhenNotEmbeddingName_ReturnsFalse(string modelName)
    {
        var result = ModelKindDetector.IsEmbeddingName(modelName);

        AssertEx.False(result, "a chat / unknown model name must never be guessed as embedding");
    }

    [Test]
    [Arguments("bge-reranker-v2-m3:Q4_K_M")]
    [Arguments("BAAI/bge-reranker-base")]
    [Arguments("jina-reranker-v2-base-multilingual")]
    [Arguments("mxbai-rerank-xsmall-v1:Q8_0")]
    [Arguments("gpustack/bge-reranker-v2-m3-GGUF:Q4_K_M")]
    [Arguments("bge-reranker-v2-m3")]
    public void IsRerankerName_WhenRerankerName_ReturnsTrue(string modelName)
    {
        var result = ModelKindDetector.IsRerankerName(modelName);

        AssertEx.True(result, "a reranker-named model must be recognized by name alone");
    }

    [Test]
    [Arguments("bge-reranker-v2-m3")]
    [Arguments("bge-reranker-large:Q8_0")]
    public void IsEmbeddingName_WhenRerankerName_ReturnsFalse(string modelName)
    {
        // A reranker name also matches the BGE- embedding prefix, but reranker takes precedence — it must NOT be
        // treated as an embedding model (else it would be auto-picked for knowledge-base embedding).
        var result = ModelKindDetector.IsEmbeddingName(modelName);

        AssertEx.False(result, "a reranker model name must never be guessed as embedding");
    }

    [Test]
    [Arguments("qwen2.5:Q4_K_M")]
    [Arguments("llama-3.1-8b-instruct:Q6_K")]
    [Arguments("nomic-embed-text")]
    [Arguments("bge-large")]
    [Arguments("rerank-org/bge-m3")]
    [Arguments("")]
    [Arguments("   ")]
    public void IsRerankerName_WhenNotRerankerName_ReturnsFalse(string modelName)
    {
        var result = ModelKindDetector.IsRerankerName(modelName);

        AssertEx.False(result, "a chat / embedding / unknown model name must never be guessed as reranker");
    }

    [Test]
    [Arguments("bge-org/some-chat-model:Q4_K_M")]
    [Arguments("embedding-org/chat-model")]
    public void Classify_WhenOnlyTheOwnerLooksLikeAnEmbedder_IsChat(string modelName)
    {
        // The owner names a publisher, not the model kind: only the segment after the last '/' is matched.
        var kind = LocalGgufModelKindClassifier.Classify(modelName);

        AssertEx.Equal(ModelKind.Chat, kind, "an embedding-looking owner must not make a chat model an embedder");
    }
}
