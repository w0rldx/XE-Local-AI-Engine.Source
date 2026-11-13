namespace XE_Local_AI_Engine.Tests.Proxy;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Proxy;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The inbound model proxy's authentication boundary: the operator (JWT, key management), the MCP client (own key,
///     MCP tools) and the model-proxy tool (own key, raw-model endpoints) must not impersonate each other.
/// </summary>
/// <remarks>
///     Each is rejected on the others' surfaces: the proxy key never reaches admin, the operator token never drives
///     the raw-model proxy.</remarks>
[Category(TestCategories.Integration)]
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

        using var response = await client.GetAsync(new Uri(ModelsRoute, UriKind.Relative));

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ProxyEndpoint_WithoutAnyCredential_EmitsABearerChallenge()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(ModelsRoute, UriKind.Relative));

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
        using var response = await client.SendAsync(request);

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
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ProxyEndpoint_WithTheCorrectKey_PassesAuthentication()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ValidKey);
        using var response = await client.SendAsync(request);

        // What matters is that the credential was accepted and the request reached the forwarder — not 401/403.
        AssertEx.True(response.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden,
            $"A correct proxy key must pass authentication; got {response.StatusCode}.");
    }

    /// <summary>
    ///     Every credential shape a client can present, on every proxy route: only the proxy key, in either header (or both, agreeing), passes.
    /// </summary>
    [Test]
    [Arguments("POST", "/api/local/v1/proxy/v1/messages")]
    [Arguments("POST", "/api/local/v1/proxy/v1/messages/count_tokens")]
    [Arguments("POST", "/api/local/v1/proxy/v1/responses")]
    [Arguments("POST", "/api/local/v1/proxy/v1/responses/input_tokens")]
    [Arguments("POST", "/api/local/v1/proxy/v1/chat/completions")]
    [Arguments("GET", ModelsRoute)]
    public async Task ProxyRoute_CredentialTable_OnlyTheProxyKeyInEitherHeaderPasses(string method, string route)
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();
        var operatorToken = factory.CreateNodeAccessToken();
        var operatorJwt = "Bearer " + operatorToken;

        CredentialCase[] cases =
        [
            new("no credential", null, null, false),
            new("empty x-api-key", null, "", false),
            new("wrong key as Bearer", "Bearer xeprx_wrong-key", null, false),
            new("wrong key as x-api-key", null, "xeprx_wrong-key", false),
            new("correct key as x-api-key", null, ValidKey, true),
            new("correct key as Bearer", "Bearer " + ValidKey, null, true),
            new("correct key in both", "Bearer " + ValidKey, ValidKey, true),
            new("correct Bearer, wrong x-api-key", "Bearer " + ValidKey, "xeprx_wrong-key", false),
            new("wrong Bearer, correct x-api-key", "Bearer xeprx_wrong-key", ValidKey, false),
            new("correct key under another scheme", "Basic " + ValidKey, null, false),
            new("another scheme beside a correct x-api-key", "Basic dXNlcjpwYXNz", ValidKey, false),
            new("operator JWT", operatorJwt, null, false),
            new("operator JWT as x-api-key", null, operatorToken, false),
            new("operator JWT beside a correct x-api-key", operatorJwt, ValidKey, false)
        ];

        // A POST that passes reaches the forwarder and gets its 404 for the uninstalled model; the GET lists models.
        var passed = method == "POST" ? HttpStatusCode.NotFound : HttpStatusCode.OK;
        var mismatches = new List<string>();
        foreach (var (name, authorization, apiKey, passes) in cases)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), route);
            if (method == "POST")
            {
                request.Content = new StringContent("{\"model\":\"not-installed\",\"messages\":[],\"input\":\"hi\"}", System.Text.Encoding.UTF8, "application/json");
            }

            if (authorization is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            if (apiKey is not null)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            }

            using var response = await client.SendAsync(request);
            var expected = passes ? passed : HttpStatusCode.Unauthorized;
            if (response.StatusCode != expected)
            {
                mismatches.Add($"{name}: expected {expected}, got {response.StatusCode}");
            }
        }

        AssertEx.Empty(mismatches, $"{method} {route}: {string.Join(" | ", mismatches)}");
    }

    [Test]
    public async Task KeyManagementEndpoints_WithTheProxyApiKeyAsXApiKey_AreStillUnauthorized()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, KeyManagementRoute);
        request.Headers.Add("x-api-key", ValidKey);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task KeyManagementEndpoints_WithoutTheOperatorJwt_AreUnauthorized()
    {
        await using var factory = CreateFactory(ValidKey);
        using var client = factory.CreateClient();

        using var getResponse = await client.GetAsync(new Uri(KeyManagementRoute, UriKind.Relative));
        using var deleteResponse = await client.DeleteAsync(new Uri(KeyManagementRoute, UriKind.Relative));

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
        using var response = await client.SendAsync(request);

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
        using var response = await client.SendAsync(request);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<KeyStatusBody>());

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(body.Configured, "An ungenerated key must report configured=false rather than 404.");
        AssertEx.True(body.EndpointUrl.EndsWith(ProxyEndpointBase, StringComparison.Ordinal),
            $"The advertised base URL must point at the OpenAI proxy base inside the loopback-gated prefix; got '{body.EndpointUrl}'.");
        // An Anthropic SDK appends /v1/messages itself, so its base is one segment shorter and lands on the same route.
        AssertEx.Equal(body.EndpointUrl[..^"/v1".Length], body.AnthropicEndpointUrl);
        AssertEx.True(body.AnthropicEndpointUrl.EndsWith("/api/local/v1/proxy", StringComparison.Ordinal),
            $"The Anthropic base plus /v1/messages must reach the proxy's messages route; got '{body.AnthropicEndpointUrl}'.");
    }

    private static TestServerWebAppFactory CreateFactory(string? storedKey)
    {
        var apiKeyService = Substitute.For<ILocalModelProxyApiKeyService>();
        apiKeyService.ValidateAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                     .Returns(call => storedKey is not null && string.Equals(call.Arg<string?>(), storedKey, StringComparison.Ordinal));
        apiKeyService.GetAsync(Arg.Any<CancellationToken>())
                     .Returns(storedKey is null
                         ? (LocalModelProxyApiKeyView?)null
                         : new LocalModelProxyApiKeyView
                         {
                             Prefix = "xeprx_valid",
                             CreatedAt = DateTimeOffset.UnixEpoch,
                             LastUsedAt = null
                         });

        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILocalModelProxyApiKeyService>();
                services.AddScoped(_ => apiKeyService);
            }
        };
    }

    private sealed record KeyStatusBody(bool Configured, string EndpointUrl, string AnthropicEndpointUrl);

    private sealed class CredentialCase
    {
        public CredentialCase(string name, string? authorization, string? apiKey, bool passes)
        {
            Name = name;
            Authorization = authorization;
            ApiKey = apiKey;
            Passes = passes;
        }

        public string Name { get; }

        public string? Authorization { get; }

        public string? ApiKey { get; }

        public bool Passes { get; }

        public void Deconstruct(out string name, out string? authorization, out string? apiKey, out bool passes)
        {
            name = Name;
            authorization = Authorization;
            apiKey = ApiKey;
            passes = Passes;
        }
    }
}
