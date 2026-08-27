namespace XE_Local_AI_Engine.Tests.Endpoints.ExternalProviders;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The connection CRUD surface: what it returns, what it must never return, and how the two write outcomes reach
///     the wire.
/// </summary>
public sealed class ExternalProviderEndpointTests
{
    private const string ApiKey = "sk-unsloth-super-secret";
    private const string ConnectionsRoute = "/api/local/v1/external-providers/connections";
    private const string ConnectionRoute = $"{ConnectionsRoute}/unsloth-box";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task ListConnections_ReportsTheKeysPresenceAndNeverTheKeyItself()
    {
        var store = Substitute.For<IExternalProviderStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(CreateConfig());
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, ConnectionsRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var connections = Deserialize<ExternalProviderConnectionsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("rev-1", connections.Revision);
        var connection = connections.Connections.Single();
        AssertEx.Equal("unsloth-box", connection.Id);
        AssertEx.Equal("Local", connection.Locality);
        AssertEx.True(connection.HasApiKey);

        // The namespaced id is composed server-side so the picker selects exactly what the provider map routes.
        AssertEx.Equal("ext:unsloth-box/qwen3-27b", connection.Models.Single().ModelId);
        AssertEx.False(body.Contains(ApiKey, StringComparison.Ordinal));
    }

    [Test]
    public async Task GetConnection_WhenTheSlugIsNotStored_Returns404()
    {
        var store = Substitute.For<IExternalProviderStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(CreateConfig());
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"{ConnectionsRoute}/not-configured");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task SaveConnection_PassesAnAbsentApiKeyThroughAsAbsent()
    {
        // The single most important contract on this route. The editor renders a masked placeholder and sends NO key
        // back, so an endpoint that helpfully normalized the missing field to "" would clear the stored key the first
        // time an operator renamed a working connection.
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        administrationService.SaveConnectionAsync(Arg.Any<ExternalProviderConnectionSaveRequest>(), Arg.Any<CancellationToken>())
                             .Returns(new ExternalProviderWriteResult.Committed(CreateConfig(), Changed: true));
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, ConnectionRoute);
        request.Content = JsonContent.Create(new SaveExternalProviderConnectionRequest
        {
            DisplayName = "Unsloth box",
            BaseUrl = "http://127.0.0.1:18099",
            Locality = "Local",
            ExpectedRevision = "rev-0",
            Models =
            [
                new SaveExternalProviderModelRequest
                {
                    WireId = "qwen3-27b",
                    SupportsTools = true
                }
            ]
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await administrationService.Received(1).SaveConnectionAsync(Arg.Is<ExternalProviderConnectionSaveRequest>(saved =>
                saved.Id == "unsloth-box"
                && saved.ApiKey == null
                && !saved.ClearApiKey
                && saved.Locality == ExternalProviderLocality.Local
                && saved.ExpectedRevision == "rev-0"
                && saved.Models.Single().WireId == "qwen3-27b"
                && saved.Models.Single().SupportsTools),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveConnection_WhenTheStoreRefusesTheShape_Returns400CarryingTheStoresOwnMessage()
    {
        const string StoreMessage = "An external connection timeout must be 5-3600 seconds.";
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        administrationService.SaveConnectionAsync(Arg.Any<ExternalProviderConnectionSaveRequest>(), Arg.Any<CancellationToken>())
                             .Returns<ExternalProviderWriteResult>(_ => throw new ExternalProviderValidationException(StoreMessage));
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, ConnectionRoute);
        request.Content = JsonContent.Create(ValidSaveRequest());
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.True(body.Contains(StoreMessage, StringComparison.Ordinal));
    }

    [Test]
    public async Task SaveConnection_WhenTheRevisionIsStale_Returns409CarryingWhatIsActuallyStored()
    {
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        administrationService.SaveConnectionAsync(Arg.Any<ExternalProviderConnectionSaveRequest>(), Arg.Any<CancellationToken>())
                             .Returns(new ExternalProviderWriteResult.Superseded(CreateConfig()));
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, ConnectionRoute);
        request.Content = JsonContent.Create(ValidSaveRequest() with
        {
            ExpectedRevision = "rev-0"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var current = Deserialize<ExternalProviderConnectionsResponse>(body);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The point of answering with the CURRENT config rather than a bare 409: the editor re-renders the real state
        // instead of guessing what the other writer did.
        AssertEx.Equal("rev-1", current.Revision);
        AssertEx.Equal("unsloth-box", current.Connections.Single().Id);
        AssertEx.False(body.Contains(ApiKey, StringComparison.Ordinal));
    }

    [Test]
    public async Task SaveConnection_WhenTheLocalityIsNotDeclared_Returns400WithoutReachingTheStore()
    {
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, ConnectionRoute);
        request.Content = JsonContent.Create(ValidSaveRequest() with
        {
            Locality = "somewhere"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await administrationService.DidNotReceiveWithAnyArgs()
                                   .SaveConnectionAsync(Arg.Any<ExternalProviderConnectionSaveRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteConnection_ForwardsTheExpectedRevisionAndReturnsTheRemainingConfig()
    {
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        administrationService.DeleteConnectionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                             .Returns(new ExternalProviderWriteResult.Committed(new StoredExternalProviderConfig
                             {
                                 Revision = "rev-2"
                             }, Changed: true));
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Delete, $"{ConnectionRoute}?expectedRevision=rev-1");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var connections = await ReadJsonAsync<ExternalProviderConnectionsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("rev-2", connections.Revision);
        AssertEx.Empty(connections.Connections);
        await administrationService.Received(1).DeleteConnectionAsync("unsloth-box", "rev-1", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteConnection_WhenTheRevisionIsStale_Returns409()
    {
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        administrationService.DeleteConnectionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                             .Returns(new ExternalProviderWriteResult.Superseded(CreateConfig()));
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Delete, $"{ConnectionRoute}?expectedRevision=rev-0");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var current = await ReadJsonAsync<ExternalProviderConnectionsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("rev-1", current.Revision);
    }

    private static SaveExternalProviderConnectionRequest ValidSaveRequest()
    {
        return new SaveExternalProviderConnectionRequest
        {
            DisplayName = "Unsloth box",
            BaseUrl = "http://127.0.0.1:18099",
            Locality = "Local"
        };
    }

    private static StoredExternalProviderConfig CreateConfig()
    {
        return new StoredExternalProviderConfig
        {
            Revision = "rev-1",
            Connections =
            [
                new StoredExternalProviderConnection
                {
                    Id = "unsloth-box",
                    DisplayName = "Unsloth box",
                    BaseUrl = "http://127.0.0.1:18099/v1/",
                    ApiKey = ApiKey,
                    Locality = ExternalProviderLocality.Local,
                    Models =
                    [
                        new StoredExternalProviderModel
                        {
                            WireId = "qwen3-27b",
                            DisplayName = "Qwen3 27B",
                            ContextLength = 32768,
                            SupportsTools = true
                        }
                    ]
                }
            ]
        };
    }

    private static TestServerWebAppFactory CreateFactory(IExternalProviderStore? store = null,
        IExternalProviderAdministrationService? administrationService = null)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IExternalProviderStore>();
                services.AddSingleton(store ?? Substitute.For<IExternalProviderStore>());
                services.RemoveAll<IExternalProviderAdministrationService>();
                services.AddSingleton(administrationService ?? Substitute.For<IExternalProviderAdministrationService>());
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
}
