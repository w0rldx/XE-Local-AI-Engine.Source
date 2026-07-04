namespace XE_Local_AI_Engine.Tests.NodeSettings;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeSettingsEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task GetNodeSettings_ReturnsStoredSettings()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(new StoredNodeSettings
                         {
                             MaxMessageRequestTimeoutSeconds = 120
                         });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 120, settings.MaxMessageRequestTimeoutSeconds);
        AssertEx.Equal(StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds, settings.MinMessageRequestTimeoutSeconds);
        AssertEx.Equal(StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds, settings.MaxAllowedMessageRequestTimeoutSeconds);
        await nodeSettingsStore.Received(1).LoadAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenValid_SavesAndReportsCapabilities()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(nodeSettingsStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 600
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 600, settings.MaxMessageRequestTimeoutSeconds);
        await nodeSettingsStore.Received(1).SaveAsync(Arg.Is<StoredNodeSettings>(stored => stored.MaxMessageRequestTimeoutSeconds == 600),
            Arg.Any<CancellationToken>());
        await capabilityReporter.Received(1).ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenOutOfRange_ReturnsValidationProblem()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        await using var factory = CreateFactory(nodeSettingsStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 1
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
        await capabilityReporter.DidNotReceiveWithAnyArgs().ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WithNewMigratedFields_RoundTripsThroughGet()
    {
        StoredNodeSettings? saved = null;
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(_ => saved ?? new StoredNodeSettings());
        await nodeSettingsStore.SaveAsync(Arg.Do<StoredNodeSettings>(settings => saved = settings), Arg.Any<CancellationToken>());

        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var putRequest = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        putRequest.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 300,
            EnableTools = false,
            ToolCapableModels = ["qwen3:8b", "gemma3:12b"],
            OllamaEndpoint = "http://127.0.0.1:11500",
            HuggingFaceDefaultQuant = "Q5_K_M",
            LlamaMaxLoadedProcesses = 5,
            LlamaIdleTimeToLiveSeconds = 1200,
            MaxResponseSizeMb = 25,
            RecommendedLlamaCppTag = "b9700",
            OrchestrationIdleTimeoutSeconds = 240
        });
        using var putResponse = await client.SendAsync(putRequest).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        using var getRequest = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var getResponse = await client.SendAsync(getRequest).ConfigureAwait(false);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(getResponse).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        AssertEx.Equal(expected: false, settings.EnableTools);
        AssertEx.Equal("http://127.0.0.1:11500", settings.OllamaEndpoint);
        AssertEx.Equal("Q5_K_M", settings.HuggingFaceDefaultQuant);
        AssertEx.Equal(expected: 5, settings.LlamaMaxLoadedProcesses);
        AssertEx.Equal(expected: 1200, settings.LlamaIdleTimeToLiveSeconds);
        AssertEx.Equal(expected: 25, settings.MaxResponseSizeMb);
        AssertEx.Equal("b9700", settings.RecommendedLlamaCppTag);
        AssertEx.Equal(expected: 240, settings.OrchestrationIdleTimeoutSeconds);
        AssertEx.NotNull(settings.ToolCapableModels);
        AssertEx.Contains(settings.ToolCapableModels!, "gemma3:12b");
        // Bounds are surfaced for the React form.
        AssertEx.Equal(StoredNodeSettings.MaxLlamaMaxLoadedProcesses, settings.MaxAllowedLlamaMaxLoadedProcesses);
    }

    [Test]
    public async Task SaveNodeSettings_WhenOmittingOptionalFields_KeepsCurrentStoredValues()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(new StoredNodeSettings
                         {
                             MaxMessageRequestTimeoutSeconds = 300,
                             RecommendedLlamaCppTag = "b9692",
                             OllamaEndpoint = "http://127.0.0.1:11434"
                         });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        // Omit EVERY field — including the chat timeout (now optional) — so the merge must keep all current values.
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest());
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await nodeSettingsStore.Received(1).SaveAsync(Arg.Is<StoredNodeSettings>(stored => stored.MaxMessageRequestTimeoutSeconds == 300
                                                                                           && stored.RecommendedLlamaCppTag == "b9692"
                                                                                           && stored.OllamaEndpoint == "http://127.0.0.1:11434"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenRecommendedTagMalformed_ReturnsValidationProblem()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 300,
            RecommendedLlamaCppTag = "not-a-tag"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenOllamaEndpointNotAUrl_ReturnsValidationProblem()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 300,
            OllamaEndpoint = "not a url"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenLlamaMaxLoadedProcessesOutOfRange_ReturnsValidationProblem()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 300,
            LlamaMaxLoadedProcesses = 999
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenDraftModeWithoutDraftModel_ReturnsValidationProblem()
    {
        // A draft-* speculative mode with no draft model must be rejected at the boundary (would fail chat-server start).
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            SpeculativeMode = "draft-simple"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenNgramModeWithoutDraftModel_Saves()
    {
        // ngram-* modes self-speculate; they need no draft model, so an empty draft-model name is valid.
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            SpeculativeMode = "ngram-mod"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await nodeSettingsStore.Received(1).SaveAsync(Arg.Is<StoredNodeSettings>(stored => stored.SpeculativeMode == "ngram-mod"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenDraftModeWithDraftModel_Saves()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            SpeculativeMode = "draft-simple",
            SpeculativeDraftModelName = "my-draft"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await nodeSettingsStore.Received(1).SaveAsync(
            Arg.Is<StoredNodeSettings>(stored => stored.SpeculativeMode == "draft-simple" && stored.SpeculativeDraftModelName == "my-draft"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenClearingDraftModelUnderStoredDraftMode_ReturnsValidationProblem()
    {
        // The partial-update edge the boundary validator can't see: a draft-* mode is already stored, and this request
        // (which omits SpeculativeMode) clears the draft model name. The post-merge guard must still reject it.
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(new StoredNodeSettings
                         {
                             SpeculativeMode = "draft-simple",
                             SpeculativeDraftModelName = "my-draft"
                         });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            SpeculativeDraftModelName = "   "
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void NodeSettings_VoiceFields_RoundTripThroughMapper()
    {
        var request = new SaveNodeSettingsRequest
        {
            VoiceFeatureEnabled = true,
            AllowedVoiceModels = ["onnx-community/Kokoro-82M-v1.0-ONNX"],
            DefaultVoiceProfile = "  am_adam  "
        };

        var stored = request.ToStoredSettings(new StoredNodeSettings());

        AssertEx.Equal(expected: true, stored.VoiceFeatureEnabled);
        AssertEx.NotNull(stored.AllowedVoiceModels);
        AssertEx.Contains(stored.AllowedVoiceModels!, "onnx-community/Kokoro-82M-v1.0-ONNX");
        AssertEx.Equal("am_adam", stored.DefaultVoiceProfile);

        var response = stored.ToResponse();

        AssertEx.Equal(expected: true, response.VoiceFeatureEnabled);
        AssertEx.NotNull(response.AllowedVoiceModels);
        AssertEx.Contains(response.AllowedVoiceModels!, "onnx-community/Kokoro-82M-v1.0-ONNX");
        AssertEx.Equal("am_adam", response.DefaultVoiceProfile);

        // Omitting the voice fields on a later save keeps the current stored values (additive merge).
        var merged = new SaveNodeSettingsRequest().ToStoredSettings(stored);
        AssertEx.Equal(expected: true, merged.VoiceFeatureEnabled);
        AssertEx.Equal("am_adam", merged.DefaultVoiceProfile);
        AssertEx.NotNull(merged.AllowedVoiceModels);
    }

    [Test]
    public void NodeSettings_RerankerModelName_RoundTripsThroughMapper()
    {
        // A supplied (trimmed) value is stored and surfaced; omitting it on a later save keeps the current value; an
        // empty string is the "Off" signal that clears it (the store's Normalize later maps blank to null = disabled).
        var request = new SaveNodeSettingsRequest
        {
            RerankerModelName = "  bge-reranker-v2-m3  "
        };

        var stored = request.ToStoredSettings(new StoredNodeSettings());
        AssertEx.Equal("bge-reranker-v2-m3", stored.RerankerModelName);

        var response = stored.ToResponse();
        AssertEx.Equal("bge-reranker-v2-m3", response.RerankerModelName);

        // Omitting the field on a later save keeps the current stored value (additive merge).
        var merged = new SaveNodeSettingsRequest().ToStoredSettings(stored);
        AssertEx.Equal("bge-reranker-v2-m3", merged.RerankerModelName);

        // The "Off" option sends an empty string, which clears the reranker model name.
        var cleared = new SaveNodeSettingsRequest { RerankerModelName = string.Empty }.ToStoredSettings(stored);
        AssertEx.Equal(string.Empty, cleared.RerankerModelName);
    }

    private static TestingWebAppFactory CreateFactory(INodeSettingsStore nodeSettingsStore, ICapabilityReporter? capabilityReporter = null)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeSettingsStore>();
                services.AddSingleton(nodeSettingsStore);
                services.RemoveAll<ICapabilityReporter>();
                services.AddSingleton(capabilityReporter ?? Substitute.For<ICapabilityReporter>());
            }
        };
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false));
    }
}
