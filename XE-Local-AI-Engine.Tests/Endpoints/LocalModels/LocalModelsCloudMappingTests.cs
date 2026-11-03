namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The chat model list exposes Codex cloud models (tagged <see cref="LocalModelProviders.CodexOAuth" />) as a
///     distinct group alongside the node-local GGUF models, and the cloud entries advertise the Codex provider's
///     declared capability matrix rather than a local classification.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LocalModelsCloudMappingTests
{
    private static readonly Dictionary<string, ModelClassificationRecord> NoClassifications = [];

    [Test]
    public void ToCodexCloudModelResponses_EmitsCatalogTaggedCodexOAuth()
    {
        var cloud = LocalModelsMapper.ToCodexCloudModelResponses(null);

        AssertEx.Equal(CodexModelCatalog.ModelIds.Count, cloud.Count);
        AssertEx.True(cloud.All(static m => m.Provider == LocalModelProviders.CodexOAuth),
            "every cloud entry must be tagged provider=CodexOAuth");
        AssertEx.True(cloud.All(static m => m.IsReasoningCapable), "Codex models reason by default");
        // Tool calling is enabled for ALL Codex ids; the mapped tag tracks the V0 flag, which is true.
        AssertEx.True(CodexProviderCapabilities.V0.SupportsToolCalling, "the Codex matrix must advertise tool calling");
        AssertEx.True(cloud.All(static m => m.IsToolCapable == CodexProviderCapabilities.V0.SupportsToolCalling));
        AssertEx.True(cloud.Any(static m => m.ModelName == "gpt-5.6-sol"), "the catalog must include gpt-5.6-sol");
    }

    [Test]
    public void ToCodexCloudModelResponses_MarksTheSelectedCloudModel()
    {
        var cloud = LocalModelsMapper.ToCodexCloudModelResponses("gpt-5.6-terra");

        var selected = cloud.Where(static m => m.IsSelected).ToList();
        AssertEx.Equal(expected: 1, selected.Count);
        AssertEx.Equal("gpt-5.6-terra", selected[0].ModelName);
    }

    [Test]
    public void ToListResponse_AppendsCloudModelsAfterLocalModels()
    {
        var cloud = LocalModelsMapper.ToCodexCloudModelResponses(null);

        var response = LocalModelsMapper.ToListResponse("qwen3:8b", "qwen3:8b", NoClassifications, cloud, [Gguf("qwen3:8b")]);

        // Local models first (provider=llamacpp), cloud models appended after (provider=CodexOAuth).
        AssertEx.Equal("qwen3:8b", response.Items[0].ModelName);
        AssertEx.Equal(LocalModelProviders.LlamaCpp, response.Items[0].Provider);
        AssertEx.True(response.Items.Any(static m => m.Provider == LocalModelProviders.CodexOAuth),
            "the list must include the appended cloud models");
        AssertEx.Equal(1 + cloud.Count, response.Items.Count);
    }

    [Test]
    public void ToListResponse_WhenNoCloudModels_OmitsCloudGroup()
    {
        var response = LocalModelsMapper.ToListResponse("qwen3:8b", "qwen3:8b", NoClassifications, ggufModels: [Gguf("qwen3:8b")]);

        AssertEx.Equal(expected: 1, response.Items.Count);
        AssertEx.True(response.Items.All(static m => m.Provider == LocalModelProviders.LlamaCpp));
    }

    private static LocalModelDescriptor Gguf(string modelName)
    {
        return new LocalModelDescriptor
        {
            ModelName = modelName,
            ProviderName = LocalModelProviders.LlamaCpp,
            IsAvailable = true,
            SizeBytes = 1024,
            ModifiedAt = null,
            MaxContextTokens = null
        };
    }
}
