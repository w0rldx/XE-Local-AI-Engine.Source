namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Common.ProblemDetailModels.Enums;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class LocalModelEndpointTests
{
    private const string ChatModel = "llama3:8b";
    private const string EncodedHfName = "hf.co%2Funsloth%2Fgemma-4-12b-it-GGUF%3AUD-Q4_K_XL";
    private const string HfName = "hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task ListLocalModels_ReturnsTheInstalledGgufAndTheSelection()
    {
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            DefaultModelName = ChatModel
        });
        await using var factory = CreateFactory(settingsStore, Installed(Gguf(ChatModel) with
        {
            Family = "fake",
            QuantizationLevel = "Q0_0"
        }));
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/models");
        using var response = await client.SendAsync(request);
        var models = await ReadJsonAsync<ListLocalModelsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(ChatModel, models.SelectedModelName);
        AssertEx.ContainsSingle(models.Items, item => item.ModelName == ChatModel);
        var model = models.Items.Single(item => item.ModelName == ChatModel);
        AssertEx.Equal(LocalModelProviders.LlamaCpp, model.Provider);
        AssertEx.True(model.IsSelected);
        AssertEx.Equal("fake", model.Family);
        AssertEx.Equal("Q0_0", model.QuantizationLevel);
    }

    [Test]
    public async Task ListLocalModels_EnrichesItemsWithTheOfflineKindAndTheDescriptorCapabilities()
    {
        await using var factory = CreateFactory(ggufModelStore: Installed(Gguf(ChatModel)));
        using var client = factory.CreateClient();

        var model = await ListSingleAsync(factory, client, ChatModel);

        AssertEx.Equal("Chat", model.Kind);
        AssertEx.Equal("Chat", model.DetectedKind);
        AssertEx.False(model.IsOverridden);
        AssertEx.Contains(model.Capabilities, "completion");
        AssertEx.Contains(model.Capabilities, "tools");
    }

    [Test]
    public async Task ListLocalModels_WhenNoGgufIsInstalled_AnswersOkAndEmpty()
    {
        // The picker keeps cloud and external entries enabled, and the quick-start tour reaches its download offer, only
        // if a node with nothing installed still answers the list successfully.
        await using var factory = CreateFactory(ggufModelStore: Installed());
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/models");
        using var response = await client.SendAsync(request);
        var models = await ReadJsonAsync<ListLocalModelsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Empty(models.Items);
    }

    [Test]
    public async Task ListLocalModels_WhenTheGgufStoreFails_DegradesToAnEmptyList()
    {
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        ggufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                      .Returns<Task<IReadOnlyList<LocalModelDescriptor>>>(_ => throw new IOException("registry unreadable"));
        await using var factory = CreateFactory(ggufModelStore: ggufModelStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/models");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var models = Deserialize<ListLocalModelsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Empty(models.Items);
        AssertEx.False(body.Contains("registry unreadable", StringComparison.Ordinal), "A store failure's text never reaches the client.");
    }

    [Test]
    public async Task SetModelKind_WhenValid_PersistsOverrideAndReportsEffectiveKind()
    {
        await using var factory = CreateFactory(ggufModelStore: Installed(Gguf(ChatModel)));
        using var client = factory.CreateClient();

        using var putRequest = CreateRequest(factory, HttpMethod.Put, $"/api/local/v1/models/{ChatModel}/kind");
        putRequest.Content = JsonContent.Create(new SetModelKindRequest
        {
            Kind = "Embedding"
        });
        using var putResponse = await client.SendAsync(putRequest);
        var put = await ReadJsonAsync<ModelKindResponse>(putResponse);

        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        AssertEx.Equal(ChatModel, put.ModelName);
        AssertEx.Equal("Embedding", put.Kind);
        AssertEx.Equal("Chat", put.DetectedKind);
        AssertEx.True(put.IsOverridden);

        // The override survives and surfaces through the list: the row agrees with the response it followed.
        var model = await ListSingleAsync(factory, client, ChatModel);
        AssertEx.Equal(LocalModelProviders.LlamaCpp, model.Provider);
        AssertEx.Equal("Embedding", model.Kind);
        AssertEx.Equal("Chat", model.DetectedKind);
        AssertEx.True(model.IsOverridden);
    }

    [Test]
    public async Task ResetModelKind_WhenOverridden_ClearsOverrideAndRevertsToDetected()
    {
        await using var factory = CreateFactory(ggufModelStore: Installed(Gguf(ChatModel)));
        using var client = factory.CreateClient();

        using var putRequest = CreateRequest(factory, HttpMethod.Put, $"/api/local/v1/models/{ChatModel}/kind");
        putRequest.Content = JsonContent.Create(new SetModelKindRequest
        {
            Kind = "Embedding"
        });
        using var putResponse = await client.SendAsync(putRequest);
        AssertEx.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        using var deleteRequest = CreateRequest(factory, HttpMethod.Delete, $"/api/local/v1/models/{ChatModel}/kind");
        using var deleteResponse = await client.SendAsync(deleteRequest);
        var deleted = await ReadJsonAsync<ModelKindResponse>(deleteResponse);

        AssertEx.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        AssertEx.Equal(ChatModel, deleted.ModelName);
        AssertEx.Equal("Chat", deleted.Kind);
        AssertEx.Equal("Chat", deleted.DetectedKind);
        AssertEx.False(deleted.IsOverridden);

        var model = await ListSingleAsync(factory, client, ChatModel);
        AssertEx.Equal("Chat", model.Kind);
        AssertEx.Equal("Chat", model.DetectedKind);
        AssertEx.False(model.IsOverridden);
    }

    [Test]
    public async Task SetModelKind_WhenKindIsInvalid_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, $"/api/local/v1/models/{ChatModel}/kind");
        request.Content = JsonContent.Create(new SetModelKindRequest
        {
            Kind = "Banana"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task SetModelKind_WhenModelNameIsUnsafe_ReturnsValidationProblem()
    {
        var classificationService = Substitute.For<IModelClassificationService>();
        await using var factory = CreateFactory(classificationService: classificationService);
        using var client = factory.CreateClient();

        // %2E%2E decodes to ".." in the bound route value, which ModelNameValidator rejects as path traversal.
        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/models/%2E%2Esecret/kind");
        request.Content = JsonContent.Create(new SetModelKindRequest
        {
            Kind = "Chat"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await classificationService.DidNotReceiveWithAnyArgs().SetOverrideAsync(Arg.Any<string>(), Arg.Any<ModelKind>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetLocalModelDetails_WhenCodexCloudModel_ReportsItsCatalogWindow_AndNeverReachesTheGgufBranch()
    {
        // A Codex cloud id has no LOCAL details; its one detail is the catalog window. A same-named GGUF proves the
        // endpoint short-circuits: falling through would report that file's window instead.
        await using var factory = CreateFactory(ggufModelStore: Installed(Gguf("gpt-5.6-terra") with
        {
            MaxContextTokens = 1
        }));
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/models/gpt-5.6-terra/details");
        using var response = await client.SendAsync(request);
        var details = Deserialize<LocalModelDetailsResponse>(await response.Content.ReadAsStringAsync());

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(272_000, details.MaxContextTokens);
        AssertEx.Null(details.Template);
    }

    [Test]
    [Arguments(128_000)]
    [Arguments(null)]
    public async Task GetLocalModelDetails_WhenAzureFoundryDeployment_ReportsTheDeclaredWindow_AndNeverReachesTheGgufBranch(int? declaredWindow)
    {
        // An Azure Foundry deployment id has no LOCAL details either. An undeclared window is still 200 with null; a
        // same-named GGUF proves the branch short-circuits before the installed-model lookup.
        var cloudModelResolver = Substitute.For<ICloudModelResolver>();
        cloudModelResolver.IsCloudModelAsync("gpt-4o-prod", Arg.Any<CancellationToken>()).Returns(true);
        cloudModelResolver.ResolveContextWindowAsync("gpt-4o-prod", Arg.Any<CancellationToken>()).Returns(new CloudModelContextWindow
        {
            ContextTokens = declaredWindow
        });
        var ggufModelStore = Installed(Gguf("gpt-4o-prod") with
        {
            MaxContextTokens = 1
        });
        await using var factory = CreateFactory(ggufModelStore: ggufModelStore, cloudModelResolver: cloudModelResolver);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/models/gpt-4o-prod/details");
        using var response = await client.SendAsync(request);
        var details = Deserialize<LocalModelDetailsResponse>(await response.Content.ReadAsStringAsync());

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("gpt-4o-prod", details.ModelName);
        AssertEx.Equal(declaredWindow, details.MaxContextTokens);
        AssertEx.Equal(declaredWindow, details.EffectiveContextTokens);
    }

    [Test]
    public async Task GetLocalModelDetails_WhenACloudIdIsNoLongerRecognised_Returns404()
    {
        // The trust resolver still answers "cloud" for an id whose Azure deployment was removed, but there is no window to report.
        var cloudModelResolver = Substitute.For<ICloudModelResolver>();
        cloudModelResolver.IsCloudModelAsync("gpt-4o-prod", Arg.Any<CancellationToken>()).Returns(true);
        cloudModelResolver.ResolveContextWindowAsync("gpt-4o-prod", Arg.Any<CancellationToken>()).Returns((CloudModelContextWindow?)null);
        var ggufModelStore = Installed(Gguf("gpt-4o-prod"));
        await using var factory = CreateFactory(ggufModelStore: ggufModelStore, cloudModelResolver: cloudModelResolver);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/models/gpt-4o-prod/details");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task SelectLocalModel_WhenValid_PersistsDefaultModelAndKeepsOtherSettings()
    {
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            MaxMessageRequestTimeoutSeconds = 120
        });
        await using var factory = CreateFactory(settingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/models/select");
        request.Content = JsonContent.Create(new SelectLocalModelRequest
        {
            ModelName = ChatModel
        });
        using var response = await client.SendAsync(request);
        var selection = await ReadJsonAsync<SelectLocalModelResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(ChatModel, selection.SelectedModelName);
        AssertEx.Equal(ChatModel, settingsStore.Current.DefaultModelName);
        AssertEx.Equal(expected: 120, settingsStore.Current.MaxMessageRequestTimeoutSeconds);
    }

    [Test]
    public async Task SelectLocalModel_WhenDecisionModel_Returns400AndKeepsTheDefault()
    {
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            DefaultModelName = "chat-model"
        });
        var ggufModelStore = Installed(Gguf("org/Laya-GGUF:Q8_0") with
        {
            DecisionType = "classify"
        });
        await using var factory = CreateFactory(settingsStore, ggufModelStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/models/select");
        request.Content = JsonContent.Create(new SelectLocalModelRequest
        {
            ModelName = "org/Laya-GGUF:Q8_0"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal("chat-model", settingsStore.Current.DefaultModelName);
    }

    [Test]
    public async Task DeleteLocalModel_WhenValid_UsesJournaledGgufCoordinator()
    {
        // Local models are GGUF files served by the bundled llama.cpp runtime; the journaled coordinator is the deletion
        // path, and the store's own delete is what it must not fall back to.
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        var deletionCoordinator = DeletionCoordinator("orca-mini:latest");
        await using var factory = CreateFactory(ggufModelStore: ggufModelStore, deletionCoordinator: deletionCoordinator);
        using var client = factory.CreateClient();

        using var deleteRequest = CreateRequest(factory, HttpMethod.Delete, "/api/local/v1/models/orca-mini:latest");
        using var deleteResponse = await client.SendAsync(deleteRequest);
        var deleted = await ReadJsonAsync<DeleteLocalModelResponse>(deleteResponse);

        AssertEx.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        AssertEx.Equal("orca-mini:latest", deleted.ModelName);
        AssertEx.True(deleted.Deleted);
        await deletionCoordinator.Received(1).CommitDeleteAsync("orca-mini:latest", Arg.Any<CancellationToken>());
        await deletionCoordinator.Received(1).PurgeAfterSuccessAsync(Arg.Any<CommittedModelDeletion>(), CancellationToken.None);
        await ggufModelStore.DidNotReceiveWithAnyArgs().DeleteModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteLocalModel_WhenModelAlreadyGone_ReturnsOkIdempotently()
    {
        // Regression: CommitDeleteAsync throws KeyNotFoundException when the mutation lease has no snapshot (the model
        // was already deleted/never existed). Delete must stay idempotent (200) rather than 500ing, and report
        // Deleted=false so the caller can tell the no-op apart from a real removal.
        var deletionCoordinator = Substitute.For<ILocalModelDeletionCoordinator>();
        deletionCoordinator.CommitDeleteAsync("ghost:model", Arg.Any<CancellationToken>())
                           .Returns<Task<CommittedModelDeletion>>(_ => throw new KeyNotFoundException("The installed model was not found."));
        await using var factory = CreateFactory(deletionCoordinator: deletionCoordinator);
        using var client = factory.CreateClient();

        using var deleteRequest = CreateRequest(factory, HttpMethod.Delete, "/api/local/v1/models/ghost:model");
        using var deleteResponse = await client.SendAsync(deleteRequest);
        var deleted = await ReadJsonAsync<DeleteLocalModelResponse>(deleteResponse);

        AssertEx.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        AssertEx.Equal("ghost:model", deleted.ModelName);
        AssertEx.False(deleted.Deleted, "a model that was never installed reports no deletion");
        await deletionCoordinator.DidNotReceiveWithAnyArgs()
                                 .PurgeAfterSuccessAsync(Arg.Any<CommittedModelDeletion>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteLocalModel_WhenDependentAdaptersBlockIt_WritesTheConflictProblemDetailsEnvelope()
    {
        // The refusal is a typed conflict exception the endpoint must NOT catch: the global ConflictExceptionHandler
        // answers the one 409 problem+json envelope every other conflict uses, discriminated by conflictType, with the
        // refusal sentence as `detail` (which is what the SPA's apiErrorMessage toasts).
        var deletionCoordinator = Substitute.For<ILocalModelDeletionCoordinator>();
        deletionCoordinator.CommitDeleteAsync("base:model", Arg.Any<CancellationToken>())
                           .Returns<Task<CommittedModelDeletion>>(_ => throw new InstalledModelDependentAdaptersException());
        await using var factory = CreateFactory(deletionCoordinator: deletionCoordinator);
        using var client = factory.CreateClient();

        using var deleteRequest = CreateRequest(factory, HttpMethod.Delete, "/api/local/v1/models/base:model");
        using var deleteResponse = await client.SendAsync(deleteRequest);

        AssertEx.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);
        AssertEx.Contains(deleteResponse.Content.Headers.ContentType?.ToString(), "problem+json", StringComparison.OrdinalIgnoreCase);
        var problem = await ReadJsonAsync<DeleteConflictProblemBody>(deleteResponse);
        AssertEx.Equal(nameof(NodeConflictProblemType.InstalledModelHasDependentAdapters), problem.ConflictType);
        AssertEx.Equal(expected: 409, problem.Status);
        AssertEx.Contains(problem.Detail, "Remove them before deleting it", StringComparison.Ordinal);
        AssertEx.NotEmpty(problem.TraceId);
        await deletionCoordinator.DidNotReceiveWithAnyArgs()
                                 .PurgeAfterSuccessAsync(Arg.Any<CommittedModelDeletion>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteLocalModel_WhenNameHasEncodedSlashes_DecodesBeforeDeleting()
    {
        // The hey-api client escapes the route segment with encodeURIComponent, and Kestrel leaves %2F encoded by design,
        // so the bound name arrives as "hf.co%2F...". The endpoint must decode it and delete the canonical HF reference.
        var deletionCoordinator = DeletionCoordinator(HfName);
        await using var factory = CreateFactory(deletionCoordinator: deletionCoordinator);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Delete, $"/api/local/v1/models/{EncodedHfName}");
        using var response = await client.SendAsync(request);
        var deleted = await ReadJsonAsync<DeleteLocalModelResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(HfName, deleted.ModelName);
        AssertEx.True(deleted.Deleted);
        await deletionCoordinator.Received(1).CommitDeleteAsync(HfName, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteLocalModel_WhenNameIsPlainTag_DeletesUnchanged()
    {
        var deletionCoordinator = DeletionCoordinator(ChatModel);
        await using var factory = CreateFactory(deletionCoordinator: deletionCoordinator);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Delete, $"/api/local/v1/models/{ChatModel}");
        using var response = await client.SendAsync(request);
        var deleted = await ReadJsonAsync<DeleteLocalModelResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(ChatModel, deleted.ModelName);
        await deletionCoordinator.Received(1).CommitDeleteAsync(ChatModel, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteLocalModel_WhenEncodedSlashHidesTraversal_ReturnsValidationProblem()
    {
        // ..%2F..%2Fetc decodes to "../../etc", which the validator's ".." guard rejects AFTER decoding — so decoding
        // cannot smuggle path traversal past the guard.
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        var deletionCoordinator = DeletionCoordinator("unused");
        await using var factory = CreateFactory(ggufModelStore: ggufModelStore, deletionCoordinator: deletionCoordinator);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Delete, "/api/local/v1/models/hf.co%2F..%2F..%2Fetc");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await deletionCoordinator.DidNotReceiveWithAnyArgs().CommitDeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await ggufModelStore.DidNotReceiveWithAnyArgs().DeleteModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteLocalModel_WhenNonGgufProvider_PreservesProviderDeletionPath()
    {
        var deletionCoordinator = DeletionCoordinator("other:model");
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(ExternalProviderConstants.ProviderName);
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(_ => Task.FromResult(ExternalProviderConstants.ProviderName));
        providerResolver.ResolveProvider(ExternalProviderConstants.ProviderName).Returns(provider);
        await using var factory = CreateFactory(deletionCoordinator: deletionCoordinator, providerResolver: providerResolver);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Delete, "/api/local/v1/models/other:model");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await provider.Received(1).DeleteModelAsync("other:model", Arg.Any<CancellationToken>());
        await deletionCoordinator.DidNotReceiveWithAnyArgs().CommitDeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetLocalModelDetails_WhenGgufModel_ReturnsSecretSafeGgufMetadata()
    {
        // A llama.cpp model's details come from the installed-GGUF registry: the descriptor's context length surfaces as
        // MaxContextTokens on the shared details response.
        await using var factory = CreateFactory(ggufModelStore: Installed(Gguf("Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M") with
        {
            SizeBytes = 491_000_000,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = 32768,
            Origin = LocalModelOrigin.Imported,
            ModelContentFingerprint = "v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        }));
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/models/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M/details");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var details = Deserialize<LocalModelDetailsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M", details.ModelName);
        AssertEx.Equal(expected: 32768, details.MaxContextTokens);
        AssertEx.Equal(LocalModelOrigin.Imported, details.Origin);
        AssertEx.Equal("v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            details.ModelContentFingerprint);
        AssertEx.False(body.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task GetLocalModelDetails_WhenGgufModelNotInstalled_Returns404()
    {
        // A name that routes to llamacpp but has no installed descriptor (stale map row / removed file) has no details.
        await using var factory = CreateFactory(ggufModelStore: Installed());
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/models/ghost-gguf:Q4_K_M/details");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task GetLocalModelDetails_WhenGgufNameHasEncodedSlashes_DecodesBeforeResolving()
    {
        // Decode must precede provider resolution AND the GGUF lookup: the resolver and the registry are both keyed by
        // the decoded canonical name, so an encoded HF reference resolves and matches the installed descriptor.
        await using var factory = CreateFactory(ggufModelStore: Installed(Gguf(HfName) with
        {
            MaxContextTokens = 8192
        }));
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"/api/local/v1/models/{EncodedHfName}/details");
        using var response = await client.SendAsync(request);
        var details = await ReadJsonAsync<LocalModelDetailsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(HfName, details.ModelName);
        AssertEx.Equal(expected: 8192, details.MaxContextTokens);
    }

    [Test]
    public async Task SetModelKind_WhenNameHasEncodedSlashes_DecodesBeforeStoring()
    {
        var classificationService = Substitute.For<IModelClassificationService>();
        classificationService.SetOverrideAsync(Arg.Any<string>(), Arg.Any<ModelKind>(), Arg.Any<CancellationToken>())
                             .Returns(new ModelClassificationResult
                             {
                                 ModelName = HfName,
                                 Kind = ModelKind.Chat,
                                 DetectedKind = ModelKind.Unknown,
                                 Capabilities = [],
                                 IsOverridden = true
                             });
        await using var factory = CreateFactory(classificationService: classificationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, $"/api/local/v1/models/{EncodedHfName}/kind");
        request.Content = JsonContent.Create(new SetModelKindRequest
        {
            Kind = "Chat"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await classificationService.Received(1).SetOverrideAsync(HfName, ModelKind.Chat, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResetModelKind_WhenNameHasEncodedSlashes_DecodesBeforeResetting()
    {
        var classificationService = Substitute.For<IModelClassificationService>();
        classificationService.ResetOverrideAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                             .Returns(new ModelClassificationResult
                             {
                                 ModelName = HfName,
                                 Kind = ModelKind.Unknown,
                                 DetectedKind = ModelKind.Unknown,
                                 Capabilities = [],
                                 IsOverridden = false
                             });
        await using var factory = CreateFactory(classificationService: classificationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Delete, $"/api/local/v1/models/{EncodedHfName}/kind");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await classificationService.Received(1).ResetOverrideAsync(HfName, Arg.Any<CancellationToken>());
    }

    private static async Task<LocalModelResponse> ListSingleAsync(TestServerWebAppFactory factory, HttpClient client, string modelName)
    {
        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/models");
        using var response = await client.SendAsync(request);
        var models = await ReadJsonAsync<ListLocalModelsResponse>(response);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return models.Items.Single(item => item.ModelName == modelName);
    }

    private static LocalModelDescriptor Gguf(string modelName)
    {
        return new LocalModelDescriptor
        {
            ModelName = modelName,
            ProviderName = LocalModelProviders.LlamaCpp,
            IsAvailable = true,
            SizeBytes = 1,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = null,
            Capabilities = ["completion", "tools"]
        };
    }

    private static IGgufModelStore Installed(params LocalModelDescriptor[] descriptors)
    {
        var store = Substitute.For<IGgufModelStore>();
        store.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns(descriptors);
        return store;
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
                   .Returns(new CommittedModelDeletion
                   {
                       OperationId = receipt.OperationId,
                       RequestedModelName = modelName,
                       RemovedModelNames = [modelName],
                       StageReceipt = receipt
                   });
        return coordinator;
    }

    private static HttpRequestMessage CreateRequest(TestServerWebAppFactory factory, HttpMethod method, string uri)
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

    /// <summary>The members of the shared <c>ConflictProblemDetails</c> envelope a blocked delete must carry.</summary>
    private sealed record DeleteConflictProblemBody(string ConflictType, int Status, string Detail, string TraceId);

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions));
    }

    // These tests cover the LOCAL model surface, so the Codex token store is stubbed to "no session": an ambient Codex
    // session on the dev machine would otherwise add cloud models to the list and make the local assertions (e.g.
    // available-and-empty) non-deterministic. The dedicated cloud-model mapping tests cover the cloud path.
    private static TestServerWebAppFactory CreateFactory(FakeNodeSettingsStore? settingsStore = null,
        IGgufModelStore? ggufModelStore = null,
        ICloudModelResolver? cloudModelResolver = null,
        ILocalModelDeletionCoordinator? deletionCoordinator = null,
        IModelClassificationService? classificationService = null,
        ILocalModelProviderResolver? providerResolver = null)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeSettingsStore>();
                services.AddSingleton<INodeSettingsStore>(settingsStore ?? new FakeNodeSettingsStore(new StoredNodeSettings()));

                var codexTokenStore = Substitute.For<ICodexTokenStore>();
                codexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
                services.RemoveAll<ICodexTokenStore>();
                services.AddSingleton(codexTokenStore);

                Replace(services, ggufModelStore);
                Replace(services, cloudModelResolver);
                Replace(services, deletionCoordinator);
                Replace(services, classificationService);
                Replace(services, providerResolver);
            }
        };
    }

    private static void Replace<TService>(IServiceCollection services, TService? instance)
        where TService : class
    {
        if (instance is not null)
        {
            services.RemoveAll<TService>();
            services.AddSingleton(instance);
        }
    }
}
