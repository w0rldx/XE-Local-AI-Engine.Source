namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using OllamaSharp;
using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalModelEndpointTests
{
    // The resolver provider keys the details endpoint routes by (lowercase, matching the registered providers).
    private const string LlamaCppProviderName = LocalModelProviders.LlamaCpp;
    private const string OllamaProviderName = "ollama";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task ListLocalModels_WhenAvailable_ReturnsModelsAndSelection()
    {
        await using var context = await CreateContextAsync("llama3:8b").ConfigureAwait(false);
        context.SettingsStore.Settings = new StoredNodeSettings
        {
            DefaultModelName = "llama3:8b"
        };
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var models = await ReadJsonAsync<ListLocalModelsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(models.IsAvailable);
        AssertEx.Equal("llama3:8b", models.SelectedModelName);
        AssertEx.ContainsSingle(models.Items, item => item.ModelName == "llama3:8b");
        var model = models.Items.Single(item => item.ModelName == "llama3:8b");
        AssertEx.True(model.IsSelected);
        AssertEx.Equal("fake", model.Family);
        AssertEx.Equal("Q0_0", model.QuantizationLevel);
    }

    [Test]
    public async Task ListLocalModels_WhenAvailable_EnrichesItemsWithClassification()
    {
        await using var context = await CreateContextAsync("llama3:8b").ConfigureAwait(false);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var models = await ReadJsonAsync<ListLocalModelsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var model = models.Items.Single(item => item.ModelName == "llama3:8b");

        // FakeOllama /api/show reports ["completion","embedding","tools"], so the detector classifies the model as Chat.
        AssertEx.Equal("Chat", model.Kind);
        AssertEx.Equal("Chat", model.DetectedKind);
        AssertEx.False(model.IsOverridden);
        AssertEx.Contains(model.Capabilities, "completion");
        AssertEx.Contains(model.Capabilities, "embedding");
    }

    [Test]
    public async Task SetModelKind_WhenValid_PersistsOverrideAndReportsEffectiveKind()
    {
        await using var context = await CreateContextAsync("llama3:8b").ConfigureAwait(false);
        using var client = context.Factory.CreateClient();

        using var putRequest = CreateRequest(context.Factory, HttpMethod.Put, "/api/local/v1/models/llama3:8b/kind");
        putRequest.Content = JsonContent.Create(new SetModelKindRequest
        {
            Kind = "Embedding"
        });
        using var putResponse = await client.SendAsync(putRequest).ConfigureAwait(false);
        var put = await ReadJsonAsync<ModelKindResponse>(putResponse).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        AssertEx.Equal("llama3:8b", put.ModelName);
        AssertEx.Equal("Embedding", put.Kind);
        // Setting an override does not probe /api/show, so detection has not yet run.
        AssertEx.Equal("Unknown", put.DetectedKind);
        AssertEx.True(put.IsOverridden);

        // The override survives and surfaces through the list endpoint as the effective kind; the list call lazily detects,
        // so the detected kind now resolves to Chat while the override keeps the effective kind on Embedding.
        using var listRequest = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models");
        using var listResponse = await client.SendAsync(listRequest).ConfigureAwait(false);
        var models = await ReadJsonAsync<ListLocalModelsResponse>(listResponse).ConfigureAwait(false);
        var model = models.Items.Single(item => item.ModelName == "llama3:8b");
        AssertEx.Equal("Embedding", model.Kind);
        AssertEx.Equal("Chat", model.DetectedKind);
        AssertEx.True(model.IsOverridden);
    }

    [Test]
    public async Task ResetModelKind_WhenOverridden_ClearsOverrideAndRevertsToDetected()
    {
        await using var context = await CreateContextAsync("llama3:8b").ConfigureAwait(false);
        using var client = context.Factory.CreateClient();

        using var putRequest = CreateRequest(context.Factory, HttpMethod.Put, "/api/local/v1/models/llama3:8b/kind");
        putRequest.Content = JsonContent.Create(new SetModelKindRequest
        {
            Kind = "Embedding"
        });
        using var putResponse = await client.SendAsync(putRequest).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        using var deleteRequest = CreateRequest(context.Factory, HttpMethod.Delete, "/api/local/v1/models/llama3:8b/kind");
        using var deleteResponse = await client.SendAsync(deleteRequest).ConfigureAwait(false);
        var deleted = await ReadJsonAsync<ModelKindResponse>(deleteResponse).ConfigureAwait(false);

        // Reset clears the override but does NOT eagerly probe /api/show (the override-only row carries a null digest, so
        // probing now would cache a stale digest and force a redundant re-probe on the next list). Detection is deferred to
        // the next list, so the DELETE response reports Unknown with the override cleared.
        AssertEx.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        AssertEx.Equal("llama3:8b", deleted.ModelName);
        AssertEx.Equal("Unknown", deleted.Kind);
        AssertEx.Equal("Unknown", deleted.DetectedKind);
        AssertEx.False(deleted.IsOverridden);

        // The next list lazily detects with the real digest, so the effective kind reverts to the detected Chat.
        using var listRequest = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models");
        using var listResponse = await client.SendAsync(listRequest).ConfigureAwait(false);
        var models = await ReadJsonAsync<ListLocalModelsResponse>(listResponse).ConfigureAwait(false);
        var model = models.Items.Single(item => item.ModelName == "llama3:8b");
        AssertEx.Equal("Chat", model.Kind);
        AssertEx.Equal("Chat", model.DetectedKind);
        AssertEx.False(model.IsOverridden);
    }

    [Test]
    public async Task SetModelKind_WhenKindIsInvalid_ReturnsValidationProblem()
    {
        await using var context = await CreateContextAsync("llama3:8b").ConfigureAwait(false);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Put, "/api/local/v1/models/llama3:8b/kind");
        request.Content = JsonContent.Create(new SetModelKindRequest
        {
            Kind = "Banana"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task SetModelKind_WhenModelNameIsUnsafe_ReturnsValidationProblem()
    {
        var modelService = Substitute.For<IOllamaModelService>();
        await using var context = CreateContext(modelService, new StubNodeSettingsStore(new StoredNodeSettings()));
        using var client = context.Factory.CreateClient();

        // %2E%2E decodes to ".." in the bound route value, which ModelNameValidator rejects as path traversal.
        using var request = CreateRequest(context.Factory, HttpMethod.Put, "/api/local/v1/models/%2E%2Esecret/kind");
        request.Content = JsonContent.Create(new SetModelKindRequest
        {
            Kind = "Chat"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await modelService.DidNotReceiveWithAnyArgs().ShowModelDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ListLocalModels_WhenProviderUnavailable_ReturnsSafeUnavailableResponse()
    {
        var modelService = Substitute.For<IOllamaModelService>();
        modelService.ListLocalModelsAsync(Arg.Any<CancellationToken>()).Returns<Task<IEnumerable<Model>>>(_ => throw new InvalidOperationException("provider offline"));
        await using var context = CreateContext(modelService, new StubNodeSettingsStore(new StoredNodeSettings()));
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var models = await ReadJsonAsync<ListLocalModelsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(models.IsAvailable);
        AssertEx.Empty(models.Items);
        AssertEx.Equal("Local model provider is unavailable.", models.Error);
    }

    [Test]
    public async Task GetLocalModelDetails_WhenOllamaModel_ProbesOllamaForSecretSafeDetails()
    {
        // The test host stubs the provider resolver to route every model to "ollama" (StubNoCodexSession), so this
        // exercises the Ollama /api/show branch. The real resolver's default is now "llamacpp"; the GGUF test below
        // overrides the resolver to assert the no-Ollama GGUF branch.
        await using var context = await CreateContextAsync("llama3:8b").ConfigureAwait(false);
        context.Server!.State.ModelInfo["llama3:8b"] = new Dictionary<string, object?>
        {
            ["llama.context_length"] = 8192
        };
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models/llama3:8b/details");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var details = Deserialize<LocalModelDetailsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("llama3:8b", details.ModelName);
        AssertEx.Equal(expected: 8192, details.MaxContextTokens);
        AssertEx.Equal("{{ .Prompt }}", details.Template);
        AssertEx.False(body.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
        AssertEx.Contains(context.Server.RecordedRequests, recorded => recorded.Path == "/api/show" && recorded.ModelName == "llama3:8b");
    }

    [Test]
    public async Task GetLocalModelDetails_WhenCodexCloudModel_Returns404_AndNeverProbesLocalRuntime()
    {
        // A Codex cloud id has no LOCAL details — the endpoint must short-circuit to 404 instead of probing Ollama
        // /api/show (which 500s because the local runtime has no such model). Regression for the details-500 noise.
        await using var context = await CreateContextAsync("llama3:8b").ConfigureAwait(false);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models/gpt-5.5/details");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var probedLocalRuntime = context.Server!.RecordedRequests
                                        .Any(recorded => recorded.Path == "/api/show" && recorded.ModelName == "gpt-5.5");
        AssertEx.False(probedLocalRuntime, "a Codex cloud id must not probe the local Ollama /api/show endpoint");
    }

    [Test]
    public async Task SelectLocalModel_WhenValid_PersistsDefaultModelWithoutProviderSwitching()
    {
        var modelService = Substitute.For<IOllamaModelService>();
        var settingsStore = new StubNodeSettingsStore(new StoredNodeSettings
        {
            MaxMessageRequestTimeoutSeconds = 120
        });
        await using var context = CreateContext(modelService, settingsStore);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Post, "/api/local/v1/models/select");
        request.Content = JsonContent.Create(new SelectLocalModelRequest
        {
            ModelName = "llama3:8b"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var selection = await ReadJsonAsync<SelectLocalModelResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("llama3:8b", selection.SelectedModelName);
        AssertEx.Equal("llama3:8b", settingsStore.Settings.DefaultModelName);
        AssertEx.Equal(expected: 120, settingsStore.Settings.MaxMessageRequestTimeoutSeconds);
        await modelService.DidNotReceiveWithAnyArgs().ListLocalModelsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteLocalModel_WhenValid_UsesJournaledGgufCoordinator()
    {
        // Local models are GGUF files served by the bundled llama.cpp runtime (Ollama is no longer a runtime), so the
        // delete endpoint routes to IGgufModelStore.DeleteModelAsync — assert the GGUF store is the deletion path.
        var modelService = Substitute.For<IOllamaModelService>();
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        var deletionCoordinator = DeletionCoordinator("orca-mini:latest");
        await using var context = CreateContext(modelService,
            new StubNodeSettingsStore(new StoredNodeSettings()),
            LlamaCppProviderName,
            ggufModelStore,
            deletionCoordinator);
        using var client = context.Factory.CreateClient();

        using var deleteRequest = CreateRequest(context.Factory, HttpMethod.Delete, "/api/local/v1/models/orca-mini:latest");
        using var deleteResponse = await client.SendAsync(deleteRequest).ConfigureAwait(false);
        var deleted = await ReadJsonAsync<DeleteLocalModelResponse>(deleteResponse).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        AssertEx.Equal("orca-mini:latest", deleted.ModelName);
        AssertEx.True(deleted.Deleted);
        await deletionCoordinator.Received(1).CommitDeleteAsync("orca-mini:latest", Arg.Any<CancellationToken>());
        await deletionCoordinator.Received(1).PurgeAfterSuccessAsync(Arg.Any<CommittedModelDeletion>(), CancellationToken.None);
        await ggufModelStore.DidNotReceiveWithAnyArgs().DeleteModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await modelService.DidNotReceiveWithAnyArgs().DeleteModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteLocalModel_WhenNameHasEncodedSlashes_DecodesBeforeDeleting()
    {
        // The hey-api client escapes the route segment with encodeURIComponent, and Kestrel leaves %2F encoded by design,
        // so the bound name arrives as "hf.co%2F...". The endpoint must decode it and delete the canonical HF reference.
        var modelService = Substitute.For<IOllamaModelService>();
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        var deletionCoordinator = DeletionCoordinator("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL");
        await using var context = CreateContext(modelService,
            new StubNodeSettingsStore(new StoredNodeSettings()),
            LlamaCppProviderName,
            ggufModelStore,
            deletionCoordinator);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory,
            HttpMethod.Delete,
            "/api/local/v1/models/hf.co%2Funsloth%2Fgemma-4-12b-it-GGUF%3AUD-Q4_K_XL");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var deleted = await ReadJsonAsync<DeleteLocalModelResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", deleted.ModelName);
        AssertEx.True(deleted.Deleted);
        await deletionCoordinator.Received(1)
                                 .CommitDeleteAsync("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteLocalModel_WhenNameIsPlainTag_DeletesUnchanged()
    {
        var modelService = Substitute.For<IOllamaModelService>();
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        var deletionCoordinator = DeletionCoordinator("llama3:8b");
        await using var context = CreateContext(modelService,
            new StubNodeSettingsStore(new StoredNodeSettings()),
            LlamaCppProviderName,
            ggufModelStore,
            deletionCoordinator);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Delete, "/api/local/v1/models/llama3:8b");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var deleted = await ReadJsonAsync<DeleteLocalModelResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("llama3:8b", deleted.ModelName);
        await deletionCoordinator.Received(1).CommitDeleteAsync("llama3:8b", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteLocalModel_WhenEncodedSlashHidesTraversal_ReturnsValidationProblem()
    {
        // ..%2F..%2Fetc decodes to "../../etc", which the validator's ".." guard rejects AFTER decoding — so decoding
        // cannot smuggle path traversal past the guard.
        var modelService = Substitute.For<IOllamaModelService>();
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        var deletionCoordinator = DeletionCoordinator("unused");
        await using var context = CreateContext(modelService,
            new StubNodeSettingsStore(new StoredNodeSettings()),
            LlamaCppProviderName,
            ggufModelStore,
            deletionCoordinator);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Delete, "/api/local/v1/models/hf.co%2F..%2F..%2Fetc");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await deletionCoordinator.DidNotReceiveWithAnyArgs().CommitDeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await ggufModelStore.DidNotReceiveWithAnyArgs().DeleteModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteLocalModel_WhenNonGgufProvider_PreservesProviderDeletionPath()
    {
        var modelService = Substitute.For<IOllamaModelService>();
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        var deletionCoordinator = DeletionCoordinator("other:model");
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(OllamaProviderName);
        await using var context = CreateContext(modelService,
            new StubNodeSettingsStore(new StoredNodeSettings()),
            OllamaProviderName,
            ggufModelStore,
            deletionCoordinator,
            provider);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Delete, "/api/local/v1/models/other:model");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await provider.Received(1).DeleteModelAsync("other:model", Arg.Any<CancellationToken>());
        await deletionCoordinator.DidNotReceiveWithAnyArgs().CommitDeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetLocalModelDetails_WhenNameHasEncodedSlashes_DecodesBeforeProbing()
    {
        // Ollama branch (default-stubbed resolver): the decoded canonical name is the one probed via /api/show.
        var modelService = Substitute.For<IOllamaModelService>();
        modelService.ShowModelDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(new OllamaModelDetails(new ShowModelResponse(), MaxContextTokens: 4096, []));
        await using var context = CreateContext(modelService, new StubNodeSettingsStore(new StoredNodeSettings()));
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory,
            HttpMethod.Get,
            "/api/local/v1/models/hf.co%2Funsloth%2Fgemma-4-12b-it-GGUF%3AUD-Q4_K_XL/details");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var details = await ReadJsonAsync<LocalModelDetailsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", details.ModelName);
        await modelService.Received(1)
                          .ShowModelDetailsAsync("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetLocalModelDetails_WhenGgufModel_ReturnsGgufMetadata_WithoutProbingOllama()
    {
        // A llamacpp-routed model's details come from the installed-GGUF registry, NOT Ollama /api/show. Assert the
        // Ollama model service is never touched (desktop mode has no Ollama daemon) and the descriptor's context
        // length surfaces as MaxContextTokens on the shared details response.
        var modelService = Substitute.For<IOllamaModelService>();
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        ggufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                      .Returns(_ =>
                      [
                          new LocalModelDescriptor
                          {
                              ModelName = "Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M",
                              ProviderName = LlamaCppProviderName,
                              IsAvailable = true,
                              SizeBytes = 491_000_000,
                              ModifiedAt = DateTimeOffset.UnixEpoch,
                              MaxContextTokens = 32768,
                              Origin = LocalModelOrigin.Imported,
                              ModelContentFingerprint = "v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                          }
                      ]);
        await using var context = CreateContext(modelService, new StubNodeSettingsStore(new StoredNodeSettings()), LlamaCppProviderName, ggufModelStore);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M/details");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var details = await ReadJsonAsync<LocalModelDetailsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M", details.ModelName);
        AssertEx.Equal(expected: 32768, details.MaxContextTokens);
        AssertEx.Equal(LocalModelOrigin.Imported, details.Origin);
        AssertEx.Equal("v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            details.ModelContentFingerprint);
        await modelService.DidNotReceiveWithAnyArgs().ShowModelDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetLocalModelDetails_WhenGgufModelNotInstalled_Returns404_WithoutProbingOllama()
    {
        // A name that routes to llamacpp but has no installed descriptor (stale map row / removed file) has no details:
        // a clean 404 that still never touches Ollama.
        var modelService = Substitute.For<IOllamaModelService>();
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        ggufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                      .Returns(_ => []);
        await using var context = CreateContext(modelService, new StubNodeSettingsStore(new StoredNodeSettings()), LlamaCppProviderName, ggufModelStore);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models/ghost-gguf:Q4_K_M/details");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await modelService.DidNotReceiveWithAnyArgs().ShowModelDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetLocalModelDetails_WhenGgufNameHasEncodedSlashes_DecodesBeforeResolving()
    {
        // Decode must precede provider resolution AND the GGUF lookup: the resolver and the registry are both keyed by
        // the decoded canonical name, so an encoded HF reference resolves and matches the installed descriptor.
        var modelService = Substitute.For<IOllamaModelService>();
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        ggufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                      .Returns(_ =>
                      [
                          new LocalModelDescriptor
                          {
                              ModelName = "hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL",
                              ProviderName = LlamaCppProviderName,
                              IsAvailable = true,
                              SizeBytes = 1,
                              ModifiedAt = DateTimeOffset.UnixEpoch,
                              MaxContextTokens = 8192
                          }
                      ]);
        await using var context = CreateContext(modelService, new StubNodeSettingsStore(new StoredNodeSettings()), LlamaCppProviderName, ggufModelStore);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory,
            HttpMethod.Get,
            "/api/local/v1/models/hf.co%2Funsloth%2Fgemma-4-12b-it-GGUF%3AUD-Q4_K_XL/details");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var details = await ReadJsonAsync<LocalModelDetailsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", details.ModelName);
        AssertEx.Equal(expected: 8192, details.MaxContextTokens);
        await modelService.DidNotReceiveWithAnyArgs().ShowModelDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetModelKind_WhenNameHasEncodedSlashes_DecodesBeforeStoring()
    {
        var classificationService = Substitute.For<IModelClassificationService>();
        classificationService.SetOverrideAsync(Arg.Any<string>(), Arg.Any<ModelKind>(), Arg.Any<CancellationToken>())
                             .Returns(new ModelClassificationResult("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL",
                                 ModelKind.Chat,
                                 ModelKind.Unknown,
                                 [],
                                 IsOverridden: true));
        await using var context = CreateContextWithClassification(classificationService);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory,
            HttpMethod.Put,
            "/api/local/v1/models/hf.co%2Funsloth%2Fgemma-4-12b-it-GGUF%3AUD-Q4_K_XL/kind");
        request.Content = JsonContent.Create(new SetModelKindRequest
        {
            Kind = "Chat"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await classificationService.Received(1)
                                   .SetOverrideAsync("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", ModelKind.Chat, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResetModelKind_WhenNameHasEncodedSlashes_DecodesBeforeResetting()
    {
        var classificationService = Substitute.For<IModelClassificationService>();
        classificationService.ResetOverrideAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                             .Returns(new ModelClassificationResult("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL",
                                 ModelKind.Unknown,
                                 ModelKind.Unknown,
                                 [],
                                 IsOverridden: false));
        await using var context = CreateContextWithClassification(classificationService);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory,
            HttpMethod.Delete,
            "/api/local/v1/models/hf.co%2Funsloth%2Fgemma-4-12b-it-GGUF%3AUD-Q4_K_XL/kind");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await classificationService.Received(1)
                                   .ResetOverrideAsync("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", Arg.Any<CancellationToken>());
    }

    private static async Task<LocalModelEndpointTestContext> CreateContextAsync(params string[] models)
    {
        var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = models.Length > 0 ? models : ["chat"]
        }, CancellationToken.None).ConfigureAwait(false);
        try
        {
            return new LocalModelEndpointTestContext(server);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static LocalModelEndpointTestContext CreateContext(IOllamaModelService modelService, StubNodeSettingsStore settingsStore)
    {
        return new LocalModelEndpointTestContext(modelService, settingsStore);
    }

    // Overload for the GGUF (llamacpp) details branch: routes every model to the given provider and supplies the
    // installed-GGUF store the branch reads. Used to assert the no-Ollama GGUF path.
    private static LocalModelEndpointTestContext CreateContext(IOllamaModelService modelService,
        StubNodeSettingsStore settingsStore,
        string providerName,
        IGgufModelStore ggufModelStore,
        ILocalModelDeletionCoordinator? deletionCoordinator = null,
        ILocalModelProvider? localProvider = null)
    {
        return new LocalModelEndpointTestContext(modelService, settingsStore, providerName, ggufModelStore, deletionCoordinator, localProvider);
    }

    private static ILocalModelDeletionCoordinator DeletionCoordinator(string modelName)
    {
        var coordinator = Substitute.For<ILocalModelDeletionCoordinator>();
        var receipt = new GgufDeletionStageReceipt(Guid.NewGuid(),
            modelName,
            Array.Empty<InstalledModelRegistryAliasSnapshot>(),
            Array.Empty<InstalledModelPhysicalMember>(),
            Array.Empty<GgufDeletionStagedMember>(),
            GgufRegistryAliasSetHash.ComputeV1([]),
            GgufPhysicalMemberSetHash.ComputeV1([]));
        coordinator.CommitDeleteAsync(modelName, Arg.Any<CancellationToken>())
                   .Returns(new CommittedModelDeletion(receipt.OperationId, modelName, [modelName], receipt));
        return coordinator;
    }

    private static LocalModelEndpointTestContext CreateContextWithClassification(IModelClassificationService classificationService)
    {
        return new LocalModelEndpointTestContext(classificationService);
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static T Deserialize<T>(string json)
        where T : class
    {
        return AssertEx.NotNull(JsonSerializer.Deserialize<T>(json, JsonOptions));
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false));
    }

    private sealed class StubNodeSettingsStore(StoredNodeSettings settings) : INodeSettingsStore
    {
        public StoredNodeSettings Settings { get; set; } = settings;

        public Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Settings);
        }

        public StoredNodeSettings Load(CancellationToken cancellationToken = default)
        {
            return Settings;
        }

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class LocalModelEndpointTestContext : IAsyncDisposable
    {
        private readonly OllamaApiClient? _ollamaClient;
        private readonly OllamaModelService? _ownedModelService;

        public LocalModelEndpointTestContext(FakeOllamaServer server)
        {
            Server = server ?? throw new ArgumentNullException(nameof(server));
            _ollamaClient = new OllamaApiClient(Server.BaseAddress);
            _ownedModelService = new OllamaModelService(_ollamaClient);
            SettingsStore = new StubNodeSettingsStore(new StoredNodeSettings());
            Factory = CreateFactory(_ownedModelService, SettingsStore);
        }

        public LocalModelEndpointTestContext(IOllamaModelService modelService, StubNodeSettingsStore settingsStore)
        {
            SettingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            Factory = CreateFactory(modelService ?? throw new ArgumentNullException(nameof(modelService)), SettingsStore);
        }

        public LocalModelEndpointTestContext(IOllamaModelService modelService,
            StubNodeSettingsStore settingsStore,
            string providerName,
            IGgufModelStore ggufModelStore,
            ILocalModelDeletionCoordinator? deletionCoordinator,
            ILocalModelProvider? localProvider)
        {
            SettingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            ArgumentNullException.ThrowIfNull(ggufModelStore);
            Factory = CreateFactory(modelService ?? throw new ArgumentNullException(nameof(modelService)),
                SettingsStore,
                providerName,
                ggufModelStore,
                deletionCoordinator,
                localProvider);
        }

        public LocalModelEndpointTestContext(IModelClassificationService classificationService)
        {
            ArgumentNullException.ThrowIfNull(classificationService);
            SettingsStore = new StubNodeSettingsStore(new StoredNodeSettings());
            Factory = CreateFactory(classificationService, SettingsStore);
        }

        public TestingWebAppFactory Factory { get; }

        public StubNodeSettingsStore SettingsStore { get; }

        public FakeOllamaServer? Server { get; }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync().ConfigureAwait(false);

            _ownedModelService?.Dispose();
            _ollamaClient?.Dispose();

            if (Server is not null)
            {
                await Server.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static TestingWebAppFactory CreateFactory(IOllamaModelService modelService, StubNodeSettingsStore settingsStore)
        {
            return new TestingWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<IOllamaModelService>();
                    services.AddSingleton(modelService);
                    services.RemoveAll<INodeSettingsStore>();
                    services.AddSingleton<INodeSettingsStore>(settingsStore);
                    StubNoCodexSession(services);
                }
            };
        }

        private static TestingWebAppFactory CreateFactory(IOllamaModelService modelService,
            StubNodeSettingsStore settingsStore,
            string providerName,
            IGgufModelStore ggufModelStore,
            ILocalModelDeletionCoordinator? deletionCoordinator,
            ILocalModelProvider? localProvider)
        {
            return new TestingWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<IOllamaModelService>();
                    services.AddSingleton(modelService);
                    services.RemoveAll<INodeSettingsStore>();
                    services.AddSingleton<INodeSettingsStore>(settingsStore);
                    services.RemoveAll<IGgufModelStore>();
                    services.AddSingleton(ggufModelStore);
                    if (deletionCoordinator is not null)
                    {
                        services.RemoveAll<ILocalModelDeletionCoordinator>();
                        services.AddSingleton(deletionCoordinator);
                    }

                    StubNoCodexSession(services);
                    // Override the default (ollama) resolver from StubNoCodexSession so this context routes the details
                    // endpoint to the requested provider (llamacpp for the GGUF branch).
                    StubProviderResolver(services, providerName, localProvider);
                }
            };
        }

        private static TestingWebAppFactory CreateFactory(IModelClassificationService classificationService, StubNodeSettingsStore settingsStore)
        {
            return new TestingWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<IModelClassificationService>();
                    services.AddSingleton(classificationService);
                    services.RemoveAll<INodeSettingsStore>();
                    services.AddSingleton<INodeSettingsStore>(settingsStore);
                    StubNoCodexSession(services);
                }
            };
        }

        // These tests cover the LOCAL model surface, so the Codex token store is stubbed to "no session" — otherwise an
        // ambient Codex session on the dev machine would add cloud models to the list and make the local assertions
        // (e.g. empty-when-unavailable) non-deterministic. The dedicated cloud-model mapping tests cover the cloud path.
        private static void StubNoCodexSession(IServiceCollection services)
        {
            var codexTokenStore = Substitute.For<ICodexTokenStore>();
            codexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
            services.RemoveAll<ICodexTokenStore>();
            services.AddSingleton(codexTokenStore);

            // Default the details endpoint's provider routing to "ollama" so the Ollama-branch detail tests probe
            // /api/show. The real resolver's default for an unmapped model is now "llamacpp" (which would route the
            // GGUF branch and skip Ollama) — the GGUF test overrides this resolver to assert that branch explicitly.
            StubProviderResolver(services, OllamaProviderName);
        }

        // Replaces the real ILocalModelProviderResolver with a substitute that routes EVERY model to a single provider
        // name. The details endpoint only calls ResolveProviderNameForModelAsync, so the other members stay
        // unimplemented (an NSubstitute default), keeping these endpoint tests deterministic and provider-stack-free.
        private static void StubProviderResolver(IServiceCollection services,
            string providerName,
            ILocalModelProvider? localProvider = null)
        {
            var resolver = Substitute.For<ILocalModelProviderResolver>();
            resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromResult(providerName));
            if (localProvider is not null)
            {
                resolver.ResolveProvider(providerName).Returns(localProvider);
            }

            services.RemoveAll<ILocalModelProviderResolver>();
            services.AddSingleton(resolver);
        }
    }
}
