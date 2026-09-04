namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
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
        var record = CreateRecord("Filesystem", enabled: false);
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
        AssertEx.Equal(expected: 2, body.Items.Count);
    }

    [Test]
    public async Task ListServers_MasksEveryEnvironmentValue_AndCarriesTheTrustTier()
    {
        // Encryption at rest only helps against someone holding the FILE. Returning the plaintext to anything holding
        // a session made the column's AEAD decorative, so the response carries the keys and a fixed placeholder.
        var service = Substitute.For<IMcpServerService>();
        var record = CreateRecord("Filesystem", enabled: true) with
        {
            TrustTier = McpTrustTier.PrivilegedHost,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["API_TOKEN"] = "the-real-secret"
            }
        };
        service.ListAsync(Arg.Any<CancellationToken>()).Returns([record]);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, ServersRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var body = AssertEx.NotNull(JsonSerializer.Deserialize<ListMcpServersResponse>(raw, JsonOptions));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = body.Items.Single();
        AssertEx.Contains(item.Env.Keys, "API_TOKEN");
        AssertEx.Equal(McpServerResponse.MaskedEnvironmentValue, item.Env["API_TOKEN"]);
        AssertEx.Equal(McpTrustTier.PrivilegedHost, item.TrustTier);
        // Assert on the WIRE, not only on the deserialized shape: the point is that the secret is not in the bytes.
        AssertEx.False(raw.Contains("the-real-secret", StringComparison.Ordinal),
            "the stored environment value must not appear anywhere in the response body");
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
        service.SetEnabledAsync(id, enabled: true, Arg.Any<CancellationToken>()).Returns(CreateRecord("Filesystem", enabled: true) with
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
        await service.Received(1).SetEnabledAsync(id, enabled: true, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task GetToolCatalog_WhenAuthorized_ReturnsBuiltInAndMcpTools()
    {
        var service = Substitute.For<IMcpServerService>();
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetKnownToolsAsync(Arg.Any<CancellationToken>()).Returns([
            new LocalToolCatalogEntry
            {
                Name = "GetCurrentTime",
                Description = "Returns the time.",
                RequiresApproval = false,
                Source = "builtin",
                Category = ToolCategory.ReadLocal
            },
            new LocalToolCatalogEntry
            {
                Name = "mcp__filesystem__read_file",
                Description = "Reads a file.",
                RequiresApproval = true,
                Source = "mcp:filesystem",
                Category = ToolCategory.Network
            }
        ]);
        await using var factory = CreateFactory(service, offerProvider);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, ToolCatalogRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await ReadJsonAsync<ToolCatalogResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        // Each entry carries its ToolCategory name and the effective approval computed through the node policy. With no
        // node-default tightening configured the policy is identity on the catalog default: the read-only built-in stays
        // auto-execute, and the always-approval MCP tool stays approval-requiring.
        AssertEx.ContainsSingle(body.Tools,
            tool => tool.Name == "GetCurrentTime" && tool.Source == "builtin" && !tool.RequiresApproval
                    && tool.Category == "ReadLocal" && !tool.EffectiveRequiresApproval);
        AssertEx.ContainsSingle(body.Tools,
            tool => tool.Name == "mcp__filesystem__read_file" && tool.Source == "mcp:filesystem" && tool.RequiresApproval
                    && tool.Category == "Network" && tool.EffectiveRequiresApproval);
        // Neither a built-in nor an MCP tool can carry a session-scoped approval, so the catalog says so and the chat
        // card withholds its "Approve for this session" button rather than promising a decision the node downgrades.
        AssertEx.True(body.Tools.All(tool => !tool.SessionScopeEligible),
            "Only the skill tools and Fixed custom tools are session-scope eligible.");
    }

    [Test]
    public async Task GetToolCatalog_MarksAFixedCustomToolSessionScopeEligible_AndAParameterizedOneNot()
    {
        // The one boolean the chat approval card keys off. It is computed through the SAME predicate the invocation
        // runner's session memo uses (SessionApprovalEligibility), so the card cannot offer a durable decision the node
        // would silently treat as one-shot. Fixed = a verbatim, operator-authored invocation; Parameterized = the model
        // chooses the arguments, so every argument set must re-prompt.
        var service = Substitute.For<IMcpServerService>();
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetKnownToolsAsync(Arg.Any<CancellationToken>()).Returns([
            new LocalToolCatalogEntry
            {
                Name = "custom__nightly_backup",
                Description = "Runs the nightly backup.",
                RequiresApproval = true,
                Source = "custom",
                Category = ToolCategory.WriteExecute,
                IsFixedCustomTool = true
            },
            new LocalToolCatalogEntry
            {
                Name = "custom__fetch_url",
                Description = "Fetches a URL the model picks.",
                RequiresApproval = true,
                Source = "custom",
                Category = ToolCategory.Network,
                IsFixedCustomTool = false
            }
        ]);
        await using var factory = CreateFactory(service, offerProvider);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, ToolCatalogRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await ReadJsonAsync<ToolCatalogResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.ContainsSingle(body.Tools, tool => tool.Name == "custom__nightly_backup" && tool.SessionScopeEligible);
        AssertEx.ContainsSingle(body.Tools, tool => tool.Name == "custom__fetch_url" && !tool.SessionScopeEligible);
    }

    [Test]
    public async Task GetToolCatalog_SeparatesAskUserFromTheToolsThatFailAnUnattendedRun()
    {
        // effectiveRequiresApproval is TRUE for both ask_user and an ordinary approval-gated tool, but only the latter
        // ends an unattended run: ToolApprovalCoordinator.RequestToolApprovalAsync throws ApprovalUnavailableException,
        // while RequestUserAnswerAsync skips the park and lets the turn continue unanswered. A warning driven off the
        // approval flag alone therefore names a tool that would not actually fail the run, which is what this splits.
        var service = Substitute.For<IMcpServerService>();
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetKnownToolsAsync(Arg.Any<CancellationToken>()).Returns([
            new LocalToolCatalogEntry
            {
                Name = AskUserTool.ToolName,
                Description = AskUserTool.Description,
                RequiresApproval = true,
                Source = "builtin",
                Category = ToolCategory.ReadLocal
            },
            new LocalToolCatalogEntry
            {
                Name = "custom__fetch_url",
                Description = "Fetches a URL the model picks.",
                RequiresApproval = true,
                Source = "custom",
                Category = ToolCategory.Network
            },
            new LocalToolCatalogEntry
            {
                Name = "GetCurrentTime",
                Description = "Returns the time.",
                RequiresApproval = false,
                Source = "builtin",
                Category = ToolCategory.ReadLocal
            }
        ]);
        await using var factory = CreateFactory(service, offerProvider);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, ToolCatalogRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await ReadJsonAsync<ToolCatalogResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.ContainsSingle(body.Tools,
            tool => tool.Name == AskUserTool.ToolName
                    && tool.EffectiveRequiresApproval
                    && tool.UnattendedBehaviour == ToolUnattendedBehaviourValues.ContinuesUnanswered);
        AssertEx.ContainsSingle(body.Tools,
            tool => tool.Name == "custom__fetch_url"
                    && tool.EffectiveRequiresApproval
                    && tool.UnattendedBehaviour == ToolUnattendedBehaviourValues.Fails);
        AssertEx.ContainsSingle(body.Tools,
            tool => tool.Name == "GetCurrentTime"
                    && !tool.EffectiveRequiresApproval
                    && tool.UnattendedBehaviour == ToolUnattendedBehaviourValues.Runs);
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
        AssertEx.Equal(expected: 2, body.Tools.Count);
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

    private static TestServerWebAppFactory CreateFactory(IMcpServerService service, ILocalToolOfferProvider? offerProvider = null)
    {
        return new TestServerWebAppFactory
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

    private static HttpRequestMessage CreateRequest(TestServerWebAppFactory factory, HttpMethod method, string uri)
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
            WorkingDirectory: null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            Url: null,
            McpTrustTier.Sandboxed,
            enabled,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return AssertEx.NotNull(JsonSerializer.Deserialize<T>(body, JsonOptions));
    }
}
