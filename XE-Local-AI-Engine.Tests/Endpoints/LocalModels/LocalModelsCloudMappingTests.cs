namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The chat model list exposes Codex cloud models (tagged <see cref="LocalModelProviders.CodexOAuth" />) as a
///     distinct group alongside the node-local Ollama models, and the cloud entries advertise the Codex provider's
///     declared capability matrix rather than an Ollama classification.
/// </summary>
public sealed class LocalModelsCloudMappingTests
{
    [Test]
    public void ToCodexCloudModelResponses_EmitsCatalogTaggedCodexOAuth()
    {
        var cloud = LocalModelsMapper.ToCodexCloudModelResponses(null);

        AssertEx.Equal(CodexModelCatalog.ModelIds.Count, cloud.Count);
        AssertEx.True(cloud.All(static m => m.Provider == LocalModelProviders.CodexOAuth),
            "every cloud entry must be tagged provider=CodexOAuth");
        AssertEx.True(cloud.All(static m => m.IsReasoningCapable), "Codex models reason by default");
        // Tool calling is now enabled for ALL Codex ids; the mapped tag tracks the V0 flag, which is now true.
        AssertEx.True(CodexProviderCapabilities.V0.SupportsToolCalling, "the Codex matrix must advertise tool calling");
        AssertEx.True(cloud.All(static m => m.IsToolCapable == CodexProviderCapabilities.V0.SupportsToolCalling));
        AssertEx.True(cloud.Any(static m => m.ModelName == "gpt-5.5"), "the catalog must include gpt-5.5");
    }

    [Test]
    public void ToCodexCloudModelResponses_MarksTheSelectedCloudModel()
    {
        var cloud = LocalModelsMapper.ToCodexCloudModelResponses("gpt-5.4");

        var selected = cloud.Where(static m => m.IsSelected).ToList();
        AssertEx.Equal(expected: 1, selected.Count);
        AssertEx.Equal("gpt-5.4", selected[0].ModelName);
    }

    [Test]
    public void ToListResponse_AppendsCloudModelsAfterLocalModels()
    {
        var localModels = new[]
        {
            new Model
            {
                Name = "qwen3:8b",
                ModifiedAt = DateTime.UtcNow
            }
        };
        var classifications = new Dictionary<string, ModelClassificationResult>
        {
            ["qwen3:8b"] = new("qwen3:8b", ModelKind.Chat, ModelKind.Chat, ["tools"], IsOverridden: false)
        };
        var cloud = LocalModelsMapper.ToCodexCloudModelResponses(null);

        var response = LocalModelsMapper.ToListResponse(localModels, "qwen3:8b", "qwen3:8b", classifications, cloud);

        // Local models first (provider=Ollama), cloud models appended after (provider=CodexOAuth).
        AssertEx.Equal("qwen3:8b", response.Items[0].ModelName);
        AssertEx.Equal(LocalModelProviders.Ollama, response.Items[0].Provider);
        AssertEx.True(response.Items.Any(static m => m.Provider == LocalModelProviders.CodexOAuth),
            "the list must include the appended cloud models");
        AssertEx.Equal(localModels.Length + cloud.Count, response.Items.Count);
    }

    [Test]
    public void ToListResponse_WhenNoCloudModels_OmitsCloudGroup()
    {
        var localModels = new[]
        {
            new Model
            {
                Name = "qwen3:8b",
                ModifiedAt = DateTime.UtcNow
            }
        };
        var classifications = new Dictionary<string, ModelClassificationResult>
        {
            ["qwen3:8b"] = new("qwen3:8b", ModelKind.Chat, ModelKind.Chat, [], IsOverridden: false)
        };

        var response = LocalModelsMapper.ToListResponse(localModels, "qwen3:8b", "qwen3:8b", classifications);

        AssertEx.Equal(expected: 1, response.Items.Count);
        AssertEx.True(response.Items.All(static m => m.Provider == LocalModelProviders.Ollama));
    }
}
