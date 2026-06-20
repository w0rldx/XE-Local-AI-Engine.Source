namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The chat model list aggregates installed GGUF models (served by the bundled llama.cpp runtime, tagged
///     <see cref="LocalModelProviders.LlamaCpp" />) alongside the node-local Ollama models, classifies them
///     <c>Chat</c> WITHOUT an <c>/api/show</c> probe (so they satisfy the React <c>kind === "Chat"</c> picker filter),
///     and surfaces them even when Ollama is unavailable.
/// </summary>
public sealed class LocalModelsGgufMappingTests
{
    private static LocalModelDescriptor Gguf(string modelName, long? sizeBytes = 1024) => new()
    {
        ModelName = modelName,
        ProviderName = LocalModelProviders.LlamaCpp,
        IsAvailable = true,
        SizeBytes = sizeBytes,
        ModifiedAt = DateTimeOffset.UnixEpoch,
        MaxContextTokens = null
    };

    [Test]
    public void ToLlamaCppModelResponses_TagsLlamaCpp_AndClassifiesChat()
    {
        var gguf = LocalModelsMapper.ToLlamaCppModelResponses(
            [Gguf("bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M")],
            null);

        AssertEx.Equal(1, gguf.Count);
        AssertEx.Equal(LocalModelProviders.LlamaCpp, gguf[0].Provider);
        // Chat WITHOUT a capability probe — this is the crux for the React `kind === "Chat"` picker filter.
        AssertEx.Equal(ModelKind.Chat.ToString(), gguf[0].Kind);
        AssertEx.Equal(ModelKind.Chat.ToString(), gguf[0].DetectedKind);
        AssertEx.True(gguf[0].Capabilities.Count == 0, "GGUF entries carry no Ollama capabilities");
        AssertEx.False(gguf[0].IsReasoningCapable, "caps are not probed at list time → safe default false");
        AssertEx.False(gguf[0].IsToolCapable, "caps are not probed at list time → safe default false");
        AssertEx.Equal(1024, gguf[0].SizeBytes);
    }

    [Test]
    public void ToLlamaCppModelResponses_MarksTheSelectedModel()
    {
        var gguf = LocalModelsMapper.ToLlamaCppModelResponses(
            [Gguf("repo-a:Q4_K_M"), Gguf("repo-b:Q4_K_M")],
            "repo-b:Q4_K_M");

        AssertEx.ContainsSingle(gguf, static m => m.IsSelected);
        AssertEx.Contains(gguf, static m => m.ModelName == "repo-b:Q4_K_M" && m.IsSelected);
    }

    [Test]
    public void ToListResponse_ConcatsGgufAfterOllama_BeforeCloud()
    {
        var ollama = new[]
        {
            new Model
            {
                Name = "qwen3:8b",
                ModifiedAt = DateTime.UtcNow
            }
        };
        var classifications = new Dictionary<string, ModelClassificationResult>
        {
            ["qwen3:8b"] = new("qwen3:8b", ModelKind.Chat, ModelKind.Chat, ["tools"], false)
        };
        var cloud = LocalModelsMapper.ToCodexCloudModelResponses(null);

        var response = LocalModelsMapper.ToListResponse(ollama,
            "qwen3:8b",
            "qwen3:8b",
            classifications,
            cloud,
            [Gguf("repo-a:Q4_K_M")]);

        // Order: Ollama → GGUF (llamacpp) → cloud (Codex).
        AssertEx.Equal(LocalModelProviders.Ollama, response.Items[0].Provider);
        AssertEx.Equal(LocalModelProviders.LlamaCpp, response.Items[1].Provider);
        AssertEx.Equal("repo-a:Q4_K_M", response.Items[1].ModelName);
        AssertEx.True(response.Items.Any(static m => m.Provider == LocalModelProviders.CodexOAuth),
            "the cloud group must remain appended last");
        AssertEx.Equal(ollama.Length + 1 + cloud.Count, response.Items.Count);
    }

    [Test]
    public void ToListResponse_DedupesGgufNameAlreadyListedByOllama()
    {
        var ollama = new[]
        {
            new Model
            {
                Name = "shared-model",
                ModifiedAt = DateTime.UtcNow
            }
        };
        var classifications = new Dictionary<string, ModelClassificationResult>
        {
            ["shared-model"] = new("shared-model", ModelKind.Chat, ModelKind.Chat, [], false)
        };

        // A name present under BOTH runtimes (case-insensitively) is listed once — the Ollama entry wins.
        var response = LocalModelsMapper.ToListResponse(ollama,
            null,
            null,
            classifications,
            null,
            [Gguf("SHARED-MODEL"), Gguf("unique-gguf:Q4_K_M")]);

        AssertEx.Equal(2, response.Items.Count);
        AssertEx.ContainsSingle(response.Items, static m => m.ModelName == "shared-model" && m.Provider == LocalModelProviders.Ollama);
        AssertEx.Contains(response.Items, static m => m.ModelName == "unique-gguf:Q4_K_M" && m.Provider == LocalModelProviders.LlamaCpp);
    }

    [Test]
    public void ToUnavailableListResponse_IncludesGguf_AndReportsAvailableWhenGgufPresent()
    {
        // Ollama unavailable, but an installed GGUF means a node-local runtime CAN serve a chat → IsAvailable true.
        var response = LocalModelsMapper.ToUnavailableListResponse(null,
            null,
            "Local model provider is unavailable.",
            null,
            [Gguf("repo-a:Q4_K_M")]);

        AssertEx.True(response.IsAvailable, "an installed GGUF makes the node-local runtime available even without Ollama");
        AssertEx.Equal(1, response.Items.Count);
        AssertEx.Equal(LocalModelProviders.LlamaCpp, response.Items[0].Provider);
    }

    [Test]
    public void ToUnavailableListResponse_WithNoGguf_StaysUnavailable()
    {
        var response = LocalModelsMapper.ToUnavailableListResponse(null,
            null,
            "Local model provider is unavailable.");

        AssertEx.False(response.IsAvailable, "no GGUF and no Ollama → the local runtime is unavailable");
        AssertEx.Equal(0, response.Items.Count);
    }
}
