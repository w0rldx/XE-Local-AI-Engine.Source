namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The authentication boundary of the INBOUND MCP surface. Two separate principals meet here and must not be able
///     to impersonate each other: the node operator (JWT, drives the key-management endpoints) and an external MCP
///     client (bearer API key, drives the MCP endpoint). Each is rejected on the other's surface.
/// </summary>
public sealed class McpServerInboundAuthTests
{
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

    private static StringContent EmptyRpcContent()
    {
        return new StringContent("{}", Encoding.UTF8, "application/json");
    }

    private static TestingWebAppFactory CreateFactory(string? storedKey)
    {
        var apiKeyService = Substitute.For<IMcpServerApiKeyService>();
        apiKeyService.ValidateAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                     .Returns(call => storedKey is not null && string.Equals(call.Arg<string?>(), storedKey, StringComparison.Ordinal));
        // The view deliberately has no key field — the node keeps only a digest — so the fake supplies metadata only.
        apiKeyService.GetAsync(Arg.Any<CancellationToken>())
                     .Returns(storedKey is null
                         ? (McpServerApiKeyView?)null
                         : new McpServerApiKeyView("xemcp_valid", DateTimeOffset.UnixEpoch, LastUsedAt: null));

        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IMcpServerApiKeyService>();
                services.AddScoped(_ => apiKeyService);
            }
        };
    }

    private sealed record KeyStatusBody(bool Configured, string EndpointUrl);
}
