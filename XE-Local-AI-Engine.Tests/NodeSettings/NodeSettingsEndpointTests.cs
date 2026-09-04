namespace XE_Local_AI_Engine.Tests.NodeSettings;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Validators;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

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
            CustomToolsEnabled = true,
            ToolCapableModels = ["qwen3:8b", "gemma3:12b"],
            OllamaEndpoint = "http://127.0.0.1:11500",
            HuggingFaceDefaultQuant = "Q5_K_M",
            LlamaMaxLoadedProcesses = 5,
            LlamaIdleTimeToLiveSeconds = 1200,
            KeepModelWarmEnabled = true,
            KeepModelWarmModelName = "repo/model:Q4_K_M",
            KeepModelWarmIntervalSeconds = 300,
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
        AssertEx.Equal(expected: true, settings.CustomToolsEnabled);
        AssertEx.Equal("http://127.0.0.1:11500", settings.OllamaEndpoint);
        AssertEx.Equal("Q5_K_M", settings.HuggingFaceDefaultQuant);
        AssertEx.Equal(expected: 5, settings.LlamaMaxLoadedProcesses);
        AssertEx.Equal(expected: 1200, settings.LlamaIdleTimeToLiveSeconds);
        AssertEx.Equal(expected: true, settings.KeepModelWarmEnabled);
        AssertEx.Equal("repo/model:Q4_K_M", settings.KeepModelWarmModelName);
        AssertEx.Equal(expected: 300, settings.KeepModelWarmIntervalSeconds);
        AssertEx.Equal(expected: 25, settings.MaxResponseSizeMb);
        AssertEx.Equal("b9700", settings.RecommendedLlamaCppTag);
        AssertEx.Equal(expected: 240, settings.OrchestrationIdleTimeoutSeconds);
        AssertEx.NotNull(settings.ToolCapableModels);
        AssertEx.Contains(settings.ToolCapableModels!, "gemma3:12b");
        // Bounds are surfaced for the React form.
        AssertEx.Equal(StoredNodeSettings.MaxLlamaMaxLoadedProcesses, settings.MaxAllowedLlamaMaxLoadedProcesses);
        AssertEx.Equal(StoredNodeSettings.MinKeepModelWarmIntervalSeconds, settings.MinKeepModelWarmIntervalSeconds);
        AssertEx.Equal(StoredNodeSettings.MaxKeepModelWarmIntervalSeconds, settings.MaxAllowedKeepModelWarmIntervalSeconds);
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
                             OllamaEndpoint = "http://127.0.0.1:11434",
                             KeepModelWarmEnabled = true,
                             KeepModelWarmModelName = "keep-me-warm",
                             KeepModelWarmIntervalSeconds = 180
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
                                                                                           && stored.OllamaEndpoint == "http://127.0.0.1:11434"
                                                                                           && stored.KeepModelWarmEnabled == true
                                                                                           && stored.KeepModelWarmModelName == "keep-me-warm"
                                                                                           && stored.KeepModelWarmIntervalSeconds == 180),
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
    public async Task SaveNodeSettings_WhenKeepModelWarmIntervalOutOfRange_ReturnsValidationProblem()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            KeepModelWarmIntervalSeconds = StoredNodeSettings.MaxKeepModelWarmIntervalSeconds + 1
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenKeepModelWarmEnabledWithoutModel_ReturnsValidationProblem()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            KeepModelWarmEnabled = true
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenKeepModelWarmEnabledWithOneProcessSlot_ReturnsValidationProblem()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(new StoredNodeSettings
                         {
                             KeepModelWarmEnabled = true,
                             KeepModelWarmModelName = "model-a",
                             LlamaMaxLoadedProcesses = 3
                         });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            LlamaMaxLoadedProcesses = 1
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenKeepModelWarmIntervalIsNotBelowMergedIdleTtl_ReturnsValidationProblem()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(new StoredNodeSettings
                         {
                             KeepModelWarmEnabled = true,
                             KeepModelWarmModelName = "model-a",
                             KeepModelWarmIntervalSeconds = 300,
                             LlamaIdleTimeToLiveSeconds = 900
                         });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            LlamaIdleTimeToLiveSeconds = 300
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenEffectiveRuntimeProcessCapIsOne_ReturnsValidationProblem()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(new StoredNodeSettings
                         {
                             KeepModelWarmEnabled = true,
                             KeepModelWarmModelName = "model-a",
                             KeepModelWarmIntervalSeconds = 60,
                             LlamaIdleTimeToLiveSeconds = 900
                         });
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithLlamaMaxLoadedProcesses(1)
                                                     .Build();
        await using var factory = CreateFactory(nodeSettingsStore, runtimeSettings: runtimeSettings);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest());
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenEffectiveRuntimeIdleTtlMatchesInterval_ReturnsValidationProblem()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(new StoredNodeSettings
                         {
                             KeepModelWarmEnabled = true,
                             KeepModelWarmModelName = "model-a",
                             KeepModelWarmIntervalSeconds = 300,
                             LlamaMaxLoadedProcesses = 3
                         });
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithLlamaIdleTimeToLive(TimeSpan.FromSeconds(300))
                                                     .Build();
        await using var factory = CreateFactory(nodeSettingsStore, runtimeSettings: runtimeSettings);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest());
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenAutoEffortFastModelIsNotNodeLocal_BindsTheErrorToTheField()
    {
        // The locality rejection must name the request property, the way the capacity rejection names
        // llamaMaxLoadedProcesses. Unbound, it falls to the generic bucket and neither the React select's per-field
        // error nor an API consumer can attribute it.
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            AutoEffortFastModelName = "ext:studio/qwen3-1.7b"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var error = document.RootElement.GetProperty("errors")[0];
        AssertEx.Equal("autoEffortFastModelName", error.GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void NodeSettings_KeepModelWarmFields_RoundTripThroughMapper()
    {
        var stored = new SaveNodeSettingsRequest
        {
            KeepModelWarmEnabled = false,
            KeepModelWarmModelName = "  repo/model:Q5_K_M  ",
            KeepModelWarmIntervalSeconds = 240
        }.ToStoredSettings(new StoredNodeSettings
        {
            KeepModelWarmEnabled = true
        });

        AssertEx.Equal(expected: false, stored.KeepModelWarmEnabled);
        AssertEx.Equal("repo/model:Q5_K_M", stored.KeepModelWarmModelName);
        AssertEx.Equal(expected: 240, stored.KeepModelWarmIntervalSeconds);

        var response = stored.ToResponse();
        AssertEx.Equal(expected: false, response.KeepModelWarmEnabled);
        AssertEx.Equal("repo/model:Q5_K_M", response.KeepModelWarmModelName);
        AssertEx.Equal(expected: 240, response.KeepModelWarmIntervalSeconds);
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
        await nodeSettingsStore.Received(1).SaveAsync(Arg.Is<StoredNodeSettings>(stored => stored.SpeculativeMode == "draft-simple" && stored.SpeculativeDraftModelName == "my-draft"),
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
    public void NodeSettings_VoiceFields_RoundTripWithoutActivatingNeuralVoiceBehavior()
    {
        var request = new SaveNodeSettingsRequest
        {
            VoiceFeatureEnabled = true,
            DefaultVoiceProfile = "  af_heart  "
        };

        var stored = request.ToStoredSettings(new StoredNodeSettings());

        AssertEx.Equal(expected: true, stored.VoiceFeatureEnabled);
        AssertEx.Equal("af_heart", stored.DefaultVoiceProfile);

        var response = stored.ToResponse();

        AssertEx.Equal(expected: true, response.VoiceFeatureEnabled);
        AssertEx.Equal("af_heart", response.DefaultVoiceProfile);

        // Omitting the voice fields on a later save keeps the current stored values (additive merge).
        var merged = new SaveNodeSettingsRequest().ToStoredSettings(stored);
        AssertEx.Equal(expected: true, merged.VoiceFeatureEnabled);
        AssertEx.Equal("af_heart", merged.DefaultVoiceProfile);
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
        var cleared = new SaveNodeSettingsRequest
        {
            RerankerModelName = string.Empty
        }.ToStoredSettings(stored);
        AssertEx.Equal(string.Empty, cleared.RerankerModelName);
    }

    [Test]
    public void NodeSettings_AutoEffortFastModelName_RoundTripsThroughMapper()
    {
        // Same shape as the reranker select: a trimmed value round-trips, omitting the field keeps the stored value,
        // and the "Off" option sends an empty string that clears it.
        var request = new SaveNodeSettingsRequest
        {
            AutoEffortFastModelName = "  qwen3-1.7b  "
        };

        var stored = request.ToStoredSettings(new StoredNodeSettings());
        AssertEx.Equal("qwen3-1.7b", stored.AutoEffortFastModelName);

        var response = stored.ToResponse();
        AssertEx.Equal("qwen3-1.7b", response.AutoEffortFastModelName);

        var merged = new SaveNodeSettingsRequest().ToStoredSettings(stored);
        AssertEx.Equal("qwen3-1.7b", merged.AutoEffortFastModelName);

        var cleared = new SaveNodeSettingsRequest
        {
            AutoEffortFastModelName = string.Empty
        }.ToStoredSettings(stored);
        AssertEx.Equal(string.Empty, cleared.AutoEffortFastModelName);
    }

    [Test]
    public void NodeSettings_UsageRates_RoundTripThroughMapper()
    {
        // A supplied rate map is wrapped into the stored shape and surfaced flat on GET; omitting the field on a later
        // save keeps the current override (null-preserving merge) and does NOT wipe unrelated settings.
        var request = new SaveNodeSettingsRequest
        {
            OllamaEndpoint = "http://127.0.0.1:11434",
            UsageRates = new Dictionary<string, ModelRate>
            {
                ["gpt-5"] = new()
                {
                    InputPer1M = 1.25,
                    OutputPer1M = 10
                }
            }
        };

        var stored = request.ToStoredSettings(new StoredNodeSettings());
        AssertEx.NotNull(stored.UsageRates);
        AssertEx.NotNull(stored.UsageRates!.Models);
        AssertEx.Equal(expected: 1.25d, stored.UsageRates.Models!["gpt-5"].InputPer1M);
        AssertEx.Equal(expected: 10d, stored.UsageRates.Models["gpt-5"].OutputPer1M);

        var response = stored.ToResponse();
        AssertEx.NotNull(response.UsageRates);
        AssertEx.Equal(expected: 1.25d, response.UsageRates!["gpt-5"].InputPer1M);

        // Omitting UsageRates on a later save keeps the current override AND leaves the other stored fields intact.
        var merged = new SaveNodeSettingsRequest
        {
            OllamaEndpoint = "http://127.0.0.1:11500"
        }.ToStoredSettings(stored);
        AssertEx.NotNull(merged.UsageRates);
        AssertEx.Equal(expected: 1.25d, merged.UsageRates!.Models!["gpt-5"].InputPer1M);
        AssertEx.Equal("http://127.0.0.1:11500", merged.OllamaEndpoint);
    }

    [Test]
    public void SaveNodeSettings_UsageRateValidation_RejectsNegativeAndBlank_AcceptsValid()
    {
        // Boundary validation for the usage-rate map (host-independent — exercises the FluentValidation rule directly, so
        // it does not depend on full host startup).
        var validator = new SaveNodeSettingsRequestValidator();

        var negative = validator.Validate(new SaveNodeSettingsRequest
        {
            UsageRates = new Dictionary<string, ModelRate>
            {
                ["gpt-5"] = new()
                {
                    InputPer1M = -1,
                    OutputPer1M = 10
                }
            }
        });
        AssertEx.False(negative.IsValid);

        var blankKey = validator.Validate(new SaveNodeSettingsRequest
        {
            UsageRates = new Dictionary<string, ModelRate>
            {
                ["   "] = new()
                {
                    InputPer1M = 1,
                    OutputPer1M = 1
                }
            }
        });
        AssertEx.False(blankKey.IsValid);

        var valid = validator.Validate(new SaveNodeSettingsRequest
        {
            UsageRates = new Dictionary<string, ModelRate>
            {
                ["gpt-5"] = new()
                {
                    InputPer1M = 1.25,
                    OutputPer1M = 10
                }
            }
        });
        AssertEx.True(valid.IsValid);
    }

    private static TestServerWebAppFactory CreateFactory(INodeSettingsStore nodeSettingsStore,
        ICapabilityReporter? capabilityReporter = null,
        INodeRuntimeSettings? runtimeSettings = null)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeSettingsStore>();
                services.AddSingleton(nodeSettingsStore);
                services.RemoveAll<ICapabilityReporter>();
                services.AddSingleton(capabilityReporter ?? Substitute.For<ICapabilityReporter>());
                if (runtimeSettings is not null)
                {
                    services.RemoveAll<INodeRuntimeSettings>();
                    services.AddSingleton(runtimeSettings);
                }
            }
        };
    }

    private static HttpRequestMessage CreateRequest(TestServerWebAppFactory factory, HttpMethod method, string uri)
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
