namespace XE_Local_AI_Engine.Tests.NodeSettings;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Validators;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

[Category(TestCategories.Integration)]
public sealed class NodeSettingsEndpointTests
{
    // The mapper tests below exercise the non-switch fields; the switches' effective values have their own endpoint test.
    private static readonly NodeSettingsEffectiveValues DefaultEffectiveValues =
        StubNodeRuntimeSettings.Create().Build().ResolveEffectiveValues(new StoredNodeSettings());

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task GetNodeSettings_ReturnsStoredSettings()
    {
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
        {
            MaxMessageRequestTimeoutSeconds = 120
        });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var response = await client.SendAsync(request);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 120, settings.MaxMessageRequestTimeoutSeconds);
        AssertEx.Equal(StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds, settings.MinMessageRequestTimeoutSeconds);
        AssertEx.Equal(StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds, settings.MaxAllowedMessageRequestTimeoutSeconds);
        await nodeSettingsStore.Received(1).LoadAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenValid_SavesAndReportsCapabilities()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 600
        });
        using var response = await client.SendAsync(request);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 600, settings.MaxMessageRequestTimeoutSeconds);
        await nodeSettingsStore.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate).MaxMessageRequestTimeoutSeconds == 600),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenValid_PreservesTheStoredMachineKey()
    {
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
        {
            MachineKey = "abc"
        });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 600
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        // The key comes off the record the store holds when the write runs, not off the one the request carried:
        // that is what survives a key minted between this save's load and its write.
        await nodeSettingsStore.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate, new StoredNodeSettings
                {
                    MachineKey = "abc"
                }).MachineKey == "abc"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenASiblingRegistersAToolCapableModelBeforeTheWrite_KeepsItsEntry()
    {
        // The request's optional fields are a partial merge: everything it omits is resolved from the record the
        // endpoint hands the service. Resolved from a snapshot loaded here, a save of the chat timeout wrote that
        // snapshot's ToolCapableModels back over a registration that landed while the save was validating.
        var siblingHasWritten = false;
        var nodeSettingsStore = new FakeNodeSettingsStore(new StoredNodeSettings
            {
                ToolCapableModels = ["already-approved"]
            },
            siblingWriteBeforeTheUpdate: latest =>
            {
                if (siblingHasWritten)
                {
                    return latest;
                }

                siblingHasWritten = true;
                return latest with
                {
                    ToolCapableModels = [.. latest.ToolCapableModels ?? [], "registered-while-the-save-validated"]
                };
            });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 900
        });
        using var response = await client.SendAsync(request);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 900, settings.MaxMessageRequestTimeoutSeconds, "the field the request changed still lands.");
        AssertEx.True(settings.ToolCapableModels?.Contains("registered-while-the-save-validated") == true,
            "a registration that landed while this save validated must survive a request that never named the field.");
        AssertEx.True(nodeSettingsStore.Current.ToolCapableModels?.Contains("already-approved") == true);
    }

    [Test]
    public async Task SaveNodeSettings_WhenTheRecordKeepsChangingUnderTheSave_ReturnsConflict()
    {
        // Refused rather than written unvalidated: a writer that never stops means no attempt ever validated the
        // record its write would land on, and the operator is told to reload instead of being told it worked.
        var siblingWrites = 0;
        var nodeSettingsStore = new FakeNodeSettingsStore(new StoredNodeSettings(),
            siblingWriteBeforeTheUpdate: latest => latest with
            {
                ToolCapableModels = [.. latest.ToolCapableModels ?? [], $"sibling-{++siblingWrites}"]
            });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 900
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var conflict = await ReadJsonAsync<NodeSettingsConflictResponse>(response);
        AssertEx.Equal("Node settings changed while this save was being validated. Reload and retry.", conflict.Message);
        AssertEx.Equal(StoredNodeSettings.DefaultMaxMessageRequestTimeoutSeconds,
            nodeSettingsStore.Current.MaxMessageRequestTimeoutSeconds,
            "nothing the save proposed may reach disk.");
    }

    [Test]
    public async Task SaveNodeSettings_WhenOutOfRange_ReturnsValidationProblem()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 1
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WithNewMigratedFields_RoundTripsThroughGet()
    {
        StoredNodeSettings? saved = null;
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(_ => saved ?? new StoredNodeSettings());
        // The save is a read-modify-write, so the round-trip fake has to apply the mutation the way the store does.
        nodeSettingsStore.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
                         .Returns(call =>
                         {
                             saved = call.Arg<Func<StoredNodeSettings, StoredNodeSettings>>()(saved ?? new StoredNodeSettings());
                             return Task.FromResult(saved);
                         });

        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var putRequest = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        putRequest.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 300,
            EnableTools = false,
            CustomToolsEnabled = true,
            ToolRelevanceEnabled = true,
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
        using var putResponse = await client.SendAsync(putRequest);
        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        using var getRequest = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var getResponse = await client.SendAsync(getRequest);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(getResponse);

        AssertEx.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        AssertEx.Equal(expected: false, settings.EnableTools);
        AssertEx.Equal(expected: true, settings.CustomToolsEnabled);
        AssertEx.Equal(expected: true, settings.ToolRelevanceEnabled);
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
    public async Task SaveNodeSettings_WithTheTunables_RoundTripsThroughGetWithTheirBounds()
    {
        var saved = new StoredNodeSettings
        {
            LlamaChatCacheRamMiB = 2048
        };
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => saved);
        nodeSettingsStore.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
                         .Returns(call =>
                         {
                             saved = call.Arg<Func<StoredNodeSettings, StoredNodeSettings>>()(saved);
                             return Task.FromResult(saved);
                         });

        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var putRequest = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        putRequest.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            TranscriptionIdleTimeoutMinutes = 30,
            LlamaReadinessTimeoutCapSeconds = 900,
            LlamaChatHttpTimeoutSeconds = 7200,
            LlamaEmbeddingHttpTimeoutSeconds = 120,
            // -1 is "back to automatic": the stored 2048 must become null.
            LlamaChatCacheRamMiB = StoredNodeSettings.LlamaChatCacheRamMiBAuto,
            LlamaCpuThreadReserve = 2,
            LlamaGpuReservePercent = 10,
            LlamaRamReservePercent = 20,
            ImageIdleTimeToLiveSeconds = 600,
            ModelFitSafetyMarginPercent = 15,
            MaxProviderCallsPerInvocation = 100,
            CustomToolMaxTimeoutSeconds = 600,
            WebFetchTimeoutSeconds = 30,
            WebFetchMaxContentChars = 20_000,
            KnowledgeSearchDefaultResults = 4,
            KnowledgeSearchMaxResults = 8,
            HuggingFaceDownloadConnections = 6,
            TranscriptionInferenceTimeoutMinutes = 60,
            AgentHomeMaxRunSeconds = 1200,
            AgentHomeRunRetentionDays = 14,
            ReasoningBudgetMinimalTokens = 512,
            ReasoningBudgetLowTokens = 1024,
            ReasoningBudgetMediumTokens = 4096,
            ReasoningBudgetHighTokens = 16384,
            DefaultReasoningEffort = "minimal",
            ChatOutputCapMode = "notice",
            ChatOutputCapMaxTokens = 8192
        });
        using var putResponse = await client.SendAsync(putRequest);
        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        using var getRequest = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var getResponse = await client.SendAsync(getRequest);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(getResponse);

        AssertEx.Equal(expected: 30, settings.TranscriptionIdleTimeoutMinutes);
        AssertEx.Equal(expected: 900, settings.LlamaReadinessTimeoutCapSeconds);
        AssertEx.Equal(expected: 7200, settings.LlamaChatHttpTimeoutSeconds);
        AssertEx.Equal(expected: 120, settings.LlamaEmbeddingHttpTimeoutSeconds);
        AssertEx.Null(settings.LlamaChatCacheRamMiB);
        AssertEx.Equal(expected: 2, settings.LlamaCpuThreadReserve);
        AssertEx.Equal(expected: 10, settings.LlamaGpuReservePercent);
        AssertEx.Equal(expected: 20, settings.LlamaRamReservePercent);
        AssertEx.Equal(expected: 600, settings.ImageIdleTimeToLiveSeconds);
        AssertEx.Equal(expected: 15, settings.ModelFitSafetyMarginPercent);
        AssertEx.Equal(expected: 100, settings.MaxProviderCallsPerInvocation);
        AssertEx.Equal(expected: 600, settings.CustomToolMaxTimeoutSeconds);
        AssertEx.Equal(expected: 30, settings.WebFetchTimeoutSeconds);
        AssertEx.Equal(expected: 20_000, settings.WebFetchMaxContentChars);
        AssertEx.Equal(expected: 4, settings.KnowledgeSearchDefaultResults);
        AssertEx.Equal(expected: 8, settings.KnowledgeSearchMaxResults);
        AssertEx.Equal(expected: 6, settings.HuggingFaceDownloadConnections);
        AssertEx.Equal(expected: 60, settings.TranscriptionInferenceTimeoutMinutes);
        AssertEx.Equal(expected: 1200, settings.AgentHomeMaxRunSeconds);
        AssertEx.Equal(expected: 14, settings.AgentHomeRunRetentionDays);
        AssertEx.Equal(expected: 512, settings.ReasoningBudgetMinimalTokens);
        AssertEx.Equal(expected: 1024, settings.ReasoningBudgetLowTokens);
        AssertEx.Equal(expected: 4096, settings.ReasoningBudgetMediumTokens);
        AssertEx.Equal(expected: 16384, settings.ReasoningBudgetHighTokens);
        AssertEx.Equal("minimal", settings.DefaultReasoningEffort);
        AssertEx.Equal("notice", settings.ChatOutputCapMode);
        AssertEx.Equal(expected: 8192, settings.ChatOutputCapMaxTokens);
        AssertEx.Equal(StoredNodeSettings.MinChatOutputCapMaxTokens, settings.MinChatOutputCapMaxTokens);
        AssertEx.Equal(StoredNodeSettings.MaxChatOutputCapMaxTokens, settings.MaxAllowedChatOutputCapMaxTokens);
        AssertEx.Equal(StoredNodeSettings.MinReasoningBudgetTokens, settings.MinReasoningBudgetTokens);
        AssertEx.Equal(StoredNodeSettings.MaxReasoningBudgetTokens, settings.MaxAllowedReasoningBudgetTokens);
        // Bounds are server-authoritative for the React form.
        AssertEx.Equal(StoredNodeSettings.MinLlamaReadinessTimeoutCapSeconds, settings.MinLlamaReadinessTimeoutCapSeconds);
        AssertEx.Equal(StoredNodeSettings.MaxLlamaChatCacheRamMiB, settings.MaxAllowedLlamaChatCacheRamMiB);
        AssertEx.Equal(StoredNodeSettings.MaxKnowledgeSearchResults, settings.MaxAllowedKnowledgeSearchResults);
        AssertEx.Equal(StoredNodeSettings.MaxTranscriptionIdleTimeoutMinutes, settings.MaxAllowedTranscriptionIdleTimeoutMinutes);
        AssertEx.Equal(StoredNodeSettings.MinHuggingFaceDiskMarginBytes, settings.MinHuggingFaceDiskMarginBytes);
        AssertEx.Equal(StoredNodeSettings.MaxHuggingFaceDiskMarginBytes, settings.MaxAllowedHuggingFaceDiskMarginBytes);
    }

    [Test]
    public async Task SaveNodeSettings_WithAnUnbudgetedDefaultReasoningEffort_IsRejectedUnderTheWireName()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            DefaultReasoningEffort = "none"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal("defaultReasoningEffort", document.RootElement.GetProperty("errors")[0].GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_UnsetSentinels_ClearTheBudgetsAndOutputCap_AndTheShippedDefaultsApplyAgain()
    {
        var saved = new StoredNodeSettings
        {
            ReasoningBudgetMinimalTokens = 512,
            ReasoningBudgetLowTokens = 1024,
            ReasoningBudgetMediumTokens = 4096,
            ReasoningBudgetHighTokens = 16384,
            DefaultReasoningEffort = "high",
            ChatOutputCapMode = "off",
            ChatOutputCapMaxTokens = 2048
        };
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => saved);
        nodeSettingsStore.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
                         .Returns(call =>
                         {
                             saved = call.Arg<Func<StoredNodeSettings, StoredNodeSettings>>()(saved);
                             return Task.FromResult(saved);
                         });

        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        // A save of an unrelated field (all seven null) keeps every stored value.
        using var keepRequest = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        keepRequest.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            WebFetchTimeoutSeconds = 30
        });
        using var keepResponse = await client.SendAsync(keepRequest);
        AssertEx.Equal(HttpStatusCode.OK, keepResponse.StatusCode);
        AssertEx.Equal(expected: 1024, saved.ReasoningBudgetLowTokens);
        AssertEx.Equal("off", saved.ChatOutputCapMode);

        using var unsetRequest = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        unsetRequest.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            ReasoningBudgetMinimalTokens = StoredNodeSettings.TokenSettingUnset,
            ReasoningBudgetLowTokens = StoredNodeSettings.TokenSettingUnset,
            ReasoningBudgetMediumTokens = StoredNodeSettings.TokenSettingUnset,
            ReasoningBudgetHighTokens = StoredNodeSettings.TokenSettingUnset,
            DefaultReasoningEffort = "",
            ChatOutputCapMode = "",
            ChatOutputCapMaxTokens = StoredNodeSettings.TokenSettingUnset
        });
        using var unsetResponse = await client.SendAsync(unsetRequest);
        AssertEx.Equal(HttpStatusCode.OK, unsetResponse.StatusCode);

        using var getRequest = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var getResponse = await client.SendAsync(getRequest);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(getResponse);
        AssertEx.Null(settings.ReasoningBudgetMinimalTokens);
        AssertEx.Null(settings.ReasoningBudgetLowTokens);
        AssertEx.Null(settings.ReasoningBudgetMediumTokens);
        AssertEx.Null(settings.ReasoningBudgetHighTokens);
        AssertEx.Null(settings.DefaultReasoningEffort);
        AssertEx.Null(settings.ChatOutputCapMode);
        AssertEx.Null(settings.ChatOutputCapMaxTokens);

        // What a turn reads now is the shipped default, not the cleared value.
        var runtimeSettings = factory.Services.GetRequiredService<INodeRuntimeSettings>();
        var budgets = await runtimeSettings.GetReasoningBudgetsAsync();
        AssertEx.Equal(expected: 1024, budgets.Minimal);
        AssertEx.Equal(expected: 2048, budgets.Low);
        AssertEx.Equal(expected: 8192, budgets.Medium);
        AssertEx.Equal(expected: 24576, budgets.High);
        AssertEx.Equal("low", budgets.UnspecifiedEffort);
        var outputCap = await runtimeSettings.GetChatOutputCapAsync();
        AssertEx.Equal(StoredNodeSettings.ChatOutputCapModeCap, outputCap.Mode);
        AssertEx.Equal(StoredNodeSettings.DefaultChatOutputCapMaxTokens, outputCap.MaxTokens);
    }

    /// <summary>Only -1 and the empty string are the "back to default" sentinels; every other out-of-range value is still refused.</summary>
    [Test]
    [Arguments(-2)]
    [Arguments(0)]
    [Arguments(StoredNodeSettings.MinReasoningBudgetTokens - 1)]
    [Arguments(StoredNodeSettings.MaxReasoningBudgetTokens + 1)]
    public void SaveNodeSettings_ReasoningBudgetAndOutputCapCeiling_RejectEverythingOutOfRangeButTheSentinel(int tokens)
    {
        var validator = new SaveNodeSettingsRequestValidator();

        var budget = validator.Validate(new SaveNodeSettingsRequest
        {
            ReasoningBudgetMinimalTokens = tokens
        });
        var ceiling = validator.Validate(new SaveNodeSettingsRequest
        {
            ChatOutputCapMaxTokens = tokens
        });
        var sentinels = validator.Validate(new SaveNodeSettingsRequest
        {
            ReasoningBudgetMinimalTokens = StoredNodeSettings.TokenSettingUnset,
            ChatOutputCapMaxTokens = StoredNodeSettings.TokenSettingUnset,
            DefaultReasoningEffort = "",
            ChatOutputCapMode = ""
        });

        AssertEx.False(budget.IsValid);
        AssertEx.False(ceiling.IsValid);
        AssertEx.True(sentinels.IsValid, string.Join("; ", sentinels.Errors.Select(static error => error.ErrorMessage)));
    }

    [Test]
    [Arguments(" ")]
    [Arguments("none")]
    [Arguments("default")]
    public void SaveNodeSettings_EffortAndOutputCapMode_RejectAnyOtherBlankOrUnknownLiteral(string value)
    {
        var validator = new SaveNodeSettingsRequestValidator();

        AssertEx.False(validator.Validate(new SaveNodeSettingsRequest { DefaultReasoningEffort = value }).IsValid);
        AssertEx.False(validator.Validate(new SaveNodeSettingsRequest { ChatOutputCapMode = value }).IsValid);
    }

    [Test]
    public async Task SaveNodeSettings_WhenTheKnowledgeSearchDefaultExceedsTheMaximum_BindsTheErrorToTheDefault()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            KnowledgeSearchDefaultResults = 10,
            KnowledgeSearchMaxResults = 5
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal("knowledgeSearchDefaultResults", document.RootElement.GetProperty("errors")[0].GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenTheAgentHomeRunLimitIsBelowTheCommandTimeout_BindsTheErrorToTheRunLimit()
    {
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
        {
            AgentHomeCommandTimeoutSeconds = 600
        });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            AgentHomeMaxRunSeconds = 300
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal("agentHomeMaxRunSeconds", document.RootElement.GetProperty("errors")[0].GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WithTheChatKnobs_RoundTripsThroughGetWithTheirBounds()
    {
        var saved = new StoredNodeSettings();
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => saved);
        nodeSettingsStore.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
                         .Returns(call =>
                         {
                             saved = call.Arg<Func<StoredNodeSettings, StoredNodeSettings>>()(saved);
                             return Task.FromResult(saved);
                         });

        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var putRequest = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        putRequest.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            ToolPipelineMaxIterationsPerRequest = 80,
            ToolPipelineMaxToolResultChars = 100_000,
            ToolPipelineMaxConsecutiveInvalidToolCalls = 7,
            DefaultContextTokens = 65_536,
            ProviderBudgetRecentMessagesToKeep = 20,
            ProviderBudgetMaxCumulativeInputTokens = 8_000_000,
            ContextBudgetRecentTurnKeepCount = 9,
            CompactionAutoEnabled = false,
            CompactionAutoCompactPercent = 90,
            CompactionRecentMessagesVerbatim = 16,
            CompactionDistillEnabled = false,
            MaxInlinedAttachmentChars = 200_000,
            KnowledgeChatTopK = 12,
            ProviderRetryEnabled = false,
            ProviderMaxRetries = 0,
            SpawnMaxConcurrent = 1,
            SpawnMaxCloud = 0,
            SpawnQueueWaitSeconds = 0
        });
        using var putResponse = await client.SendAsync(putRequest);
        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        using var getRequest = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var getResponse = await client.SendAsync(getRequest);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(getResponse);

        AssertEx.Equal(expected: 80, settings.ToolPipelineMaxIterationsPerRequest);
        AssertEx.Equal(expected: 100_000, settings.ToolPipelineMaxToolResultChars);
        AssertEx.Equal(expected: 7, settings.ToolPipelineMaxConsecutiveInvalidToolCalls);
        AssertEx.Equal(expected: 65_536, settings.DefaultContextTokens);
        AssertEx.Equal(expected: 20, settings.ProviderBudgetRecentMessagesToKeep);
        AssertEx.Equal(expected: 8_000_000, settings.ProviderBudgetMaxCumulativeInputTokens);
        AssertEx.Equal(expected: 9, settings.ContextBudgetRecentTurnKeepCount);
        AssertEx.Equal(expected: false, settings.CompactionAutoEnabled);
        AssertEx.Equal(expected: 90, settings.CompactionAutoCompactPercent);
        AssertEx.Equal(expected: 16, settings.CompactionRecentMessagesVerbatim);
        AssertEx.Equal(expected: false, settings.CompactionDistillEnabled);
        AssertEx.Equal(expected: 200_000, settings.MaxInlinedAttachmentChars);
        AssertEx.Equal(expected: 12, settings.KnowledgeChatTopK);
        AssertEx.Equal(expected: false, settings.ProviderRetryEnabled);
        AssertEx.Equal(expected: 0, settings.ProviderMaxRetries);
        AssertEx.Equal(expected: 1, settings.SpawnMaxConcurrent);
        AssertEx.Equal(expected: 0, settings.SpawnMaxCloud);
        AssertEx.Equal(expected: 0, settings.SpawnQueueWaitSeconds);
        // Bounds are server-authoritative for the React form.
        AssertEx.Equal(StoredNodeSettings.MinToolPipelineMaxToolResultChars, settings.MinToolPipelineMaxToolResultChars);
        AssertEx.Equal(StoredNodeSettings.MaxDefaultContextTokens, settings.MaxAllowedDefaultContextTokens);
        AssertEx.Equal(StoredNodeSettings.MinCompactionAutoCompactPercent, settings.MinCompactionAutoCompactPercent);
        AssertEx.Equal(StoredNodeSettings.MaxCompactionAutoCompactPercent, settings.MaxAllowedCompactionAutoCompactPercent);
        AssertEx.Equal(StoredNodeSettings.MinSpawnMaxConcurrent, settings.MinSpawnMaxConcurrent);
        AssertEx.Equal(StoredNodeSettings.MaxSpawnQueueWaitSeconds, settings.MaxAllowedSpawnQueueWaitSeconds);
    }

    [Test]
    [Arguments(StoredNodeSettings.MinSpawnMaxConcurrent - 1)]
    [Arguments(StoredNodeSettings.MaxSpawnMaxConcurrent + 1)]
    public async Task SaveNodeSettings_WhenTheSpawnCapIsOutOfRange_BindsTheErrorToTheWireField(int spawnMaxConcurrent)
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            SpawnMaxConcurrent = spawnMaxConcurrent
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal("spawnMaxConcurrent", document.RootElement.GetProperty("errors")[0].GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(-2)]
    [Arguments(131073)]
    public async Task SaveNodeSettings_WhenTheCacheRamIsOutOfRange_ReturnsValidationProblem(int cacheRamMiB)
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            LlamaChatCacheRamMiB = cacheRamMiB
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The error names the wire field itself, never the nullable's ".Value" member.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal("llamaChatCacheRamMiB", document.RootElement.GetProperty("errors")[0].GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(0L)]
    [Arguments(StoredNodeSettings.MaxHuggingFaceDiskMarginBytes + 1)]
    public async Task SaveNodeSettings_WhenTheDiskMarginIsOutOfRange_ReturnsValidationProblem(long marginBytes)
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            HuggingFaceDiskMarginBytes = marginBytes
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The error names the wire field itself, never the nullable's ".Value" member.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal("huggingFaceDiskMarginBytes", document.RootElement.GetProperty("errors")[0].GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenOmittingOptionalFields_KeepsCurrentStoredValues()
    {
        var stored = new StoredNodeSettings
        {
            MaxMessageRequestTimeoutSeconds = 300,
            RecommendedLlamaCppTag = "b9692",
            OllamaEndpoint = "http://127.0.0.1:11434",
            KeepModelWarmEnabled = true,
            KeepModelWarmModelName = "keep-me-warm",
            KeepModelWarmIntervalSeconds = 180,
            ToolRelevanceEnabled = true
        };
        var nodeSettingsStore = NewSettingsStore(stored);
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        // Omit EVERY field — including the chat timeout (now optional) — so the merge must keep all current values.
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest());
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        // Against the record the store HOLDS: the save re-applies its projection to the write-time record, and
        // declines to project at all onto a record that is not the one it validated.
        await nodeSettingsStore.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate, stored).MaxMessageRequestTimeoutSeconds == 300
                && Persisted(mutate, stored).RecommendedLlamaCppTag == "b9692"
                && Persisted(mutate, stored).OllamaEndpoint == "http://127.0.0.1:11434"
                && Persisted(mutate, stored).KeepModelWarmEnabled == true
                && Persisted(mutate, stored).KeepModelWarmModelName == "keep-me-warm"
                && Persisted(mutate, stored).KeepModelWarmIntervalSeconds == 180
                // A field missing from ToStoredSettings' hand-enumerated `new StoredNodeSettings { … }` is silently
                // NULLed on EVERY put, and no reflection test guards that enumeration. A failure here is a data-loss
                // bug, never a test to relax.
                && Persisted(mutate, stored).ToolRelevanceEnabled == true),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenRecommendedTagMalformed_ReturnsValidationProblem()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 300,
            RecommendedLlamaCppTag = "not-a-tag"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenOllamaEndpointNotAUrl_ReturnsValidationProblem()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 300,
            OllamaEndpoint = "not a url"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenLlamaMaxLoadedProcessesOutOfRange_ReturnsValidationProblem()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            MaxMessageRequestTimeoutSeconds = 300,
            LlamaMaxLoadedProcesses = 999
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenKeepModelWarmIntervalOutOfRange_ReturnsValidationProblem()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            KeepModelWarmIntervalSeconds = StoredNodeSettings.MaxKeepModelWarmIntervalSeconds + 1
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenKeepModelWarmEnabledWithoutModel_ReturnsValidationProblem()
    {
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings());
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            KeepModelWarmEnabled = true
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenKeepModelWarmEnabledWithOneProcessSlot_ReturnsValidationProblem()
    {
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
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
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenKeepModelWarmIntervalIsNotBelowMergedIdleTtl_ReturnsValidationProblem()
    {
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
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
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenEffectiveRuntimeProcessCapIsOne_ReturnsValidationProblem()
    {
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
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
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenEffectiveRuntimeIdleTtlMatchesInterval_ReturnsValidationProblem()
    {
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
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
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenAutoEffortFastModelIsNotNodeLocal_BindsTheErrorToTheField()
    {
        // The locality rejection must name the request property, the way the capacity rejection names
        // llamaMaxLoadedProcesses. Unbound, it falls to the generic bucket and neither the React select's per-field
        // error nor an API consumer can attribute it.
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings());
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            AutoEffortFastModelName = "ext:studio/qwen3-1.7b"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("errors")[0];
        AssertEx.Equal("autoEffortFastModelName", error.GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
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

        var response = stored.ToResponse(DefaultEffectiveValues);
        AssertEx.Equal(expected: false, response.KeepModelWarmEnabled);
        AssertEx.Equal("repo/model:Q5_K_M", response.KeepModelWarmModelName);
        AssertEx.Equal(expected: 240, response.KeepModelWarmIntervalSeconds);
    }

    [Test]
    public async Task SaveNodeSettings_WhenKvCacheTypeUnknown_ReturnsValidationProblem()
    {
        // A junk KV type must never persist: the launch policy validates it in its constructor, so a stored bad value
        // would fail host build on the next restart instead of degrading.
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            KvCacheType = "q5_1"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenKvCacheTypeKnown_Saves()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            KvCacheType = "q4_0"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await nodeSettingsStore.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate).KvCacheType == "q4_0"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenDraftModeWithoutDraftModel_ReturnsValidationProblem()
    {
        // A draft-* speculative mode with no draft model must be rejected at the boundary (would fail chat-server start).
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            SpeculativeMode = "draft-simple"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenNgramModeWithoutDraftModel_Saves()
    {
        // ngram-* modes self-speculate; they need no draft model, so an empty draft-model name is valid.
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            SpeculativeMode = "ngram-mod"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await nodeSettingsStore.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate).SpeculativeMode == "ngram-mod"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenDraftModeWithDraftModel_Saves()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            SpeculativeMode = "draft-simple",
            SpeculativeDraftModelName = "my-draft"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await nodeSettingsStore.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate).SpeculativeMode == "draft-simple" && Persisted(mutate).SpeculativeDraftModelName == "my-draft"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenClearingDraftModelUnderStoredDraftMode_ReturnsValidationProblem()
    {
        // The partial-update edge the boundary validator can't see: a draft-* mode is already stored, and this request
        // (which omits SpeculativeMode) clears the draft model name. The post-merge guard must still reject it.
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
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
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
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

        var response = stored.ToResponse(DefaultEffectiveValues);

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

        var response = stored.ToResponse(DefaultEffectiveValues);
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

        var response = stored.ToResponse(DefaultEffectiveValues);
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
    public async Task UnsavedSwitch_WithASeededTrue_ReadsBackTrue_AndSavingFalsePersistsFalse()
    {
        var saved = new StoredNodeSettings();
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => saved);
        nodeSettingsStore.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
                         .Returns(call =>
                         {
                             saved = call.Arg<Func<StoredNodeSettings, StoredNodeSettings>>()(saved);
                             return Task.FromResult(saved);
                         });

        // The real accessor over appsettings seeds that turn both switches on; nothing was ever saved.
        await using var factory = CreateFactory(nodeSettingsStore,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["KnowledgeBase:AllowCloudModelAccess"] = "true",
                ["ChatRetention:Enabled"] = "true"
            });
        using var client = factory.CreateClient();

        using var firstGet = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var firstGetResponse = await client.SendAsync(firstGet);
        var unsaved = await ReadJsonAsync<NodeSettingsResponse>(firstGetResponse);
        AssertEx.True(unsaved.AllowCloudModelAccess, "An unsaved switch must report its seeded effective value.");
        AssertEx.True(unsaved.ChatRetentionEnabled, "An unsaved switch must report its seeded effective value.");

        using var putRequest = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        putRequest.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            AllowCloudModelAccess = false,
            ChatRetentionEnabled = false
        });
        using var putResponse = await client.SendAsync(putRequest);
        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var putBody = await ReadJsonAsync<NodeSettingsResponse>(putResponse);
        AssertEx.False(putBody.AllowCloudModelAccess);
        AssertEx.False(putBody.ChatRetentionEnabled);
        AssertEx.Equal(expected: false, saved.AllowCloudModelAccess);
        AssertEx.Equal(expected: false, saved.ChatRetentionEnabled);

        using var secondGet = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var secondGetResponse = await client.SendAsync(secondGet);
        var reread = await ReadJsonAsync<NodeSettingsResponse>(secondGetResponse);
        AssertEx.False(reread.AllowCloudModelAccess, "A saved false must override the seeded true.");
        AssertEx.False(reread.ChatRetentionEnabled, "A saved false must override the seeded true.");
    }

    [Test]
    public async Task FeatureSwitches_UnsavedReadBackTheirSeed_AndSavedValuesRoundTrip()
    {
        var saved = new StoredNodeSettings();
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => saved);
        nodeSettingsStore.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
                         .Returns(call =>
                         {
                             saved = call.Arg<Func<StoredNodeSettings, StoredNodeSettings>>()(saved);
                             return Task.FromResult(saved);
                         });

        // Seeds that differ from three code defaults (work sessions, external apps, AgentHome), the shipped-config shape.
        await using var factory = CreateFactory(nodeSettingsStore,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["WorkSessions:Enabled"] = "true",
                ["ExternalApps:Enabled"] = "true",
                ["AgentHome:Enabled"] = "true",
                ["Transcription:Enabled"] = "false"
            });
        using var client = factory.CreateClient();

        using var firstGet = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var firstGetResponse = await client.SendAsync(firstGet);
        var unsaved = await ReadJsonAsync<NodeSettingsResponse>(firstGetResponse);
        AssertEx.True(unsaved.WorkSessionsEnabled, "an unsaved switch reports its seed, not the code default");
        AssertEx.True(unsaved.ExternalAppsEnabled);
        AssertEx.True(unsaved.AgentHomeEnabled);
        AssertEx.False(unsaved.TranscriptionEnabled);
        AssertEx.True(unsaved.GraphWorkflowsEnabled, "an unseeded switch reports its code default");
        AssertEx.False(unsaved.DevWorkflowsEnabled);

        using var putRequest = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        putRequest.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            DevelopmentEnabled = false,
            WorkSessionsEnabled = true,
            GraphWorkflowsEnabled = false,
            TranscriptionEnabled = true,
            ExternalAppsEnabled = false,
            ComputeEnabled = true,
            AgentHomeEnabled = false,
            SchedulerEnabled = false,
            DevWorkflowsEnabled = true
        });
        using var putResponse = await client.SendAsync(putRequest);
        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        AssertEx.Equal(expected: false, saved.DevelopmentEnabled);
        AssertEx.Equal(expected: true, saved.WorkSessionsEnabled);
        AssertEx.Equal(expected: false, saved.GraphWorkflowsEnabled);
        AssertEx.Equal(expected: true, saved.TranscriptionEnabled);
        AssertEx.Equal(expected: false, saved.ExternalAppsEnabled);
        AssertEx.Equal(expected: true, saved.ComputeEnabled);
        AssertEx.Equal(expected: false, saved.AgentHomeEnabled);
        AssertEx.Equal(expected: false, saved.SchedulerEnabled);
        AssertEx.Equal(expected: true, saved.DevWorkflowsEnabled);

        using var secondGet = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var secondGetResponse = await client.SendAsync(secondGet);
        var reread = await ReadJsonAsync<NodeSettingsResponse>(secondGetResponse);
        AssertEx.False(reread.DevelopmentEnabled);
        AssertEx.True(reread.WorkSessionsEnabled);
        AssertEx.False(reread.GraphWorkflowsEnabled);
        AssertEx.True(reread.TranscriptionEnabled, "a saved true overrides the seeded false");
        AssertEx.False(reread.ExternalAppsEnabled, "a saved false overrides the seeded true");
        AssertEx.True(reread.ComputeEnabled);
        AssertEx.False(reread.AgentHomeEnabled);
        AssertEx.False(reread.SchedulerEnabled);
        AssertEx.True(reread.DevWorkflowsEnabled);
    }

    [Test]
    [Arguments(true, false, "devWorkflowsEnabled")]
    [Arguments(null, false, "workSessionsEnabled")]
    public async Task SaveNodeSettings_WhenDevWorkflowsWouldRunWithoutWorkSessions_BindsTheErrorToTheSwitchThatBrokeIt(bool? devWorkflows,
        bool workSessions,
        string expectedField)
    {
        // Development workflows already stored on, so the second row's request only takes work sessions away.
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
        {
            DevWorkflowsEnabled = devWorkflows is null ? true : null
        });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            DevWorkflowsEnabled = devWorkflows,
            WorkSessionsEnabled = workSessions
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal(expectedField, document.RootElement.GetProperty("errors")[0].GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenAgentHomeIsEnabledWithNoToolCapableModel_BindsTheErrorToTheSwitch()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore, StubNodeRuntimeSettings.Create().WithAgentHomeEnabled(false).WithToolCapableModels().WithToolCapableModelsSeed().Build());
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            AgentHomeEnabled = true
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal("agentHomeEnabled", document.RootElement.GetProperty("errors")[0].GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnsavedRetentionWindow_WithASeededNonDefaultValue_ReadsBackTheSeed_AndSavingAnotherValuePersistsIt()
    {
        var saved = new StoredNodeSettings();
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => saved);
        nodeSettingsStore.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
                         .Returns(call =>
                         {
                             saved = call.Arg<Func<StoredNodeSettings, StoredNodeSettings>>()(saved);
                             return Task.FromResult(saved);
                         });

        // The real accessor over an appsettings seed of one day; nothing was ever saved.
        await using var factory = CreateFactory(nodeSettingsStore,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ChatRetention:RetentionDays"] = "1"
            });
        using var client = factory.CreateClient();

        using var firstGet = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var firstGetResponse = await client.SendAsync(firstGet);
        var unsaved = await ReadJsonAsync<NodeSettingsResponse>(firstGetResponse);
        AssertEx.Equal(expected: 1, unsaved.ChatRetentionDays, "An unsaved retention window must report its seeded effective value.");
        AssertEx.Equal(StoredNodeSettings.DefaultAgentExecutionLogRetentionDays, unsaved.AgentExecutionLogRetentionDays);

        using var putRequest = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        putRequest.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            ChatRetentionDays = 7
        });
        using var putResponse = await client.SendAsync(putRequest);
        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var putBody = await ReadJsonAsync<NodeSettingsResponse>(putResponse);
        AssertEx.Equal(expected: 7, putBody.ChatRetentionDays);
        AssertEx.Equal(expected: 7, saved.ChatRetentionDays);

        using var secondGet = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var secondGetResponse = await client.SendAsync(secondGet);
        var reread = await ReadJsonAsync<NodeSettingsResponse>(secondGetResponse);
        AssertEx.Equal(expected: 7, reread.ChatRetentionDays, "A saved window must override the seed.");
    }

    [Test]
    public async Task SaveNodeSettings_WithTheKnowledgeAndUsageKnobs_RoundTripsThroughGetWithTheirBounds()
    {
        var saved = new StoredNodeSettings();
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => saved);
        nodeSettingsStore.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
                         .Returns(call =>
                         {
                             saved = call.Arg<Func<StoredNodeSettings, StoredNodeSettings>>()(saved);
                             return Task.FromResult(saved);
                         });

        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var putRequest = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        putRequest.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            KnowledgeAdaptiveRerankingEnabled = false,
            KnowledgeRetrievalLatencyBudgetMs = 1500,
            KnowledgeScheduledReindexEnabled = false,
            KnowledgeScheduledReindexIntervalMinutes = 120,
            KnowledgeAgentToolsEnabled = false,
            AllowCloudModelAccess = true,
            ChatRetentionEnabled = true,
            ChatRetentionDays = 90,
            AgentExecutionLogRetentionEnabled = false,
            AgentExecutionLogRetentionDays = 14,
            NodeDbBackupRetainCount = 5,
            BenchmarkKldCacheMaxBytes = 8L * 1024 * 1024 * 1024,
            SchedulerHistoryRetentionDays = 45
        });
        using var putResponse = await client.SendAsync(putRequest);
        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        using var getRequest = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var getResponse = await client.SendAsync(getRequest);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(getResponse);

        AssertEx.Equal(expected: false, settings.KnowledgeAdaptiveRerankingEnabled);
        AssertEx.Equal(expected: 1500, settings.KnowledgeRetrievalLatencyBudgetMs);
        AssertEx.Equal(expected: false, settings.KnowledgeScheduledReindexEnabled);
        AssertEx.Equal(expected: 120, settings.KnowledgeScheduledReindexIntervalMinutes);
        AssertEx.Equal(expected: false, settings.KnowledgeAgentToolsEnabled);
        AssertEx.Equal(expected: true, settings.AllowCloudModelAccess);
        AssertEx.Equal(expected: true, settings.ChatRetentionEnabled);
        AssertEx.Equal(expected: 90, settings.ChatRetentionDays);
        AssertEx.Equal(expected: false, settings.AgentExecutionLogRetentionEnabled);
        AssertEx.Equal(expected: 14, settings.AgentExecutionLogRetentionDays);
        AssertEx.Equal(expected: 5, settings.NodeDbBackupRetainCount);
        AssertEx.Equal(8L * 1024 * 1024 * 1024, settings.BenchmarkKldCacheMaxBytes);
        AssertEx.Equal(expected: 45, settings.SchedulerHistoryRetentionDays);
        // The cloud opt-in is its own switch: saving it must not re-stamp the external-access preset.
        AssertEx.Null(saved.ExternalAccessProfile);
        // Bounds are server-authoritative for the React form.
        AssertEx.Equal(StoredNodeSettings.MinKnowledgeRetrievalLatencyBudgetMs, settings.MinKnowledgeRetrievalLatencyBudgetMs);
        AssertEx.Equal(StoredNodeSettings.MaxKnowledgeScheduledReindexIntervalMinutes, settings.MaxAllowedKnowledgeScheduledReindexIntervalMinutes);
        AssertEx.Equal(StoredNodeSettings.MinRetentionDays, settings.MinRetentionDays);
        AssertEx.Equal(StoredNodeSettings.MaxRetentionDays, settings.MaxAllowedRetentionDays);
        AssertEx.Equal(StoredNodeSettings.MaxNodeDbBackupRetainCount, settings.MaxAllowedNodeDbBackupRetainCount);
        AssertEx.Equal(StoredNodeSettings.MinBenchmarkKldCacheMaxBytes, settings.MinBenchmarkKldCacheMaxBytes);
        AssertEx.Equal(StoredNodeSettings.MaxBenchmarkKldCacheMaxBytes, settings.MaxAllowedBenchmarkKldCacheMaxBytes);
    }

    [Test]
    [Arguments("chatRetentionDays")]
    [Arguments("benchmarkKldCacheMaxBytes")]
    public async Task SaveNodeSettings_WhenAUsageKnobIsOutOfRange_BindsTheErrorToTheWireField(string field)
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(field == "chatRetentionDays"
            ? new SaveNodeSettingsRequest
            {
                ChatRetentionDays = StoredNodeSettings.MinRetentionDays - 1
            }
            : new SaveNodeSettingsRequest
            {
                BenchmarkKldCacheMaxBytes = StoredNodeSettings.MaxBenchmarkKldCacheMaxBytes + 1
            });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal(field, document.RootElement.GetProperty("errors")[0].GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WithTheRuntimeAndWorkspaceKnobs_RoundTripsThroughGetWithTheirBounds()
    {
        var saved = new StoredNodeSettings();
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => saved);
        nodeSettingsStore.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
                         .Returns(call =>
                         {
                             saved = call.Arg<Func<StoredNodeSettings, StoredNodeSettings>>()(saved);
                             return Task.FromResult(saved);
                         });

        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var putRequest = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        putRequest.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            ImageMaxLoadedProcesses = 2,
            ImageTextEncoderOnGpu = true,
            GraphWorkflowMaxConcurrentRuns = 8,
            GraphWorkflowDefaultNodeTimeoutSeconds = 900,
            WorkSessionMaxStepsPerRun = 50,
            WorkSessionMaxConcurrentSessions = 2,
            DevelopmentMaxAttemptDurationSeconds = 3600,
            DevelopmentMaxToolCalls = 128,
            DevelopmentMaxOutputTokens = 65536,
            AgentHomeMaxInnerToolCalls = 12,
            AgentHomePatchApplyTimeoutSeconds = 300,
            AgentHomeRunRetentionMaxRuns = 0,
            AgentHomeRunRetentionMaxTotalBytes = 4L * 1024 * 1024 * 1024
        });
        using var putResponse = await client.SendAsync(putRequest);
        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        using var getRequest = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var getResponse = await client.SendAsync(getRequest);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(getResponse);

        AssertEx.Equal(expected: 2, settings.ImageMaxLoadedProcesses);
        AssertEx.Equal(expected: true, settings.ImageTextEncoderOnGpu);
        AssertEx.Equal(expected: 8, settings.GraphWorkflowMaxConcurrentRuns);
        AssertEx.Equal(expected: 900, settings.GraphWorkflowDefaultNodeTimeoutSeconds);
        AssertEx.Equal(expected: 50, settings.WorkSessionMaxStepsPerRun);
        AssertEx.Equal(expected: 2, settings.WorkSessionMaxConcurrentSessions);
        AssertEx.Equal(expected: 3600, settings.DevelopmentMaxAttemptDurationSeconds);
        AssertEx.Equal(expected: 128, settings.DevelopmentMaxToolCalls);
        AssertEx.Equal(expected: 65536, settings.DevelopmentMaxOutputTokens);
        AssertEx.Equal(expected: 12, settings.AgentHomeMaxInnerToolCalls);
        AssertEx.Equal(expected: 300, settings.AgentHomePatchApplyTimeoutSeconds);
        AssertEx.Equal(expected: 0, settings.AgentHomeRunRetentionMaxRuns, "0 turns the count limit off and is a valid value.");
        AssertEx.Equal(4L * 1024 * 1024 * 1024, settings.AgentHomeRunRetentionMaxTotalBytes);
        // Bounds are server-authoritative for the React form.
        AssertEx.Equal(StoredNodeSettings.MaxImageMaxLoadedProcesses, settings.MaxAllowedImageMaxLoadedProcesses);
        AssertEx.Equal(StoredNodeSettings.MinGraphWorkflowDefaultNodeTimeoutSeconds, settings.MinGraphWorkflowDefaultNodeTimeoutSeconds);
        AssertEx.Equal(StoredNodeSettings.MaxWorkSessionMaxConcurrentSessions, settings.MaxAllowedWorkSessionMaxConcurrentSessions);
        AssertEx.Equal(StoredNodeSettings.MinDevelopmentMaxOutputTokens, settings.MinDevelopmentMaxOutputTokens);
        AssertEx.Equal(StoredNodeSettings.MaxAgentHomeMaxInnerToolCalls, settings.MaxAllowedAgentHomeMaxInnerToolCalls);
        AssertEx.Equal(StoredNodeSettings.MinAgentHomeRunRetentionMaxRuns, settings.MinAgentHomeRunRetentionMaxRuns);
        AssertEx.Equal(StoredNodeSettings.MaxAgentHomeRunRetentionMaxTotalBytes, settings.MaxAllowedAgentHomeRunRetentionMaxTotalBytes);
    }

    [Test]
    [Arguments("imageMaxLoadedProcesses")]
    [Arguments("agentHomeRunRetentionMaxTotalBytes")]
    public async Task SaveNodeSettings_WhenARuntimeKnobIsOutOfRange_BindsTheErrorToTheWireField(string field)
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(field == "imageMaxLoadedProcesses"
            ? new SaveNodeSettingsRequest
            {
                ImageMaxLoadedProcesses = StoredNodeSettings.MaxImageMaxLoadedProcesses + 1
            }
            : new SaveNodeSettingsRequest
            {
                AgentHomeRunRetentionMaxTotalBytes = StoredNodeSettings.MinAgentHomeRunRetentionMaxTotalBytes - 1
            });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal(field, document.RootElement.GetProperty("errors")[0].GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenABackgroundModelIsNotNodeLocal_BindsTheErrorToTheField()
    {
        // An `ext:` id would carry golden text off the node; the rejection must name the request property so the
        // React select can show it under the right picker.
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings());
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            PlaybookEvalModelName = "ext:studio/qwen3-8b"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal("playbookEvalModelName", document.RootElement.GetProperty("errors")[0].GetProperty("name").GetString());
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void NodeSettings_BackgroundModelNames_RoundTripThroughMapper()
    {
        // A trimmed value round-trips, omitting the field keeps the stored value, and the "Inherit" option sends an
        // empty string that clears it (Normalize then stores null).
        var stored = new SaveNodeSettingsRequest
        {
            PlaybookAnalysisModelName = "  qwen3:8b  ",
            PlaybookEvalModelName = "repo/model:Q4_K_M",
            MemoryExtractionModelName = "qwen3:1.7b"
        }.ToStoredSettings(new StoredNodeSettings());
        AssertEx.Equal("qwen3:8b", stored.PlaybookAnalysisModelName);

        var response = stored.ToResponse(DefaultEffectiveValues);
        AssertEx.Equal("qwen3:8b", response.PlaybookAnalysisModelName);
        AssertEx.Equal("repo/model:Q4_K_M", response.PlaybookEvalModelName);
        AssertEx.Equal("qwen3:1.7b", response.MemoryExtractionModelName);

        var merged = new SaveNodeSettingsRequest().ToStoredSettings(stored);
        AssertEx.Equal("repo/model:Q4_K_M", merged.PlaybookEvalModelName);

        var cleared = new SaveNodeSettingsRequest
        {
            MemoryExtractionModelName = string.Empty
        }.ToStoredSettings(stored);
        AssertEx.Equal(string.Empty, cleared.MemoryExtractionModelName);
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

        var response = stored.ToResponse(DefaultEffectiveValues);
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
    public void NodeSettings_WebAccessFields_RoundTripKeepAndClear()
    {
        var stored = new SaveNodeSettingsRequest
        {
            WebAccessEnabled = true,
            WebSearchSearxngUrl = "  https://searx.example.org/  "
        }.ToStoredSettings(new StoredNodeSettings());

        AssertEx.Equal(expected: true, stored.WebAccessEnabled);
        AssertEx.Equal("https://searx.example.org/", stored.WebSearchSearxngUrl);
        var response = stored.ToResponse(DefaultEffectiveValues);
        AssertEx.Equal(expected: true, response.WebAccessEnabled);
        AssertEx.Equal("https://searx.example.org/", response.WebSearchSearxngUrl);

        // Omitted on a later save: both kept. ToStoredSettings builds a FRESH record, so an omission here would erase them.
        var merged = new SaveNodeSettingsRequest().ToStoredSettings(stored);
        AssertEx.Equal(expected: true, merged.WebAccessEnabled);
        AssertEx.Equal("https://searx.example.org/", merged.WebSearchSearxngUrl);

        // An empty string clears the URL (Normalize maps blank to null), and false turns web access off.
        var cleared = new SaveNodeSettingsRequest
        {
            WebAccessEnabled = false,
            WebSearchSearxngUrl = string.Empty
        }.ToStoredSettings(stored);
        AssertEx.Equal(expected: false, cleared.WebAccessEnabled);
        AssertEx.Equal(string.Empty, cleared.WebSearchSearxngUrl);
    }

    [Test]
    [Arguments("not a url", false)]
    [Arguments("ftp://searx.example.org", false)]
    [Arguments("https://searx.example.org", true)]
    [Arguments("http://127.0.0.1:8888", true)]
    [Arguments("", true)]
    public void SaveNodeSettings_SearxngUrlValidation(string url, bool expectedValid)
    {
        var result = new SaveNodeSettingsRequestValidator().Validate(new SaveNodeSettingsRequest
        {
            WebSearchSearxngUrl = url
        });

        AssertEx.Equal(expectedValid, result.IsValid);
    }

    /// <summary>The mapper trims both choices before storing them, so the validator must judge the trimmed value too.</summary>
    [Test]
    public void SaveNodeSettings_PaddedReasoningEffortAndOutputCapMode_ValidateAndStoreTrimmed()
    {
        var request = new SaveNodeSettingsRequest
        {
            DefaultReasoningEffort = " low ",
            ChatOutputCapMode = " notice "
        };

        var result = new SaveNodeSettingsRequestValidator().Validate(request);
        var stored = request.ToStoredSettings(new StoredNodeSettings());

        AssertEx.True(result.IsValid, string.Join("; ", result.Errors.Select(static error => error.ErrorMessage)));
        AssertEx.Equal("low", stored.DefaultReasoningEffort);
        AssertEx.Equal("notice", stored.ChatOutputCapMode);
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

    [Test]
    public void NodeSettings_ContainerRuntimeSelection_RoundTripsThroughMapper()
    {
        // The mapper is the meeting point: the wire and the store share ONE spelling (the parser's), so a supplied
        // value is normalized once, an omitted field keeps what is stored, and an unparseable stored value reads back
        // as the default instead of handing the SPA a string nothing can parse.
        var stored = new SaveNodeSettingsRequest
        {
            ContainerRuntimeSelection = "DOCKER"
        }.ToStoredSettings(new StoredNodeSettings());
        AssertEx.Equal(ContainerRuntimeSelectionParser.Docker, stored.ContainerRuntimeSelection);

        AssertEx.Equal(ContainerRuntimeSelectionParser.Docker, stored.ToResponse(DefaultEffectiveValues).ContainerRuntimeSelection);

        var merged = new SaveNodeSettingsRequest().ToStoredSettings(stored);
        AssertEx.Equal(ContainerRuntimeSelectionParser.Docker, merged.ContainerRuntimeSelection);

        var junk = new StoredNodeSettings
        {
            ContainerRuntimeSelection = "podman"
        }.ToResponse(DefaultEffectiveValues);
        AssertEx.Equal(ContainerRuntimeSelectionParser.Auto, junk.ContainerRuntimeSelection);

        AssertEx.Equal(ContainerRuntimeSelectionParser.Auto, new StoredNodeSettings().ToResponse(DefaultEffectiveValues).ContainerRuntimeSelection);
    }

    [Test]
    public void NodeSettings_ContainerRuntimeSelection_IsAStringOnBothDtos()
    {
        // The enum is kept OFF the wire on purpose: a string member cannot bind a JSON number, so an undefined
        // selection can never be persisted. A later "tidy-up" back to the enum type fails here.
        var responseMember = AssertEx.NotNull(typeof(NodeSettingsResponse).GetProperty(nameof(NodeSettingsResponse.ContainerRuntimeSelection)));
        var requestMember = AssertEx.NotNull(typeof(SaveNodeSettingsRequest).GetProperty(nameof(SaveNodeSettingsRequest.ContainerRuntimeSelection)));

        AssertEx.Equal(typeof(string), responseMember.PropertyType);
        AssertEx.Equal(typeof(string), requestMember.PropertyType);
    }

    [Test]
    public async Task GetNodeSettings_ReportsTheStoredContainerRuntimeSelection()
    {
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
        {
            ContainerRuntimeSelection = ContainerRuntimeSelectionParser.Docker
        });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var response = await client.SendAsync(request);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(ContainerRuntimeSelectionParser.Docker, settings.ContainerRuntimeSelection);
    }

    [Test]
    public async Task SaveNodeSettings_WhenContainerRuntimeSelectionIsMixedCase_PersistsTheCanonicalSpelling()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new
        {
            containerRuntimeSelection = "DOCKER"
        });
        using var response = await client.SendAsync(request);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(ContainerRuntimeSelectionParser.Docker, settings.ContainerRuntimeSelection);
        await nodeSettingsStore.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate).ContainerRuntimeSelection == ContainerRuntimeSelectionParser.Docker),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenContainerRuntimeSelectionIsUnknown_ReturnsValidationProblem()
    {
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new
        {
            containerRuntimeSelection = "Podman"
        });
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, "containerRuntimeSelection", StringComparison.Ordinal);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenContainerRuntimeSelectionIsANumber_IsRejectedByType()
    {
        // The whole reason the enum stayed off the wire: JsonStringEnumConverter rejects an unknown STRING but binds
        // any JSON integer, which would reach Format and persist a value nothing can parse back.
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new
        {
            containerRuntimeSelection = 7
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_AppliesTheRecommendedPreset_WritesAllFourMembers()
    {
        var settings = await SaveAsync(new StoredNodeSettings
            {
                ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileOffline,
                AutoCheckApplicationUpdates = false,
                AutoCheckRuntimeUpdates = false,
                AutoProvisionFirstRunModel = false
            },
            new SaveNodeSettingsRequest
            {
                ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileRecommended
            });

        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileRecommended, settings.ExternalAccessProfile);
        AssertEx.Equal(expected: true, settings.AutoCheckApplicationUpdates);
        AssertEx.Equal(expected: true, settings.AutoCheckRuntimeUpdates);
        AssertEx.Equal(expected: true, settings.AutoProvisionFirstRunModel);
    }

    [Test]
    public async Task SaveNodeSettings_AppliesTheOfflinePreset_WritesAllFourMembers()
    {
        var settings = await SaveAsync(new StoredNodeSettings(),
            new SaveNodeSettingsRequest
            {
                ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileOffline
            });

        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileOffline, settings.ExternalAccessProfile);
        AssertEx.Equal(expected: false, settings.AutoCheckApplicationUpdates);
        AssertEx.Equal(expected: false, settings.AutoCheckRuntimeUpdates);
        AssertEx.Equal(expected: false, settings.AutoProvisionFirstRunModel);
    }

    [Test]
    public async Task SaveNodeSettings_WhenAPresetArrivesWithContradictoryBooleans_ThePresetWins()
    {
        // One owner, one rule: a preset expands to its own triple and ignores anything sent beside it. Without this a
        // client could persist a profile that contradicts the switches it names.
        var settings = await SaveAsync(new StoredNodeSettings(),
            new SaveNodeSettingsRequest
            {
                ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileOffline,
                AutoCheckApplicationUpdates = true,
                AutoCheckRuntimeUpdates = true,
                AutoProvisionFirstRunModel = true
            });

        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileOffline, settings.ExternalAccessProfile);
        AssertEx.Equal(expected: false, settings.AutoCheckApplicationUpdates);
        AssertEx.Equal(expected: false, settings.AutoCheckRuntimeUpdates);
        AssertEx.Equal(expected: false, settings.AutoProvisionFirstRunModel);
    }

    [Test]
    public async Task SaveNodeSettings_WhenOneSwitchIsEditedAfterAPreset_SetsTheProfileToCustom()
    {
        var settings = await SaveAsync(new StoredNodeSettings
            {
                ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileRecommended,
                AutoCheckApplicationUpdates = true,
                AutoCheckRuntimeUpdates = true,
                AutoProvisionFirstRunModel = true
            },
            new SaveNodeSettingsRequest
            {
                AutoProvisionFirstRunModel = false
            });

        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileCustom, settings.ExternalAccessProfile);
        AssertEx.Equal(expected: true, settings.AutoCheckApplicationUpdates);
        AssertEx.Equal(expected: true, settings.AutoCheckRuntimeUpdates);
        AssertEx.Equal(expected: false, settings.AutoProvisionFirstRunModel);
    }

    [Test]
    public async Task SaveNodeSettings_WhenTheLastSwitchIsFlippedBackOn_StaysCustom()
    {
        // The stamp is UNCONDITIONAL, never re-derived from the resulting triple: a node at "custom" whose owner flips
        // the third switch back on stays "custom". Re-deriving would silently relabel it "recommended".
        var settings = await SaveAsync(new StoredNodeSettings
            {
                ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileCustom,
                AutoCheckApplicationUpdates = true,
                AutoCheckRuntimeUpdates = true,
                AutoProvisionFirstRunModel = false
            },
            new SaveNodeSettingsRequest
            {
                AutoProvisionFirstRunModel = true
            });

        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileCustom, settings.ExternalAccessProfile);
        AssertEx.Equal(expected: true, settings.AutoProvisionFirstRunModel);
    }

    [Test]
    public async Task SaveNodeSettings_WhenASaveTouchesNoExternalAccessMember_KeepsTheStoredProfile()
    {
        // The common case for every OTHER setting. An unrelated save must not disturb a decided node — the one way this
        // mapper could silently break the whole feature.
        var settings = await SaveAsync(new StoredNodeSettings
            {
                ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileOffline,
                AutoCheckApplicationUpdates = false,
                AutoCheckRuntimeUpdates = false,
                AutoProvisionFirstRunModel = false
            },
            new SaveNodeSettingsRequest
            {
                MaxMessageRequestTimeoutSeconds = 600
            });

        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileOffline, settings.ExternalAccessProfile);
        AssertEx.Equal(expected: false, settings.AutoCheckApplicationUpdates);
        AssertEx.Equal(expected: false, settings.AutoCheckRuntimeUpdates);
        AssertEx.Equal(expected: false, settings.AutoProvisionFirstRunModel);
        AssertEx.Equal(expected: 600, settings.MaxMessageRequestTimeoutSeconds);
    }

    [Test]
    [Arguments(StoredNodeSettings.ExternalAccessProfilePending)]
    [Arguments(StoredNodeSettings.ExternalAccessProfileCustom)]
    [Arguments("airgapped")]
    public async Task SaveNodeSettings_WhenTheProfileIsNotAPreset_ReturnsBadRequestWithoutSaving(string profile)
    {
        // "pending" and "custom" are engine-written states, so a client must not be able to claim either; an unknown
        // literal is simply invalid. All three are rejected at the boundary, ahead of Normalize's defence in depth.
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
        {
            ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileOffline
        });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            ExternalAccessProfile = profile
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_RoundTripsAllFourExternalAccessMembers()
    {
        // Both halves: the response the client reads back AND the record handed to the store. A member dropped from
        // ToStoredSettings — which builds a FRESH record, so an omission ERASES the value — fails the second half.
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileOffline
        });
        using var response = await client.SendAsync(request);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileOffline, settings.ExternalAccessProfile);
        AssertEx.Equal(expected: false, settings.AutoCheckApplicationUpdates);
        AssertEx.Equal(expected: false, settings.AutoCheckRuntimeUpdates);
        AssertEx.Equal(expected: false, settings.AutoProvisionFirstRunModel);
        await nodeSettingsStore.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate).ExternalAccessProfile == StoredNodeSettings.ExternalAccessProfileOffline
                && Persisted(mutate).AutoCheckApplicationUpdates == false
                && Persisted(mutate).AutoCheckRuntimeUpdates == false
                && Persisted(mutate).AutoProvisionFirstRunModel == false),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(StoredNodeSettings.UiModeSimple)]
    [Arguments(StoredNodeSettings.UiModeAdvanced)]
    public async Task SaveNodeSettings_RoundTripsTheUiMode(string mode)
    {
        // Both halves: the response the client reads back AND the record handed to the store. ToStoredSettings builds a
        // FRESH record, so an omitted member ERASES the value — the second half is what catches that.
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            UiMode = mode
        });
        using var response = await client.SendAsync(request);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(mode, settings.UiMode);
        await nodeSettingsStore.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate).UiMode == mode),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("expert")]
    [Arguments("Simple")]
    [Arguments("")]
    public async Task SaveNodeSettings_WhenTheUiModeIsNotOneOfTheTwoLiterals_ReturnsBadRequestWithoutSaving(string mode)
    {
        // The boundary is the trust boundary: the SPA can only produce the two literals, so anything else is a
        // hand-written client and is rejected ahead of Normalize's defence in depth. An empty string is rejected
        // rather than read as "keep the current value" — only an ABSENT member keeps.
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
        {
            UiMode = StoredNodeSettings.UiModeSimple
        });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            UiMode = mode
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenASaveOmitsTheUiMode_KeepsTheStoredOne()
    {
        // The common case for every other setting: an unrelated save must not drop an operator's navigation choice.
        var settings = await SaveAsync(new StoredNodeSettings
            {
                UiMode = StoredNodeSettings.UiModeSimple
            },
            new SaveNodeSettingsRequest
            {
                MaxMessageRequestTimeoutSeconds = 600
            });

        AssertEx.Equal(StoredNodeSettings.UiModeSimple, settings.UiMode);
        AssertEx.Equal(expected: 600, settings.MaxMessageRequestTimeoutSeconds);
    }

    [Test]
    [Arguments(AppUpdateChannelNames.Stable)]
    [Arguments(AppUpdateChannelNames.Preview)]
    [Arguments(AppUpdateChannelNames.Development)]
    public async Task SaveNodeSettings_WithAnUpdateChannel_PersistsAndReturnsIt(string channel)
    {
        // Both halves: the response the client reads back AND the record handed to the store, because
        // ToStoredSettings builds a FRESH record and an omitted member would erase the value.
        var nodeSettingsStore = NewSettingsStore();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            UpdateChannel = channel
        });
        using var response = await client.SendAsync(request);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(channel, settings.UpdateChannel);
        await nodeSettingsStore.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate).UpdateChannel == channel),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("nightly")]
    [Arguments("Development")]
    [Arguments("")]
    public async Task SaveNodeSettings_WithAnUnknownUpdateChannel_Returns400AndPersistsNothing(string channel)
    {
        // The stored record must be untouched, not merely the status code right: a rejected save that still wrote
        // would move the node onto another channel silently.
        var nodeSettingsStore = NewSettingsStore(new StoredNodeSettings
        {
            UpdateChannel = AppUpdateChannelNames.Stable
        });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            UpdateChannel = channel
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenASaveOmitsTheUpdateChannel_KeepsTheStoredOne()
    {
        var settings = await SaveAsync(new StoredNodeSettings
            {
                UpdateChannel = AppUpdateChannelNames.Development
            },
            new SaveNodeSettingsRequest
            {
                MaxMessageRequestTimeoutSeconds = 600
            });

        AssertEx.Equal(AppUpdateChannelNames.Development, settings.UpdateChannel);
        AssertEx.Equal(expected: 600, settings.MaxMessageRequestTimeoutSeconds);
    }

    [Test]
    public async Task SaveNodeSettings_WhenTheFileIsUnreadable_ReturnsAProblemResponseNamingTheRecovery()
    {
        // Driven against the REAL store over a hand-corrupted file, because the behaviour under test lives in the
        // store's strict read, and a substitute would only assert a double against a double. It also goes through the
        // real handler chain, since the mapping is a global IExceptionHandler arm rather than a per-endpoint catch and
        // nothing in the endpoint itself shows it.
        var root = Path.Combine(Path.GetTempPath(), "xe-node-settings-unreadable", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "node-settings.json");
        try
        {
            await File.WriteAllTextAsync(settingsPath, "{ \"externalAccessProfile\": \"offli");
            var before = await File.ReadAllBytesAsync(settingsPath);

            using var store = new NodeSettingsStore(new FakeNodeDataDirectory(root), NullLogger<NodeSettingsStore>.Instance);
            await using var factory = CreateFactory(store);
            using var client = factory.CreateClient();

            using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
            request.Content = JsonContent.Create(new SaveNodeSettingsRequest
            {
                MaxMessageRequestTimeoutSeconds = 600
            });
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            AssertEx.Contains(body, "generalErrors", StringComparison.Ordinal);
            AssertEx.Contains(body, "node-settings.json", StringComparison.Ordinal);
            AssertEx.Contains(body, "Repair or delete", StringComparison.Ordinal);
            var after = await File.ReadAllBytesAsync(settingsPath);
            AssertEx.True(before.SequenceEqual(after), "A refused save must leave the unreadable settings file byte-identical.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    ///     PUTs <paramref name="request" /> against a node holding <paramref name="stored" /> and returns the response,
    ///     which the endpoint renders from the record the store actually persisted.
    /// </summary>
    private static async Task<NodeSettingsResponse> SaveAsync(StoredNodeSettings stored, SaveNodeSettingsRequest request)
    {
        await using var factory = CreateFactory(NewSettingsStore(stored));
        using var client = factory.CreateClient();

        using var httpRequest = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        httpRequest.Content = JsonContent.Create(request);
        using var response = await client.SendAsync(httpRequest);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync<NodeSettingsResponse>(response);
    }

    private static TestServerWebAppFactory CreateFactory(INodeSettingsStore nodeSettingsStore,
        INodeRuntimeSettings? runtimeSettings = null,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        return new TestServerWebAppFactory
        {
            AdditionalConfiguration = configuration,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeSettingsStore>();
                services.AddSingleton(nodeSettingsStore);
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
        await using var stream = await response.Content.ReadAsStreamAsync();
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions));
    }

    /// <summary>
    ///     The record a save actually persists. The administration service writes through
    ///     <see cref="INodeSettingsStore.UpdateAsync" />, so what lands is its mutation applied to the settings the
    ///     store holds AT WRITE TIME — not to the record the request produced.
    /// </summary>
    private static StoredNodeSettings Persisted(Func<StoredNodeSettings, StoredNodeSettings> mutate, StoredNodeSettings? latest = null) =>
        mutate(latest ?? new StoredNodeSettings());

    /// <summary>
    ///     A substitute store holding <paramref name="current" />, wired to honour
    ///     <see cref="INodeSettingsStore.UpdateAsync" />'s contract: it runs the mutation against the record it holds
    ///     and RETURNS what it persisted, which is the record the save endpoint renders its response from.
    ///     NSubstitute's own auto-value for that call is a null record, which no real store may return.
    /// </summary>
    private static INodeSettingsStore NewSettingsStore(StoredNodeSettings? current = null)
    {
        var settings = current ?? new StoredNodeSettings();
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);
        store.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
             .Returns(call => Task.FromResult(call.Arg<Func<StoredNodeSettings, StoredNodeSettings>>()(settings)));
        return store;
    }
}
