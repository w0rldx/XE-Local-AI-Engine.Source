namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The authentication boundary of the INBOUND MCP surface. Two separate principals meet here and must not be able
///     to impersonate each other: the node operator (JWT, drives the key-management endpoints) and an external MCP
///     client (bearer API key, drives the MCP endpoint). Each is rejected on the other's surface.
/// </summary>
public sealed class McpServerInboundAuthTests
{
    private static readonly string[] AgenticAdminToolNames =
    [
        "cancel_model_pull", "create_agent", "delete_agent", "delete_model", "get_agent", "get_model_pull",
        "get_node_settings", "get_runtime_acquisition", "get_runtime_status", "get_status", "get_workflow_run",
        "list_workflow_runs", "set_default_model", "start_model_pull", "start_runtime_acquisition", "update_agent",
        "update_node_settings"
    ];

    private const string McpEndpointRoute = "/api/local/v1/mcp/server";
    private const string KeyManagementRoute = "/api/local/v1/mcp/server-key";
    private const string ValidKey = "xemcp_valid-test-key";

    [Test]
    public async Task McpEndpoint_WithoutAnyCredential_IsUnauthorized()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var content = EmptyRpcContent();
        using var response = await client.PostAsync(new Uri(McpEndpointRoute, UriKind.Relative), content).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task McpEndpoint_WithoutAnyCredential_EmitsABearerChallenge()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var content = EmptyRpcContent();
        using var response = await client.PostAsync(new Uri(McpEndpointRoute, UriKind.Relative), content).ConfigureAwait(false);

        AssertEx.True(response.Headers.WwwAuthenticate.Any(static header => string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)),
            "A 401 from the MCP endpoint must carry an RFC 6750 Bearer challenge so a client knows how to authenticate.");
    }

    [Test]
    public async Task McpEndpoint_WithAWrongKey_IsUnauthorized()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, McpEndpointRoute)
        {
            Content = EmptyRpcContent()
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "xemcp_wrong-key");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task McpEndpoint_WithTheOperatorJwt_IsStillUnauthorized()
    {
        // The whole point of giving the MCP endpoint its own scheme: an operator token (or a stolen browser session)
        // must not be a way to drive the MCP tool surface.
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, McpEndpointRoute)
        {
            Content = EmptyRpcContent()
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task McpEndpoint_WithTheCorrectKey_PassesAuthentication()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, McpEndpointRoute)
        {
            Content = EmptyRpcContent()
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ValidKey);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // The body is deliberately not a valid JSON-RPC call, so the transport may answer 4xx/2xx — what matters is
        // that it is NOT 401/403, i.e. the credential was accepted and the request reached the MCP transport.
        AssertEx.True(response.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden,
            $"A correct API key must pass authentication; got {response.StatusCode}.");
    }

    [Test]
    public async Task Authentication_EmitsTrustedScopeAndBoundedPrefixClaims()
    {
        await using var factory = CreateFactory(ValidKey, McpServerApiKeyScope.Agentic);
        var context = new DefaultHttpContext
        {
            RequestServices = factory.Services
        };
        context.Request.Headers.Authorization = $"Bearer {ValidKey}";

        var result = await context.AuthenticateAsync(McpApiKeyAuthenticationHandler.SchemeName).ConfigureAwait(false);

        AssertEx.True(result.Succeeded, "A valid key must produce an authenticated MCP principal.");
        var principal = AssertEx.NotNull(result.Principal);
        AssertEx.Equal("agentic", principal.FindFirst(NodeAuthorizationPolicies.McpScopeClaimType)?.Value);
        AssertEx.Equal("xemcp_valid", principal.FindFirst(NodeAuthorizationPolicies.McpKeyPrefixClaimType)?.Value);
        AssertEx.False(principal.IsInRole(NodeAuthorizationPolicies.AdminRole), "An agentic MCP principal must never inherit the browser Operator role.");
    }

    [Test]
    public async Task McpAgenticPolicy_DistinguishesInvalidDelegateAndAgenticCredentials()
    {
        await using var delegateFactory = CreateFactory(ValidKey, McpServerApiKeyScope.Delegate);
        var delegateContext = new DefaultHttpContext
        {
            RequestServices = delegateFactory.Services
        };
        delegateContext.Request.Headers.Authorization = $"Bearer {ValidKey}";
        var delegateAuthentication = await delegateContext.AuthenticateAsync(McpApiKeyAuthenticationHandler.SchemeName).ConfigureAwait(false);
        var authorization = delegateFactory.Services.GetRequiredService<IAuthorizationService>();
        var delegateAuthorization = await authorization.AuthorizeAsync(AssertEx.NotNull(delegateAuthentication.Principal),
            resource: null,
            NodeAuthorizationPolicies.McpAgentic).ConfigureAwait(false);

        AssertEx.True(delegateAuthentication.Succeeded, "A delegate key is authenticated, so an agentic endpoint rejects it as forbidden rather than unauthorized.");
        AssertEx.False(delegateAuthorization.Succeeded, "A delegate principal must not satisfy the agentic policy.");

        await using var agenticFactory = CreateFactory(ValidKey, McpServerApiKeyScope.Agentic);
        var agenticContext = new DefaultHttpContext
        {
            RequestServices = agenticFactory.Services
        };
        agenticContext.Request.Headers.Authorization = $"Bearer {ValidKey}";
        var agenticAuthentication = await agenticContext.AuthenticateAsync(McpApiKeyAuthenticationHandler.SchemeName).ConfigureAwait(false);
        var agenticAuthorization = await agenticFactory.Services.GetRequiredService<IAuthorizationService>()
                                                       .AuthorizeAsync(AssertEx.NotNull(agenticAuthentication.Principal),
                                                           resource: null,
                                                           NodeAuthorizationPolicies.McpAgentic)
                                                       .ConfigureAwait(false);

        AssertEx.True(agenticAuthorization.Succeeded, "Only the exact agentic scope claim may satisfy the policy.");
    }

    [Test]
    public async Task McpEndpoint_ToolsList_AdvertisesExactlyEightReadOnlyToolsWithoutHostPathFields()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, McpEndpointRoute)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ValidKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var toolNames = Regex.Matches(body, "\\\"name\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"")
                             .Select(static match => match.Groups[1].Value)
                             .Where(static name => name is "list_agents"
                                 or "list_models"
                                 or "run_agent"
                                 or "list_workspaces"
                                 or "start_agent_run"
                                 or "get_agent_run"
                                 or "cancel_agent_run"
                                 or "list_agent_runs")
                             .OrderBy(static name => name, StringComparer.Ordinal)
                             .ToArray();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(8, toolNames.Length);
        AssertEx.Equal("cancel_agent_run|get_agent_run|list_agent_runs|list_agents|list_models|list_workspaces|run_agent|start_agent_run",
            string.Join('|', toolNames));
        AssertEx.Contains(body, "\"workspace_id\"");
        AssertEx.False(body.Contains("hostPath", StringComparison.OrdinalIgnoreCase), "MCP schema must not advertise host paths.");
        AssertEx.False(body.Contains("host_path", StringComparison.OrdinalIgnoreCase), "MCP schema must not advertise host paths.");
        AssertEx.False(body.Contains("write_file", StringComparison.OrdinalIgnoreCase), "MCP discovery must not advertise filesystem mutation.");
        AssertEx.False(body.Contains("execute_command", StringComparison.OrdinalIgnoreCase), "MCP discovery must not advertise process execution.");
    }

    [Test]
    public async Task McpEndpoint_ToolsList_FiltersAdministrationToolsByRealScopedPrincipal()
    {
        await using var delegateFactory = CreateFactory(ValidKey, McpServerApiKeyScope.Delegate);
        var delegateBody = await SendRpcAsync(delegateFactory,
            ValidKey,
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}").ConfigureAwait(false);
        var delegateNames = ExtractToolNames(delegateBody);

        await using var agenticFactory = CreateFactory(ValidKey, McpServerApiKeyScope.Agentic);
        var agenticBody = await SendRpcAsync(agenticFactory,
            ValidKey,
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}").ConfigureAwait(false);
        var agenticNames = ExtractToolNames(agenticBody);

        AssertEx.Equal("cancel_agent_run|get_agent_run|list_agent_runs|list_agents|list_models|list_workspaces|run_agent|start_agent_run",
            string.Join('|', delegateNames));
        AssertEx.Equal(25, agenticNames.Length);
        AssertEx.Equal(string.Join('|', AgenticAdminToolNames),
            string.Join('|', agenticNames.Intersect(AgenticAdminToolNames, StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal)));
    }

    [Test]
    public async Task McpEndpoint_DelegateDirectAdminCall_IsProtocolRejectedWhileAgenticCallSucceeds()
    {
        const string call = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get_status\",\"arguments\":{}}}";
        await using var delegateFactory = CreateFactory(ValidKey, McpServerApiKeyScope.Delegate);
        var delegateBody = await SendRpcAsync(delegateFactory, ValidKey, call).ConfigureAwait(false);
        AssertEx.True(delegateBody.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                      || delegateBody.Contains("-32600", StringComparison.Ordinal)
                      || delegateBody.Contains("InvalidRequest", StringComparison.OrdinalIgnoreCase),
            $"A delegate direct admin call must be a protocol authorization failure; body: {delegateBody}");

        await using var agenticFactory = CreateFactory(ValidKey, McpServerApiKeyScope.Agentic);
        var agenticBody = await SendRpcAsync(agenticFactory, ValidKey, call).ConfigureAwait(false);
        AssertEx.Contains(agenticBody, "loadedProcessCount");
        AssertEx.False(agenticBody.Contains("forbidden", StringComparison.OrdinalIgnoreCase));

        // And the same for the observe tools: read-only is not delegate-visible, because what a workflow run is doing
        // is exactly the kind of thing an outside delegate key has no business polling.
        const string observeCall = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"list_workflow_runs\",\"arguments\":{}}}";
        var delegateObserveBody = await SendRpcAsync(delegateFactory, ValidKey, observeCall).ConfigureAwait(false);
        AssertEx.True(delegateObserveBody.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                      || delegateObserveBody.Contains("-32600", StringComparison.Ordinal)
                      || delegateObserveBody.Contains("InvalidRequest", StringComparison.OrdinalIgnoreCase),
            $"A delegate call to a workflow observe tool must be a protocol authorization failure; body: {delegateObserveBody}");
    }

    [Test]
    public async Task RealScopedKeyStoreLifecycle_FiltersAndAuthorizesTheRegisteredToolSurface()
    {
        const string list = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}";
        const string call = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get_status\",\"arguments\":{}}}";
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        var agentic = await GenerateRealKeyAsync(factory, client, "agentic").ConfigureAwait(false);
        var agenticNames = ExtractToolNames(await SendRpcAsync(factory, agentic.Key, list).ConfigureAwait(false));
        var agenticCall = await SendRpcAsync(factory, agentic.Key, call).ConfigureAwait(false);
        AssertEx.Equal(25, agenticNames.Length);
        AssertEx.Contains(agenticCall, "loadedProcessCount");

        var delegateKey = await GenerateRealKeyAsync(factory, client, "delegate").ConfigureAwait(false);
        var delegateNames = ExtractToolNames(await SendRpcAsync(factory, delegateKey.Key, list).ConfigureAwait(false));
        var delegateCall = await SendRpcAsync(factory, delegateKey.Key, call).ConfigureAwait(false);
        AssertEx.Equal(8, delegateNames.Length);
        AssertEx.False(delegateNames.Contains("get_status", StringComparer.Ordinal));
        AssertEx.True(delegateCall.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                      || delegateCall.Contains("-32600", StringComparison.Ordinal)
                      || delegateCall.Contains("InvalidRequest", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task KeyManagementEndpoints_WithoutTheOperatorJwt_AreUnauthorized()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var getResponse = await client.GetAsync(new Uri(KeyManagementRoute, UriKind.Relative)).ConfigureAwait(false);
        using var deleteResponse = await client.DeleteAsync(new Uri(KeyManagementRoute, UriKind.Relative)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, getResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, deleteResponse.StatusCode);
    }

    [Test]
    public async Task KeyManagementEndpoints_WithTheMcpApiKey_AreStillUnauthorized()
    {
        // The reverse direction: an MCP client must not be able to read or rotate the very credential that admits it.
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, KeyManagementRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ValidKey);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task GetKey_WhenOperatorAuthenticatedAndNoKeyExists_ReportsNotConfiguredWithTheEndpointUrl()
    {
        await using var factory = CreateFactory(storedKey: null);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, KeyManagementRoute);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<KeyStatusBody>().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(body.Configured, "An ungenerated key must report configured=false rather than 404.");
        AssertEx.True(body.EndpointUrl.EndsWith(McpEndpointRoute, StringComparison.Ordinal),
            $"The advertised endpoint URL must point at the MCP route inside the loopback-gated prefix; got '{body.EndpointUrl}'.");
    }

    [Test]
    [Arguments("agentic", McpServerApiKeyScope.Agentic)]
    [Arguments("delegate", McpServerApiKeyScope.Delegate)]
    public async Task GenerateKey_WithExplicitScope_RotatesAndReturnsThatScope(string requestedScope, McpServerApiKeyScope expectedScope)
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, KeyManagementRoute)
        {
            Content = JsonContent.Create(new
            {
                scope = requestedScope
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<GeneratedKeyBody>().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expectedScope == McpServerApiKeyScope.Agentic ? "agentic" : "delegate", body.ApiKey.Scope);
        AssertEx.Equal(ValidKey, body.Key);
    }

    [Test]
    public async Task GenerateKey_WithNoRequestBody_PreservesLegacyDelegateBehavior()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, KeyManagementRoute);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.True(response.StatusCode == HttpStatusCode.OK,
            $"A legacy bodyless POST must generate a delegate key; got {(int)response.StatusCode} {response.StatusCode}: {responseText}");
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<GeneratedKeyBody>().ConfigureAwait(false));

        AssertEx.Equal("delegate", body.ApiKey.Scope);
    }

    [Test]
    public async Task GenerateKey_WithEmptyJsonContent_PreservesGeneratedClientDelegateBehavior()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, KeyManagementRoute)
        {
            Content = new ByteArrayContent([])
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<GeneratedKeyBody>().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("delegate", body.ApiKey.Scope);
    }

    [Test]
    public async Task GenerateKey_WithPresentNonJsonBody_IsRejectedAsUnsupportedMediaType()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, KeyManagementRoute)
        {
            Content = new StringContent("scope=agentic", Encoding.UTF8, "text/plain")
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        AssertEx.True(!string.IsNullOrWhiteSpace(responseBody), "Unsupported content must return an explicit validation response, not an empty server failure.");
    }

    [Test]
    public async Task OpenApi_DescribesScopedGenerationBodyAsOptionalApplicationContract()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/openapi/local/v1/v1.json", UriKind.Relative)).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
        var requestBody = document.RootElement.GetProperty("paths")
                                  .GetProperty(KeyManagementRoute)
                                  .GetProperty("post")
                                  .GetProperty("requestBody");
        var schemaText = requestBody.GetRawText();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(requestBody.TryGetProperty("required", out var required) && required.GetBoolean(),
            "The request body must remain optional for existing bodyless callers.");
        AssertEx.Contains(schemaText, "XE_Local_AI_EngineClientServicesMcpMcpServerApiKeyScope");
        AssertEx.False(schemaText.Contains("PersistenceStoresMcpServerApiKeyScope", StringComparison.Ordinal),
            "The public contract must not expose a Persistence namespace type.");
    }

    [Test]
    public async Task RealScopedKeyLifecycle_RoundTripsThroughStoreServiceAuthenticationAndPolicy()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        var agentic = await GenerateRealKeyAsync(factory, client, "agentic").ConfigureAwait(false);
        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, KeyManagementRoute);
        factory.AddNodeBearerToken(statusRequest);
        statusRequest.Headers.Add("Origin", "http://localhost");
        using var statusResponse = await client.SendAsync(statusRequest).ConfigureAwait(false);
        var status = AssertEx.NotNull(await statusResponse.Content.ReadFromJsonAsync<KeyStatusBody>().ConfigureAwait(false));
        AssertEx.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        AssertEx.Equal("agentic", AssertEx.NotNull(status.ApiKey).Scope);

        var agenticAuthentication = await AuthenticateAsync(factory, agentic.Key).ConfigureAwait(false);
        AssertEx.True(agenticAuthentication.Succeeded);
        await using var authorizationScope = factory.Services.CreateAsyncScope();
        var authorization = authorizationScope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        AssertEx.True((await authorization.AuthorizeAsync(AssertEx.NotNull(agenticAuthentication.Principal),
            resource: null,
            NodeAuthorizationPolicies.McpAgentic).ConfigureAwait(false)).Succeeded);

        var delegateKey = await GenerateRealKeyAsync(factory, client, "delegate").ConfigureAwait(false);
        var rotatedAuthentication = await AuthenticateAsync(factory, agentic.Key).ConfigureAwait(false);
        AssertEx.False(rotatedAuthentication.Succeeded, "Rotation must immediately invalidate the previous agentic key.");

        var delegateAuthentication = await AuthenticateAsync(factory, delegateKey.Key).ConfigureAwait(false);
        AssertEx.True(delegateAuthentication.Succeeded);
        AssertEx.False((await authorization.AuthorizeAsync(AssertEx.NotNull(delegateAuthentication.Principal),
                resource: null,
                NodeAuthorizationPolicies.McpAgentic).ConfigureAwait(false)).Succeeded,
            "A valid delegate key must authenticate but remain forbidden by the agentic policy.");
    }

    [Test]
    [Arguments("owner")]
    [Arguments("0")]
    public async Task GenerateKey_WithAnUnsupportedScope_IsRejected(string rawScope)
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, KeyManagementRoute)
        {
            Content = new StringContent($"{{\"scope\":{(rawScope == "0" ? rawScope : $"\"{rawScope}\"")}}}", Encoding.UTF8, "application/json")
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static StringContent EmptyRpcContent()
    {
        return new StringContent("{}", Encoding.UTF8, "application/json");
    }

    private static string[] ExtractToolNames(string body) =>
        Regex.Matches(body, "\\\"name\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"")
             .Select(static match => match.Groups[1].Value)
             .Where(static name => name is not "xe-local-ai-engine")
             .Distinct(StringComparer.Ordinal)
             .OrderBy(static name => name, StringComparer.Ordinal)
             .ToArray();

    private static async Task<string> SendRpcAsync(TestServerWebAppFactory factory, string key, string body)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, McpEndpointRoute)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return responseBody;
    }

    private static async Task<GeneratedKeyBody> GenerateRealKeyAsync(TestServerWebAppFactory factory, HttpClient client, string scope)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, KeyManagementRoute)
        {
            Content = JsonContent.Create(new
            {
                scope
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return AssertEx.NotNull(await response.Content.ReadFromJsonAsync<GeneratedKeyBody>().ConfigureAwait(false));
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(TestServerWebAppFactory factory, string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider
        };
        context.Request.Headers.Authorization = $"Bearer {key}";
        return await context.AuthenticateAsync(McpApiKeyAuthenticationHandler.SchemeName).ConfigureAwait(false);
    }

    private static TestServerWebAppFactory CreateFactory(string? storedKey,
        McpServerApiKeyScope scope = McpServerApiKeyScope.Delegate)
    {
        var apiKeyService = Substitute.For<IMcpServerApiKeyService>();
        apiKeyService.ValidateAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                     .Returns(call => storedKey is not null && string.Equals(call.Arg<string?>(), storedKey, StringComparison.Ordinal)
                         ? new McpServerApiKeyValidation(scope, "xemcp_valid")
                         : null);
        apiKeyService.GenerateAsync(Arg.Any<McpServerApiKeyScope>(), Arg.Any<CancellationToken>())
                     .Returns(call =>
                     {
                         var requestedScope = call.Arg<McpServerApiKeyScope>();
                         return new GeneratedMcpServerApiKey(ValidKey,
                             new McpServerApiKeyView("xemcp_valid", requestedScope, DateTimeOffset.UnixEpoch, LastUsedAt: null));
                     });
        // The view deliberately has no key field — the node keeps only a digest — so the fake supplies metadata only.
        apiKeyService.GetAsync(Arg.Any<CancellationToken>())
                     .Returns(storedKey is null
                         ? (McpServerApiKeyView?)null
                         : new McpServerApiKeyView("xemcp_valid",
                             scope,
                             DateTimeOffset.UnixEpoch,
                             LastUsedAt: null));

        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IMcpServerApiKeyService>();
                services.AddScoped(_ => apiKeyService);
            }
        };
    }

    private sealed record KeyStatusBody(bool Configured, string EndpointUrl, KeyMetadataBody? ApiKey = null);

    private sealed record GeneratedKeyBody(string Key, KeyMetadataBody ApiKey);

    private sealed record KeyMetadataBody(string Scope);
}
