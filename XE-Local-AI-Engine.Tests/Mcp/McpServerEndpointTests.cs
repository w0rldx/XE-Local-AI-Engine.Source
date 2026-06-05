namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class McpServerEndpointTests
{
    private const string ServersRoute = "/api/local/v1/mcp/servers";
    private const string ToolCatalogRoute = "/api/local/v1/tool-catalog";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    [Test]
    public async Task CreateServer_WhenAuthorized_ReturnsCreatedWithRecord()
    {
        var service = Substitute.For<IMcpServerService>();
        var record = CreateRecord(name: "Filesystem", enabled: false);
        service.CreateAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).Returns(record);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, ServersRoute);
        request.Content = JsonContent.Create(new
        {
            name = "Filesystem",
            transportKind = "Stdio",
            command = "npx",
            arguments = new[]
            {
                "-y",
                "server-filesystem"
            },
            env = new Dictionary<string, string>()
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await ReadJsonAsync<McpServerResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertEx.Equal("Filesystem", body.Name);
        AssertEx.Equal("Stdio", body.TransportKind.ToString());
        AssertEx.False(body.Enabled, "A new registration is reported disabled.");
        await service.Received(1).CreateAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateServer_WhenValidationFails_ReturnsBadRequest()
    {
        var service = Substitute.For<IMcpServerService>();
        service.CreateAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>())
               .Returns<McpServerRecord>(_ => throw new McpServerValidationException("Command is required for a stdio MCP server."));
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, ServersRoute);
        request.Content = JsonContent.Create(new
        {
            name = "BadStdio",
            transportKind = "Stdio",
            env = new Dictionary<string, string>()
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task ListServers_WhenAuthorized_ReturnsItems()
    {
        var service = Substitute.For<IMcpServerService>();
        service.ListAsync(Arg.Any<CancellationToken>()).Returns([CreateRecord("Filesystem", enabled: true), CreateRecord("Remote", enabled: false)]);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, ServersRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await ReadJsonAsync<ListMcpServersResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(2, body.Items.Count);
    }

    [Test]
    public async Task GetServer_WhenMissing_ReturnsNotFound()
    {
        var service = Substitute.For<IMcpServerService>();
        var id = Guid.NewGuid();
        service.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((McpServerRecord?)null);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"{ServersRoute}/{id}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task DeleteServer_WhenDeleted_ReturnsNoContent()
    {
        var service = Substitute.For<IMcpServerService>();
        var id = Guid.NewGuid();
        service.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(true);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Delete, $"{ServersRoute}/{id}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Test]
    public async Task SetEnabled_WhenAuthorized_TogglesAndReturnsRecord()
    {
        var service = Substitute.For<IMcpServerService>();
        var id = Guid.NewGuid();
        service.SetEnabledAsync(id, true, Arg.Any<CancellationToken>()).Returns(CreateRecord("Filesystem", enabled: true) with
        {
            Id = id
        });
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Patch, $"{ServersRoute}/{id}/enabled");
        request.Content = JsonContent.Create(new
        {
            enabled = true
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await ReadJsonAsync<McpServerResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(body.Enabled, "Enabling returns the toggled record.");
        await service.Received(1).SetEnabledAsync(id, true, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task GetToolCatalog_WhenAuthorized_ReturnsBuiltInAndMcpTools()
    {
        var service = Substitute.For<IMcpServerService>();
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetKnownTools().Returns([
            new LocalToolCatalogEntry
            {
                Name = "GetCurrentTime",
                Description = "Returns the time.",
                RequiresApproval = false,
                Source = "builtin"
            },
            new LocalToolCatalogEntry
            {
                Name = "mcp__filesystem__read_file",
                Description = "Reads a file.",
                RequiresApproval = true,
                Source = "mcp:filesystem"
            }
        ]);
        await using var factory = CreateFactory(service, offerProvider);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, ToolCatalogRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await ReadJsonAsync<ToolCatalogResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.ContainsSingle(body.Tools, tool => tool.Name == "GetCurrentTime" && tool.Source == "builtin" && !tool.RequiresApproval);
        AssertEx.ContainsSingle(body.Tools, tool => tool.Name == "mcp__filesystem__read_file" && tool.Source == "mcp:filesystem" && tool.RequiresApproval);
    }

    [Test]
    public async Task GetServerTools_WhenServerDisabled_ReturnsDisabledStatus()
    {
        var service = Substitute.For<IMcpServerService>();
        var id = Guid.NewGuid();
        service.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(CreateRecord("Filesystem", enabled: false) with
        {
            Id = id
        });
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"{ServersRoute}/{id}/tools");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await ReadJsonAsync<McpServerToolsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("disabled", body.Status);
        AssertEx.Empty(body.Tools);
    }

    [Test]
    public async Task GetServerTools_WhenConnected_ReturnsConnectedStatus()
    {
        var service = Substitute.For<IMcpServerService>();
        var id = Guid.NewGuid();
        service.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(CreateRecord("Filesystem", enabled: true) with
        {
            Id = id
        });
        service.GetConnectionStatuses().Returns([
            new McpServerConnectionStatus
            {
                ServerId = id,
                Name = "Filesystem",
                Connected = true,
                ToolCount = 2,
                LastError = null,
                Tools =
                [
                    new McpServerToolInfo
                    {
                        Name = "mcp__filesystem__read_file",
                        Description = "Reads a file.",
                        RequiresApproval = true
                    },
                    new McpServerToolInfo
                    {
                        Name = "mcp__filesystem__write_file",
                        Description = "Writes a file.",
                        RequiresApproval = true
                    }
                ]
            }
        ]);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"{ServersRoute}/{id}/tools");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await ReadJsonAsync<McpServerToolsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("connected", body.Status);
        AssertEx.Null(body.Error);
        AssertEx.Equal(2, body.Tools.Count);
        AssertEx.ContainsSingle(body.Tools, tool => tool.Name == "mcp__filesystem__read_file" && tool.RequiresApproval);
    }

    [Test]
    public async Task GetServerTools_WhenEnabledButNotConnected_ReturnsErrorStatus()
    {
        var service = Substitute.For<IMcpServerService>();
        var id = Guid.NewGuid();
        service.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(CreateRecord("Filesystem", enabled: true) with
        {
            Id = id
        });
        service.GetConnectionStatuses().Returns([
            new McpServerConnectionStatus
            {
                ServerId = id,
                Name = "Filesystem",
                Connected = false,
                ToolCount = 0,
                LastError = "connect timed out",
                Tools = []
            }
        ]);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"{ServersRoute}/{id}/tools");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await ReadJsonAsync<McpServerToolsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("error", body.Status);
        AssertEx.Equal("connect timed out", body.Error);
    }

    [Test]
    public async Task GetServerTools_WhenEnabledWithNoStatusEntryYet_ReturnsConnectingStatus()
    {
        // The server is enabled but the connection manager has not produced a status for it yet (a startup refresh is
        // still in flight). That is a healthy not-yet-connected state, not a hard failure, so it reports "connecting".
        var service = Substitute.For<IMcpServerService>();
        var id = Guid.NewGuid();
        service.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(CreateRecord("Filesystem", enabled: true) with
        {
            Id = id
        });
        service.GetConnectionStatuses().Returns([]);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"{ServersRoute}/{id}/tools");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await ReadJsonAsync<McpServerToolsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("connecting", body.Status);
        AssertEx.Null(body.Error);
        AssertEx.Empty(body.Tools);
    }

    [Test]
    public async Task GetServerTools_WhenEnabledNotConnectedWithoutRecordedError_ReturnsConnectingStatus()
    {
        // A status entry exists but the server is not connected and no error was recorded — still "connecting", not
        // "error". "error" is reserved for an actually recorded failure (a non-empty LastError).
        var service = Substitute.For<IMcpServerService>();
        var id = Guid.NewGuid();
        service.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(CreateRecord("Filesystem", enabled: true) with
        {
            Id = id
        });
        service.GetConnectionStatuses().Returns([
            new McpServerConnectionStatus
            {
                ServerId = id,
                Name = "Filesystem",
                Connected = false,
                ToolCount = 0,
                LastError = null,
                Tools = []
            }
        ]);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"{ServersRoute}/{id}/tools");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await ReadJsonAsync<McpServerToolsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("connecting", body.Status);
        AssertEx.Null(body.Error);
    }

    [Test]
    public async Task GetServerTools_WhenServerMissing_ReturnsNotFound()
    {
        var service = Substitute.For<IMcpServerService>();
        var id = Guid.NewGuid();
        service.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((McpServerRecord?)null);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"{ServersRoute}/{id}/tools");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task ListServers_WhenMissingBearerToken_ReturnsUnauthorized()
    {
        var service = Substitute.For<IMcpServerService>();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ServersRoute).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await service.DidNotReceive().ListAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    private static TestingWebAppFactory CreateFactory(IMcpServerService service, ILocalToolOfferProvider? offerProvider = null)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IMcpServerService>();
                services.AddScoped(_ => service);

                if (offerProvider is not null)
                {
                    services.RemoveAll<ILocalToolOfferProvider>();
                    services.AddSingleton(offerProvider);
                }
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

    private static McpServerRecord CreateRecord(string name, bool enabled)
    {
        return new McpServerRecord(Guid.NewGuid(),
            name,
            "A server.",
            McpTransportKind.Stdio,
            "npx",
            ["-y", "server"],
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            enabled,
            1,
            10,
            10);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return AssertEx.NotNull(JsonSerializer.Deserialize<T>(body, JsonOptions));
    }
}
