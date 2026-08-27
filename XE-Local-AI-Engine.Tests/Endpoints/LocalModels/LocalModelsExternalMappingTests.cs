namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The chat model list carries external OpenAI-compatible models as their own family, and carries the four
///     connection facts (<c>displayLabel</c>, <c>externalConnectionId</c>, <c>externalConnectionName</c>,
///     <c>declaredLocality</c>) that a bare <c>provider: "external"</c> tag cannot express.
/// </summary>
public sealed class LocalModelsExternalMappingTests
{
    [Test]
    public void ToExternalProviderModelResponses_CarriesTheDeclaredCapabilitiesAndTheConnectionIdentity()
    {
        var responses = LocalModelsMapper.ToExternalProviderModelResponses(
            [
                Registration(ExternalProviderLocality.Local,
                    supportsTools: true,
                    supportsVision: true,
                    supportsReasoning: true)
            ],
            selectedModelName: null);

        var model = responses.Single();
        AssertEx.Equal("ext:unsloth-box/qwen3-27b", model.ModelName);
        AssertEx.Equal(LocalModelProviders.External, model.Provider);
        AssertEx.Equal("Qwen3 27B", model.DisplayLabel);
        AssertEx.Equal("unsloth-box", model.ExternalConnectionId);
        AssertEx.Equal("Unsloth box", model.ExternalConnectionName);
        AssertEx.Equal(LocalModelDeclaredLocalities.Local, model.DeclaredLocality);
        AssertEx.Equal(ModelKind.Chat.ToString(), model.Kind);
        AssertEx.True(model.IsToolCapable);
        AssertEx.True(model.IsMultimodalCapable);
        AssertEx.True(model.IsReasoningCapable);

        // Native reasoning is a llama.cpp chat-template concept: an external model must never be diverted out of the
        // graded reasoning_effort path by claiming it.
        AssertEx.False(model.IsNativeReasoningCapable);

        // Vacuously enforceable: this provider emits no budget marker, so there is no cap for a server to ignore.
        AssertEx.True(model.ReasoningBudgetEnforceable);

        // Size and install time describe node-local weights; the node holds none for these.
        AssertEx.Null(model.SizeBytes);
        AssertEx.Null(model.ModifiedAtUtc);
    }

    [Test]
    public void ToExternalProviderModelResponses_ReportsADeclaredCloudConnectionAsCloud()
    {
        var responses = LocalModelsMapper.ToExternalProviderModelResponses([Registration(ExternalProviderLocality.Cloud)], selectedModelName: null);

        AssertEx.Equal(LocalModelDeclaredLocalities.Cloud, responses.Single().DeclaredLocality);
    }

    [Test]
    public void ToExternalProviderModelResponses_MarksTheSelectedExternalModel()
    {
        var responses = LocalModelsMapper.ToExternalProviderModelResponses([Registration(ExternalProviderLocality.Local)],
            "ext:unsloth-box/qwen3-27b");

        AssertEx.True(responses.Single().IsSelected);
    }

    [Test]
    public void ToListResponse_AppendsExternalModelsAfterTheLocalAndCloudFamilies()
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
        var external = LocalModelsMapper.ToExternalProviderModelResponses([Registration(ExternalProviderLocality.Local)], selectedModelName: null);

        var response = LocalModelsMapper.ToListResponse(localModels,
            selectedModelName: null,
            configuredDefaultModelName: null,
            classifications,
            cloud,
            ggufModels: null,
            external);

        AssertEx.Equal("qwen3:8b", response.Items[0].ModelName);
        AssertEx.Equal(LocalModelProviders.External, response.Items[^1].Provider);
        AssertEx.Equal(expected: 1, response.Items.Count(static item => item.Provider == LocalModelProviders.External));
    }

    [Test]
    public void ToUnavailableListResponse_StillCarriesExternalModels()
    {
        // An external model is served by someone else's endpoint, so an unreachable Ollama has nothing to do with it.
        var external = LocalModelsMapper.ToExternalProviderModelResponses([Registration(ExternalProviderLocality.Local)], selectedModelName: null);

        var response = LocalModelsMapper.ToUnavailableListResponse(selectedModelName: null,
            configuredDefaultModelName: null,
            "Local model provider is unavailable.",
            cloudModels: null,
            ggufModels: null,
            external);

        AssertEx.Equal(LocalModelProviders.External, response.Items.Single().Provider);
    }

    [Test]
    public void ToAzureFoundryCloudModelResponses_CarriesTheStoredDisplayLabel()
    {
        // The regression this fixes: the operator sets the label in the Azure settings editor, it round-trips through
        // settings, and until the list DTO had a field for it, it was dropped before the picker ever saw it.
        var connection = new StoredAzureFoundryConnection
        {
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-5-deployment",
                    DisplayLabel = "GPT-5 (prod)"
                }
            ]
        };

        var responses = LocalModelsMapper.ToAzureFoundryCloudModelResponses(connection, selectedModelName: null);

        AssertEx.Equal("GPT-5 (prod)", responses.Single().DisplayLabel);
    }

    [Test]
    public void ToRunningResponse_NeverListsAnExternalModel()
    {
        // "Running" means resident in this node's RAM/VRAM. Listing an ext: id would offer an eject/unload action
        // against a process this node does not own.
        var running = new[]
        {
            new RunningModelSnapshot("qwen3:8b", "qwen3:8b", ExpiresAt: null),
            new RunningModelSnapshot("ext:unsloth-box/qwen3-27b", "ext:unsloth-box/qwen3-27b", ExpiresAt: null)
        };

        var response = LocalModelsMapper.ToRunningResponse(running, ollamaConfigured: true);

        AssertEx.Equal("qwen3:8b", response.Items.Single().ModelName);
    }

    private static ExternalProviderModelRegistration Registration(ExternalProviderLocality locality,
        bool supportsTools = false,
        bool supportsVision = false,
        bool supportsReasoning = false)
    {
        return new ExternalProviderModelRegistration(new ExternalProviderConnectionDescriptor
            {
                Id = "unsloth-box",
                DisplayName = "Unsloth box",
                BaseUrl = new Uri("http://127.0.0.1:18099/v1/"),
                Locality = locality
            },
            new ExternalProviderModelDescriptor
            {
                WireId = "qwen3-27b",
                DisplayName = "Qwen3 27B",
                ContextLength = 32768,
                SupportsTools = supportsTools,
                SupportsVision = supportsVision,
                SupportsReasoning = supportsReasoning
            });
    }
}
