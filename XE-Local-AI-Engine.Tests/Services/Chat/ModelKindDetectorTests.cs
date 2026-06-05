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
        var result = ModelKindDetector.FromCapabilities(null, "nomic-embed-text");

        AssertEx.Equal(ModelKind.Embedding, result);
    }

    [Test]
    public void FromCapabilities_WhenNullCapabilitiesAndNonEmbeddingName_ReturnsUnknown()
    {
        var result = ModelKindDetector.FromCapabilities(null, "llama3");

        AssertEx.Equal(ModelKind.Unknown, result);
    }

    [Test]
    [Arguments("thinking")]
    [Arguments("THINKING")]
    [Arguments("Thinking")]
    public void SupportsThinking_WhenThinkingPresent_ReturnsTrue(string capability)
    {
        var result = ModelKindDetector.SupportsThinking(["completion", capability]);

        AssertEx.Equal(true, result);
    }

    [Test]
    public void SupportsThinking_WhenThinkingAbsent_ReturnsFalse()
    {
        var result = ModelKindDetector.SupportsThinking(["completion", "vision"]);

        AssertEx.Equal(false, result);
    }

    [Test]
    public void SupportsThinking_WhenCapabilitiesEmpty_ReturnsFalse()
    {
        var result = ModelKindDetector.SupportsThinking([]);

        AssertEx.Equal(false, result);
    }

    [Test]
    public void SupportsThinking_WhenCapabilitiesNull_ReturnsFalse()
    {
        var result = ModelKindDetector.SupportsThinking(null);

        AssertEx.Equal(false, result);
    }

    [Test]
    [Arguments("tools")]
    [Arguments("TOOLS")]
    [Arguments("Tools")]
    public void SupportsTools_WhenToolsPresent_ReturnsTrue(string capability)
    {
        var result = ModelKindDetector.SupportsTools(["completion", capability]);

        AssertEx.Equal(true, result);
    }

    [Test]
    public void SupportsTools_WhenToolsAbsent_ReturnsFalse()
    {
        var result = ModelKindDetector.SupportsTools(["completion", "vision"]);

        AssertEx.Equal(false, result);
    }

    [Test]
    public void SupportsTools_WhenCapabilitiesEmpty_ReturnsFalse()
    {
        var result = ModelKindDetector.SupportsTools([]);

        AssertEx.Equal(false, result);
    }

    [Test]
    public void SupportsTools_WhenCapabilitiesNull_ReturnsFalse()
    {
        var result = ModelKindDetector.SupportsTools(null);

        AssertEx.Equal(false, result);
    }
}
