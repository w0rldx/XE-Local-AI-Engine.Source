namespace XE_Local_AI_Engine.Tests.Services.Chat;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class OllamaModelInfoParserTests
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

        var result = OllamaModelInfoParser.TryGetContextLength(ReadModelInfo(document), out var contextLength);

        AssertEx.True(result);
        AssertEx.Equal(expected, contextLength);
    }

    [Test]
    public void TryGetContextLength_WhenFixtureContainsGemmaContextLength_ReturnsValue()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures",
            "ollama-show-gemma3.json")));

        var result = OllamaModelInfoParser.TryGetContextLength(ReadModelInfo(document), out var contextLength);

        AssertEx.True(result);
        AssertEx.Equal(131072, contextLength);
    }

    [Test]
    public void TryGetContextLength_WhenFixtureHasNoContextLength_ReturnsFalse()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures",
            "ollama-show-missing-context-length.json")));

        var result = OllamaModelInfoParser.TryGetContextLength(ReadModelInfo(document), out _);

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

        var result = OllamaModelInfoParser.TryGetContextLength(ReadModelInfo(document), out _);

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

        var result = OllamaModelInfoParser.TryGetContextLength(ReadModelInfo(document), out _);

        AssertEx.False(result);
    }

    private static Dictionary<string, JsonElement> ReadModelInfo(JsonDocument document)
    {
        return document.RootElement
                       .GetProperty("model_info")
                       .EnumerateObject()
                       .ToDictionary(property => property.Name, property => property.Value);
    }
}
