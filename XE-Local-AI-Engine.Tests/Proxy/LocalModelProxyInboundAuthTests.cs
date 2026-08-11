namespace XE_Local_AI_Engine.Tests.Proxy;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Proxy;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The authentication boundary of the inbound model proxy. Three principals meet on this node and must not
///     impersonate each other: the node operator (JWT, drives the key-management endpoints), the MCP client (its own
///     key, drives the MCP tool surface), and the model-proxy tool (its own key, drives the raw-model endpoints). Each
///     is rejected on the others' surface — in particular the proxy key must not reach admin, and the operator token
///     must not drive the raw-model proxy.
/// </summary>
public sealed class LocalModelProxyInboundAuthTests
{
    private const string ModelsRoute = "/api/local/v1/proxy/v1/models";
    private const string KeyManagementRoute = "/api/local/v1/proxy/key";
    private const string ProxyEndpointBase = "/api/local/v1/proxy/v1";
    private const string ValidKey = "xeprx_valid-test-key";

    [Test]
    public async Task ProxyEndpoint_WithoutAnyCredential_IsUnauthorized()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(ModelsRoute, UriKind.Relative)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ProxyEndpoint_WithoutAnyCredential_EmitsABearerChallenge()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(ModelsRoute, UriKind.Relative)).ConfigureAwait(false);

        AssertEx.True(response.Headers.WwwAuthenticate.Any(static header => string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)),
            "A 401 from the model proxy must carry an RFC 6750 Bearer challenge so a client knows how to authenticate.");
    }

    [Test]
    public async Task ProxyEndpoint_WithAWrongKey_IsUnauthorized()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "xeprx_wrong-key");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ProxyEndpoint_WithTheOperatorJwt_IsStillUnauthorized()
    {
        // The whole point of giving the proxy its own scheme: an operator token (or a stolen browser session) must not
        // be a way to drive the raw-model surface.
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ProxyEndpoint_WithTheCorrectKey_PassesAuthentication()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ValidKey);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // What matters is that the credential was accepted and the request reached the forwarder — not 401/403.
        AssertEx.True(response.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden,
            $"A correct proxy key must pass authentication; got {response.StatusCode}.");
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
    public async Task KeyManagementEndpoints_WithTheProxyApiKey_AreStillUnauthorized()
    {
        // The reverse direction: a proxy client must not be able to read or rotate the very credential that admits it.
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, KeyManagementRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ValidKey);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task GetKey_WhenOperatorAuthenticatedAndNoKeyExists_ReportsNotConfiguredWithTheOpenAiBaseUrl()
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
        AssertEx.True(body.EndpointUrl.EndsWith(ProxyEndpointBase, StringComparison.Ordinal),
            $"The advertised base URL must point at the OpenAI proxy base inside the loopback-gated prefix; got '{body.EndpointUrl}'.");
    }

    private static TestingWebAppFactory CreateFactory(string? storedKey)
    {
        var apiKeyService = Substitute.For<ILocalModelProxyApiKeyService>();
        apiKeyService.ValidateAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                     .Returns(call => storedKey is not null && string.Equals(call.Arg<string?>(), storedKey, StringComparison.Ordinal));
        apiKeyService.GetAsync(Arg.Any<CancellationToken>())
                     .Returns(storedKey is null
                         ? (LocalModelProxyApiKeyView?)null
                         : new LocalModelProxyApiKeyView("xeprx_valid", DateTimeOffset.UnixEpoch, LastUsedAt: null));

        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILocalModelProxyApiKeyService>();
                services.AddScoped(_ => apiKeyService);
            }
        };
    }

    private sealed record KeyStatusBody(bool Configured, string EndpointUrl);
}
