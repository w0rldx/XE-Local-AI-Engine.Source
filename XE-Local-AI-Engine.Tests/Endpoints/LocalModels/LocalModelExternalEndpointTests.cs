namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     How the existing local-model routes behave once an <c>ext:</c> id can reach them: the list carries the
///     connection metadata, the details route answers from the declarations, selection is validated against the
///     registry, and the delete route refuses with a 409 instead of a 500.
/// </summary>
public sealed class LocalModelExternalEndpointTests
{
    private const string ModelId = "ext:unsloth-box/qwen3-27b";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task ListModels_CarriesTheExternalEntryWithItsConnectionMetadata()
    {
        var catalogService = Substitute.For<ILocalModelCatalogService>();
        catalogService.GetCatalogAsync(Arg.Any<CancellationToken>())
                      .Returns(new LocalModelCatalog(SelectedModelName: null,
                          ConfiguredDefaultModelName: null,
                          OllamaModels: [],
                          Classifications: new Dictionary<string, ModelClassificationResult>(StringComparer.OrdinalIgnoreCase),
                          InstalledGgufModels: [],
                          HasUsableCodexSession: false,
                          AzureFoundryConnection: null,
                          ExternalModels: [Registration()]));
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILocalModelCatalogService>();
                services.AddSingleton(catalogService);
            }
        };
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/models");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var models = await ReadJsonAsync<ListLocalModelsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var model = models.Items.Single();
        AssertEx.Equal(ModelId, model.ModelName);
        AssertEx.Equal(LocalModelProviders.External, model.Provider);
        AssertEx.Equal("Qwen3 27B", model.DisplayLabel);
        AssertEx.Equal("unsloth-box", model.ExternalConnectionId);
        AssertEx.Equal("Unsloth box", model.ExternalConnectionName);
        AssertEx.Equal(LocalModelDeclaredLocalities.Local, model.DeclaredLocality);
    }

    [Test]
    public async Task GetModelDetails_ForAnExternalModel_AnswersFromTheDeclarationsWithTheConnectionMetadata()
    {
        var trustResolver = Substitute.For<IModelTrustResolver>();
        trustResolver.TryResolveExternalAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Registration());
        await using var factory = CreateFactory(trustResolver: trustResolver);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"/api/local/v1/models/{Uri.EscapeDataString(ModelId)}/details");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var details = await ReadJsonAsync<LocalModelDetailsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(ModelId, details.ModelName);

        // For an endpoint the node does not launch, the declared window IS the effective one — and the meter reads
        // the effective one.
        AssertEx.Equal(expected: 32768, details.MaxContextTokens);
        AssertEx.Equal(expected: 32768, details.EffectiveContextTokens);
        AssertEx.Equal("Qwen3 27B", details.DisplayLabel);
        AssertEx.Equal("unsloth-box", details.ExternalConnectionId);
        AssertEx.Equal("Unsloth box", details.ExternalConnectionName);
        AssertEx.Equal(LocalModelDeclaredLocalities.Local, details.DeclaredLocality);

        // Declared, and false: this endpoint reasons on its own terms and ignores reasoning_effort.
        AssertEx.Equal(expected: false, details.IsReasoningEffortCapable);
    }

    [Test]
    public async Task GetModelDetails_CarriesADeclaredGradedEffortCapability()
    {
        var trustResolver = Substitute.For<IModelTrustResolver>();
        trustResolver.TryResolveExternalAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                     .Returns(Registration(supportsReasoning: true, supportsReasoningEffort: true));
        await using var factory = CreateFactory(trustResolver: trustResolver);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"/api/local/v1/models/{Uri.EscapeDataString(ModelId)}/details");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var details = await ReadJsonAsync<LocalModelDetailsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        // A details view reached directly has no list entry to read the capability from, so it has to carry its own.
        AssertEx.Equal(expected: true, details.IsReasoningEffortCapable);
    }

    [Test]
    public async Task SelectModel_WhenNoConnectionRegistersTheExternalId_Returns400AndWritesNothing()
    {
        // A well-formed id whose registration is gone passes the name grammar. Storing it as the node default would
        // make every chat turn fail to route with no explanation of why.
        var trustResolver = Substitute.For<IModelTrustResolver>();
        trustResolver.TryResolveExternalAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns((ExternalProviderModelRegistration?)null);
        var settingsStore = new RecordingNodeSettingsStore();
        await using var factory = CreateFactory(trustResolver: trustResolver, settingsStore: settingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/models/select");
        request.Content = JsonContent.Create(new SelectLocalModelRequest
        {
            ModelName = ModelId
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, settingsStore.SaveCount);
    }

    [Test]
    public async Task SelectModel_WhenTheExternalIdIsRegistered_IsAccepted()
    {
        var trustResolver = Substitute.For<IModelTrustResolver>();
        trustResolver.TryResolveExternalAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Registration());
        var settingsStore = new RecordingNodeSettingsStore();
        await using var factory = CreateFactory(trustResolver: trustResolver, settingsStore: settingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/models/select");
        request.Content = JsonContent.Create(new SelectLocalModelRequest
        {
            ModelName = ModelId
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var selection = await ReadJsonAsync<SelectLocalModelResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(ModelId, selection.SelectedModelName);
        AssertEx.Equal(ModelId, settingsStore.LastSavedDefaultModelName);
    }

    [Test]
    public async Task DeleteModel_ForAnExternalModel_Returns409RatherThan500()
    {
        // The provider owns no weights on this node, so it refuses rather than reporting a success the model table
        // would render as a completed removal. A 500 would read as node trouble instead of "wrong lifecycle".
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(ExternalProviderConstants.ProviderName);
        provider.DeleteModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw new ExternalProviderOperationNotSupportedException("External models are removed by unregistering them on their connection, not by deleting local weights."));
        await using var factory = CreateFactory(externalProvider: provider);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Delete, $"/api/local/v1/models/{Uri.EscapeDataString(ModelId)}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.True(body.Contains("ModelOperationNotSupportedByProvider", StringComparison.Ordinal),
            "the 409 envelope must carry the conflictType the SPA discriminates on");
    }

    private static ExternalProviderModelRegistration Registration(bool supportsReasoning = false, bool supportsReasoningEffort = false)
    {
        return new ExternalProviderModelRegistration(new ExternalProviderConnectionDescriptor
            {
                Id = "unsloth-box",
                DisplayName = "Unsloth box",
                BaseUrl = new Uri("http://127.0.0.1:18099/v1/"),
                Locality = ExternalProviderLocality.Local
            },
            new ExternalProviderModelDescriptor
            {
                WireId = "qwen3-27b",
                DisplayName = "Qwen3 27B",
                ContextLength = 32768,
                SupportsReasoning = supportsReasoning,
                SupportsReasoningEffort = supportsReasoningEffort
            });
    }

    private static TestServerWebAppFactory CreateFactory(IModelTrustResolver? trustResolver = null,
        RecordingNodeSettingsStore? settingsStore = null,
        ILocalModelProvider? externalProvider = null)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IModelTrustResolver>();
                services.AddSingleton(trustResolver ?? Substitute.For<IModelTrustResolver>());
                services.RemoveAll<INodeSettingsStore>();
                services.AddSingleton<INodeSettingsStore>(settingsStore ?? new RecordingNodeSettingsStore());

                if (externalProvider is null)
                {
                    return;
                }

                // Routes every model to the external provider, so the deletion path reaches the provider's refusal —
                // the real administration service in between is what this test is actually exercising.
                var resolver = Substitute.For<ILocalModelProviderResolver>();
                resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(_ => Task.FromResult(ExternalProviderConstants.ProviderName));
                resolver.ResolveProvider(Arg.Any<string>()).Returns(externalProvider);
                services.RemoveAll<ILocalModelProviderResolver>();
                services.AddSingleton(resolver);
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

    /// <summary>An in-memory node-settings store that records whether a selection was actually written.</summary>
    private sealed class RecordingNodeSettingsStore : INodeSettingsStore
    {
        private StoredNodeSettings _settings = new();

        public int SaveCount { get; private set; }

        public string? LastSavedDefaultModelName { get; private set; }

        public Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_settings);
        }

        public StoredNodeSettings Load(CancellationToken cancellationToken = default)
        {
            return _settings;
        }

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            SaveCount++;
            LastSavedDefaultModelName = settings.DefaultModelName;
            return Task.CompletedTask;
        }

        public async Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate, CancellationToken cancellationToken = default)
        {
            await SaveAsync(mutate(_settings), cancellationToken);
            return _settings;
        }
    }
}
