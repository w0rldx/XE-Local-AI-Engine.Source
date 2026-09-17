namespace XE_Local_AI_Engine.Tests.Providers.Ollama;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Context-length extraction from an Ollama <c>/api/show</c> model-info block. Ported from the deleted
///     <c>OllamaModelInfoParser</c> tests when S5 collapsed the duplicate parser onto this reader, plus the null case
///     the reader answers differently: it returns <see langword="false" /> where the parser threw.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class OllamaModelInfoReaderTests
{
    [Test]
    [Arguments("llama.context_length", 8192)]
    [Arguments("gemma3.context_length", 131072)]
    [Arguments("qwen2.context_length", 32768)]
    public void TryGetContextLength_WhenArchitectureContextLengthExists_ReturnsValue(string key, int expected)
    {
        using var document = JsonDocument.Parse($$"""
                                                  {
                                                    "model_info": {
                                                      "{{key}}": {{expected}}
                                                    }
                                                  }
                                                  """);

        var result = OllamaModelInfoReader.TryGetContextLength(ReadModelInfo(document), out var contextLength);

        AssertEx.True(result);
        AssertEx.Equal(expected, contextLength);
    }

    [Test]
    public void TryGetContextLength_WhenFixtureContainsGemmaContextLength_ReturnsValue()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures",
            "ollama-show-gemma3.json")));

        var result = OllamaModelInfoReader.TryGetContextLength(ReadModelInfo(document), out var contextLength);

        AssertEx.True(result);
        AssertEx.Equal(expected: 131072, contextLength);
    }

    [Test]
    public void TryGetContextLength_WhenFixtureHasNoContextLength_ReturnsFalse()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures",
            "ollama-show-missing-context-length.json")));

        var result = OllamaModelInfoReader.TryGetContextLength(ReadModelInfo(document), out _);

        AssertEx.False(result);
    }

    [Test]
    [Arguments("context_length", 8192)]
    [Arguments("llama.embedding_length", 4096)]
    [Arguments("llama.context_length", 0)]
    [Arguments("llama.context_length", -1)]
    public void TryGetContextLength_WhenKeyOrValueIsUnsupported_ReturnsFalse(string key, int value)
    {
        using var document = JsonDocument.Parse($$"""
                                                  {
                                                    "model_info": {
                                                      "{{key}}": {{value}}
                                                    }
                                                  }
                                                  """);

        var result = OllamaModelInfoReader.TryGetContextLength(ReadModelInfo(document), out _);

        AssertEx.False(result);
    }

    [Test]
    public void TryGetContextLength_WhenValueIsNonInteger_ReturnsFalse()
    {
        using var document = JsonDocument.Parse("""
                                                {
                                                  "model_info": {
                                                    "llama.context_length": "8192"
                                                  }
                                                }
                                                """);

        var result = OllamaModelInfoReader.TryGetContextLength(ReadModelInfo(document), out _);

        AssertEx.False(result);
    }

    [Test]
    public void TryGetContextLength_WhenModelInfoIsNull_ReturnsFalseInsteadOfThrowing()
    {
        // The deleted OllamaModelInfoParser threw ArgumentNullException here. ShowModelDetailsAsync passes
        // response.Info?.ExtraInfo straight through, so a daemon that reports no model-info block must answer
        // "no context length" rather than fault the details call.
        var result = OllamaModelInfoReader.TryGetContextLength((IDictionary<string, JsonElement>?)null, out var contextLength);

        AssertEx.False(result);
        AssertEx.Equal(expected: 0, contextLength);
    }

    [Test]
    public void TryGetContextLength_WhenBoxedModelInfoIsNull_ReturnsFalseInsteadOfThrowing()
    {
        // The object-valued overload (the provider's ListModelsAsync path) shares the same null policy.
        var result = OllamaModelInfoReader.TryGetContextLength((IDictionary<string, object>?)null, out var contextLength);

        AssertEx.False(result);
        AssertEx.Equal(expected: 0, contextLength);
    }

    private static Dictionary<string, JsonElement> ReadModelInfo(JsonDocument document)
    {
        return document.RootElement
                       .GetProperty("model_info")
                       .EnumerateObject()
                       .ToDictionary(property => property.Name, property => property.Value);
    }
}
