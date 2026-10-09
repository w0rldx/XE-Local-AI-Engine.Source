namespace XE_Local_AI_Engine.Tests.Endpoints.ExternalProviders;

using System.Net;
using System.Net.Http.Json;
using System.Text;
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
[Category(TestCategories.Integration)]
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
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
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
    public async Task ListConnections_WhenAStoredLocalRowCarriesGrants_ListsThemAllFalse()
    {
        // A hand-edited file: the store only normalizes on save, so the read model must apply the descriptor's rule itself.
        var config = CreateConfig();
        var store = Substitute.For<IExternalProviderStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
             .Returns(new StoredExternalProviderConfig
             {
                 Revision = config.Revision,
                 Connections =
                 [
                     config.Connections.Single() with
                     {
                         CloudGrants = new ExternalProviderCloudGrants
                         {
                             LocalData = true,
                             UnattendedRuns = true,
                             WebTools = true,
                             McpTools = true,
                             SubAgents = true
                         }
                     }
                 ]
             });
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, ConnectionsRoute);
        using var response = await client.SendAsync(request);
        var connection = Deserialize<ExternalProviderConnectionsResponse>(await response.Content.ReadAsStringAsync()).Connections.Single();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("Local", connection.Locality);
        var grants = AssertEx.NotNull(connection.CloudGrants);
        AssertEx.False(grants.LocalData || grants.UnattendedRuns || grants.WebTools || grants.McpTools || grants.SubAgents,
            "a Local connection never surfaces grants");
    }

    [Test]
    public async Task GetConnection_WhenTheSlugIsNotStored_Returns404ProblemDetailsSayingSo()
    {
        var store = Substitute.For<IExternalProviderStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(CreateConfig());
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"{ConnectionsRoute}/not-configured");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.Contains(body, ExternalProviderProbeResult.UnknownConnectionError);
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
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await administrationService.Received(1).SaveConnectionAsync(Arg.Is<ExternalProviderConnectionSaveRequest>(saved =>
                saved.Id == "unsloth-box"
                && saved.ApiKey == null
                && !saved.ClearApiKey
                && saved.Locality == ExternalProviderLocality.Local
                && saved.ExpectedRevision == "rev-0"
                && saved.Models.Single().WireId == "qwen3-27b"
                && saved.Models.Single().SupportsTools == true),
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
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

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
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var current = Deserialize<ExternalProviderConnectionsResponse>(body);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The point of answering with the CURRENT config rather than a bare 409: the editor re-renders the real state
        // instead of guessing what the other writer did.
        AssertEx.Equal("rev-1", current.Revision);
        AssertEx.Equal("unsloth-box", current.Connections.Single().Id);
        AssertEx.False(body.Contains(ApiKey, StringComparison.Ordinal));
    }

    [Test]
    public async Task SaveConnection_PassesHeaderRowsThrough_ABlankSecretValueAsAbsent()
    {
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        administrationService.SaveConnectionAsync(Arg.Any<ExternalProviderConnectionSaveRequest>(), Arg.Any<CancellationToken>())
                             .Returns(new ExternalProviderWriteResult.Committed(CreateConfig(), Changed: true));
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, ConnectionRoute);
        request.Content = JsonContent.Create(ValidSaveRequest() with
        {
            Headers =
            [
                new ExternalProviderHeaderRequest
                {
                    Name = "X-Demo-Project",
                    Value = "demo"
                },
                new ExternalProviderHeaderRequest
                {
                    Name = "X-Demo-Token",
                    IsSecret = true
                }
            ]
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await administrationService.Received(1).SaveConnectionAsync(Arg.Is<ExternalProviderConnectionSaveRequest>(saved =>
                saved.Headers.Count == 2
                && saved.Headers[0].Name == "X-Demo-Project" && saved.Headers[0].Value == "demo" && !saved.Headers[0].IsSecret
                && saved.Headers[1].Name == "X-Demo-Token" && saved.Headers[1].Value == null && saved.Headers[1].IsSecret),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("Authorization", "v4lue-must-not-leak", "is reserved")]
    [Arguments("AUTHORIZATION", "v4lue-must-not-leak", "is reserved")]
    [Arguments("cookie", "v4lue-must-not-leak", "is reserved")]
    [Arguments("X-Demo-Project", "v4lue-must-not-leak\r\nX-Injected: 1", "invalid control characters")]
    [Arguments("X-Demo-Project", "v4lue-must-not-leak\0", "invalid control characters")]
    [Arguments("X-Demo Project", "v4lue-must-not-leak", "invalid characters")]
    [Arguments("", "v4lue-must-not-leak", "without a header name")]
    public async Task SaveConnection_WithAnInvalidHeaderRow_Returns400NamingItWithoutReachingTheStore(string name, string value, string expected)
    {
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, ConnectionRoute);
        request.Content = JsonContent.Create(ValidSaveRequest() with
        {
            Headers =
            [
                new ExternalProviderHeaderRequest
                {
                    Name = name,
                    Value = value,
                    IsSecret = true
                }
            ]
        });
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, expected);
        AssertEx.False(body.Contains("v4lue-must-not-leak", StringComparison.Ordinal), "a refusal names the header, never its value");
        await administrationService.DidNotReceiveWithAnyArgs()
                                   .SaveConnectionAsync(Arg.Any<ExternalProviderConnectionSaveRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveConnection_WithDuplicateHeaderNamesByCase_Returns400()
    {
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, ConnectionRoute);
        request.Content = JsonContent.Create(ValidSaveRequest() with
        {
            Headers =
            [
                new ExternalProviderHeaderRequest
                {
                    Name = "X-Demo-Project",
                    Value = "a"
                },
                new ExternalProviderHeaderRequest
                {
                    Name = "x-demo-project",
                    Value = "b"
                }
            ]
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "is duplicated");
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
        using var response = await client.SendAsync(request);

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
        using var response = await client.SendAsync(request);
        var connections = await ReadJsonAsync<ExternalProviderConnectionsResponse>(response);

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
        using var response = await client.SendAsync(request);
        var current = await ReadJsonAsync<ExternalProviderConnectionsResponse>(response);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("rev-1", current.Revision);
    }

    [Test]
    public async Task ListConnections_ComputesInsecureTransportAndEchoesTheOptIn()
    {
        var loopback = CreateConfig().Connections.Single();
        var store = Substitute.For<IExternalProviderStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(CreateConfig() with
        {
            Connections =
            [
                loopback,
                loopback with
                {
                    Id = "legacy-lan",
                    BaseUrl = "http://192.168.1.40:8080/v1/"
                },
                loopback with
                {
                    Id = "opted-in-lan",
                    BaseUrl = "http://192.168.1.41:8080/v1/",
                    AllowInsecureHttp = true
                }
            ]
        });
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, ConnectionsRoute);
        using var response = await client.SendAsync(request);
        var connections = (await ReadJsonAsync<ExternalProviderConnectionsResponse>(response)).Connections.ToDictionary(connection => connection.Id, StringComparer.Ordinal);

        // A row saved over plain http before HTTPS became the default keeps working; the list flags it for the editor.
        AssertEx.False(connections["unsloth-box"].InsecureTransport);
        AssertEx.True(connections["legacy-lan"].InsecureTransport);
        AssertEx.False(connections["legacy-lan"].AllowInsecureHttp);
        AssertEx.True(connections["opted-in-lan"].InsecureTransport);
        AssertEx.True(connections["opted-in-lan"].AllowInsecureHttp);
    }

    [Test]
    public async Task SaveConnection_PassesTheInsecureHttpOptInThrough()
    {
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        administrationService.SaveConnectionAsync(Arg.Any<ExternalProviderConnectionSaveRequest>(), Arg.Any<CancellationToken>())
                             .Returns(new ExternalProviderWriteResult.Committed(CreateConfig(), Changed: true));
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, ConnectionRoute);
        request.Content = JsonContent.Create(ValidSaveRequest() with
        {
            BaseUrl = "http://192.168.1.40:8080",
            AllowInsecureHttp = true
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await administrationService.Received(1).SaveConnectionAsync(Arg.Is<ExternalProviderConnectionSaveRequest>(saved => saved.AllowInsecureHttp),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveConnection_CarriesTriStateCapabilitiesBothWays()
    {
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        var stored = CreateConfig();
        var storedConnection = stored.Connections.Single();
        administrationService.SaveConnectionAsync(Arg.Any<ExternalProviderConnectionSaveRequest>(), Arg.Any<CancellationToken>())
                             .Returns(new ExternalProviderWriteResult.Committed(stored with
                             {
                                 Connections =
                                 [
                                     storedConnection with
                                     {
                                         Models =
                                         [
                                             storedConnection.Models.Single() with
                                             {
                                                 SupportsVision = false,
                                                 SupportsReasoning = null
                                             }
                                         ]
                                     }
                                 ]
                             }, Changed: true));
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, ConnectionRoute);
        request.Content = JsonContent.Create(ValidSaveRequest() with
        {
            Models =
            [
                new SaveExternalProviderModelRequest
                {
                    WireId = "qwen3-27b",
                    SupportsTools = true,
                    SupportsVision = false,
                    SupportsReasoning = null
                }
            ]
        });
        using var response = await client.SendAsync(request);
        var model = (await ReadJsonAsync<ExternalProviderConnectionsResponse>(response)).Connections.Single().Models.Single();

        // Unknown (null) is a third answer, not an omitted false: it survives the request and the response alike.
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await administrationService.Received(1).SaveConnectionAsync(Arg.Is<ExternalProviderConnectionSaveRequest>(saved =>
                saved.Models.Single().SupportsTools == true && saved.Models.Single().SupportsVision == false && saved.Models.Single().SupportsReasoning == null),
            Arg.Any<CancellationToken>());
        AssertEx.True(model.SupportsTools == true);
        AssertEx.True(model.SupportsVision == false);
        AssertEx.Null(model.SupportsReasoning);
    }

    [Test]
    public async Task SaveConnection_CarriesCloudGrantsBothWays()
    {
        var grants = new ExternalProviderCloudGrants
        {
            WebTools = true,
            SubAgents = true
        };
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        var stored = CreateConfig();
        administrationService.SaveConnectionAsync(Arg.Any<ExternalProviderConnectionSaveRequest>(), Arg.Any<CancellationToken>())
                             .Returns(new ExternalProviderWriteResult.Committed(stored with
                             {
                                 Connections =
                                 [
                                     stored.Connections.Single() with
                                     {
                                         Locality = ExternalProviderLocality.Cloud,
                                         CloudGrants = grants
                                     }
                                 ]
                             }, Changed: true));
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, ConnectionRoute);
        request.Content = JsonContent.Create(ValidSaveRequest() with
        {
            Locality = "Cloud",
            CloudGrants = new ExternalProviderCloudGrantsRequest
            {
                WebTools = true,
                SubAgents = true
            }
        });
        using var response = await client.SendAsync(request);
        var returned = (await ReadJsonAsync<ExternalProviderConnectionsResponse>(response)).Connections.Single().CloudGrants;

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await administrationService.Received(1).SaveConnectionAsync(Arg.Is<ExternalProviderConnectionSaveRequest>(saved => saved.CloudGrants == grants),
            Arg.Any<CancellationToken>());
        AssertEx.Equal(new ExternalProviderCloudGrantsResponse
        {
            LocalData = false,
            UnattendedRuns = false,
            WebTools = true,
            McpTools = false,
            SubAgents = true
        }, returned);
    }

    [Test]
    public async Task SaveConnection_WithoutCloudGrants_GrantsNoneAndAStoredRowWithoutThemReadsAllFalse()
    {
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        administrationService.SaveConnectionAsync(Arg.Any<ExternalProviderConnectionSaveRequest>(), Arg.Any<CancellationToken>())
                             .Returns(new ExternalProviderWriteResult.Committed(CreateConfig(), Changed: true));
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, ConnectionRoute);
        request.Content = JsonContent.Create(ValidSaveRequest());
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await administrationService.Received(1).SaveConnectionAsync(Arg.Is<ExternalProviderConnectionSaveRequest>(saved => saved.CloudGrants == null),
            Arg.Any<CancellationToken>());
        // The stored row predates grants (null); the response still carries the object, never null.
        var returned = Deserialize<ExternalProviderConnectionsResponse>(body).Connections.Single().CloudGrants;
        AssertEx.NotNull(returned);
        AssertEx.False(returned.LocalData || returned.UnattendedRuns || returned.WebTools || returned.McpTools || returned.SubAgents);
    }

    [Test]
    [Arguments("\"yes\"")]
    [Arguments("1")]
    [Arguments("null")]
    public async Task SaveConnection_WithANonBooleanGrant_Returns400WithoutReachingTheStore(string value)
    {
        var administrationService = Substitute.For<IExternalProviderAdministrationService>();
        await using var factory = CreateFactory(administrationService: administrationService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, ConnectionRoute);
        request.Content = new StringContent($$$"""{"displayName":"Unsloth box","baseUrl":"https://gateway.example.com","locality":"Cloud","models":[],"cloudGrants":{"webTools":{{{value}}}}}""",
            Encoding.UTF8,
            "application/json");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await administrationService.DidNotReceiveWithAnyArgs()
                                   .SaveConnectionAsync(Arg.Any<ExternalProviderConnectionSaveRequest>(), Arg.Any<CancellationToken>());
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
        await using var stream = await response.Content.ReadAsStreamAsync();
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions));
    }
}
