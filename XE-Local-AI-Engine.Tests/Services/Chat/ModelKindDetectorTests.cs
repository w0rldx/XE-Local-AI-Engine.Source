namespace XE_Local_AI_Engine.Tests.Services.Chat;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ModelKindDetectorTests
{
    [Test]
    public void FromCapabilities_WhenEmbeddingOnly_ReturnsEmbedding()
    {
        var result = ModelKindDetector.FromCapabilities(["embedding"], "nomic-embed-text");

        AssertEx.Equal(ModelKind.Embedding, result);
    }

    [Test]
    public void FromCapabilities_WhenCompletionAndTools_ReturnsChat()
    {
        var result = ModelKindDetector.FromCapabilities(["completion", "tools"], "llama3");

        AssertEx.Equal(ModelKind.Chat, result);
    }

    [Test]
    public void FromCapabilities_WhenCompletionAndVision_ReturnsChat()
    {
        var result = ModelKindDetector.FromCapabilities(["completion", "vision"], "llava");

        AssertEx.Equal(ModelKind.Chat, result);
    }

    [Test]
    public void FromCapabilities_WhenCompletionAndEmbedding_ReturnsChat()
    {
        var result = ModelKindDetector.FromCapabilities(["completion", "embedding"], "some-hybrid");

        AssertEx.Equal(ModelKind.Chat, result);
    }

    [Test]
    public void FromCapabilities_WhenCapabilitiesUnrecognized_ReturnsUnknown()
    {
        var result = ModelKindDetector.FromCapabilities(["insert"], "weird-model");

        AssertEx.Equal(ModelKind.Unknown, result);
    }

    [Test]
    [Arguments("EMBEDDING")]
    [Arguments("Embedding")]
    public void FromCapabilities_WhenEmbeddingCaseInsensitive_ReturnsEmbedding(string capability)
    {
        var result = ModelKindDetector.FromCapabilities([capability], "some-model");

        AssertEx.Equal(ModelKind.Embedding, result);
    }

    [Test]
    [Arguments("COMPLETION")]
    [Arguments("Completion")]
    public void FromCapabilities_WhenCompletionCaseInsensitive_ReturnsChat(string capability)
    {
        var result = ModelKindDetector.FromCapabilities([capability], "some-model");

        AssertEx.Equal(ModelKind.Chat, result);
    }

    [Test]
    [Arguments("nomic-embed-text")]
    [Arguments("mxbai-embed-large")]
    [Arguments("all-minilm")]
    [Arguments("bge-large")]
    [Arguments("bge:latest")]
    [Arguments("my-EMBEDDING-model")]
    public void FromCapabilities_WhenNoCapabilitiesAndEmbeddingName_ReturnsEmbedding(string modelName)
    {
        var result = ModelKindDetector.FromCapabilities([], modelName);

        AssertEx.Equal(ModelKind.Embedding, result);
    }

    [Test]
    [Arguments("llama3")]
    [Arguments("qwen2.5")]
    [Arguments("mistral")]
    [Arguments("")]
    [Arguments("   ")]
    public void FromCapabilities_WhenNoCapabilitiesAndNonEmbeddingName_ReturnsUnknown(string modelName)
    {
        var result = ModelKindDetector.FromCapabilities([], modelName);

        AssertEx.Equal(ModelKind.Unknown, result);
    }

    [Test]
    public void FromCapabilities_WhenNullCapabilitiesAndEmbeddingName_ReturnsEmbedding()
    {
        var result = ModelKindDetector.FromCapabilities(capabilities: null, "nomic-embed-text");

        AssertEx.Equal(ModelKind.Embedding, result);
    }

    [Test]
    public void FromCapabilities_WhenNullCapabilitiesAndNonEmbeddingName_ReturnsUnknown()
    {
        var result = ModelKindDetector.FromCapabilities(capabilities: null, "llama3");

        AssertEx.Equal(ModelKind.Unknown, result);
    }

    [Test]
    [Arguments("nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M")]
    [Arguments("mxbai-embed-large:Q8_0")]
    [Arguments("bge-small:Q4")]
    [Arguments("bge:latest")]
    [Arguments("all-minilm:Q4_K_M")]
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
    [Arguments("bge-reranker-v2-m3")]
    [Arguments("BAAI/bge-reranker-large:Q8_0")]
    [Arguments("jina-reranker-v2")]
    [Arguments("mxbai-rerank-large-v1:Q4_K_M")]
    public void FromCapabilities_WhenNoCapabilitiesAndRerankerName_ReturnsReranker(string modelName)
    {
        var result = ModelKindDetector.FromCapabilities([], modelName);

        AssertEx.Equal(ModelKind.Reranker, result);
    }

    [Test]
    public void FromCapabilities_WhenRerankerNameAndEmbeddingCapability_ReturnsReranker()
    {
        // A cross-encoder can advertise an embedding capability; the reranker name must still win so it is never
        // classified as Embedding (which would auto-resolve it as the knowledge-base embedding model).
        var result = ModelKindDetector.FromCapabilities(["embedding"], "bge-reranker-v2-m3");

        AssertEx.Equal(ModelKind.Reranker, result);
    }

    [Test]
    [Arguments("bge-reranker-v2-m3:Q4_K_M")]
    [Arguments("BAAI/bge-reranker-base")]
    [Arguments("jina-reranker-v2-base-multilingual")]
    [Arguments("mxbai-rerank-xsmall-v1:Q8_0")]
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
    [Arguments("")]
    [Arguments("   ")]
    public void IsRerankerName_WhenNotRerankerName_ReturnsFalse(string modelName)
    {
        var result = ModelKindDetector.IsRerankerName(modelName);

        AssertEx.False(result, "a chat / embedding / unknown model name must never be guessed as reranker");
    }

    [Test]
    [Arguments("thinking")]
    [Arguments("THINKING")]
    [Arguments("Thinking")]
    public void SupportsThinking_WhenThinkingPresent_ReturnsTrue(string capability)
    {
        var result = ModelKindDetector.SupportsThinking(["completion", capability]);

        AssertEx.Equal(expected: true, result);
    }

    [Test]
    public void SupportsThinking_WhenThinkingAbsent_ReturnsFalse()
    {
        var result = ModelKindDetector.SupportsThinking(["completion", "vision"]);

        AssertEx.Equal(expected: false, result);
    }

    [Test]
    public void SupportsThinking_WhenCapabilitiesEmpty_ReturnsFalse()
    {
        var result = ModelKindDetector.SupportsThinking([]);

        AssertEx.Equal(expected: false, result);
    }

    [Test]
    public void SupportsThinking_WhenCapabilitiesNull_ReturnsFalse()
    {
        var result = ModelKindDetector.SupportsThinking(null);

        AssertEx.Equal(expected: false, result);
    }

    [Test]
    [Arguments("tools")]
    [Arguments("TOOLS")]
    [Arguments("Tools")]
    public void SupportsTools_WhenToolsPresent_ReturnsTrue(string capability)
    {
        var result = ModelKindDetector.SupportsTools(["completion", capability]);

        AssertEx.Equal(expected: true, result);
    }

    [Test]
    public void SupportsTools_WhenToolsAbsent_ReturnsFalse()
    {
        var result = ModelKindDetector.SupportsTools(["completion", "vision"]);

        AssertEx.Equal(expected: false, result);
    }

    [Test]
    public void SupportsTools_WhenCapabilitiesEmpty_ReturnsFalse()
    {
        var result = ModelKindDetector.SupportsTools([]);

        AssertEx.Equal(expected: false, result);
    }

    [Test]
    public void SupportsTools_WhenCapabilitiesNull_ReturnsFalse()
    {
        var result = ModelKindDetector.SupportsTools(null);

        AssertEx.Equal(expected: false, result);
    }
}
