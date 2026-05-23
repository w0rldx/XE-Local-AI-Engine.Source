namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RouteCoexistenceTests
{
    [Test]
    public async Task HealthEndpoints_WhenBlazorAndSpaAreMounted_DoNotUseSpaFallback()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var liveResponse = await client.GetAsync("/health/live").ConfigureAwait(false);
        using var readyResponse = await client.GetAsync("/health/ready").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
        AssertEx.Contains(readyResponse.Content.Headers.ContentType?.MediaType,
            "json",
            StringComparison.OrdinalIgnoreCase,
            "Ready health endpoint should return JSON instead of the React or Blazor shell.");
    }

    [Test]
    public async Task LocalApi_WhenBlazorAndSpaAreMounted_ReturnsJsonInsteadOfHtmlShell()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        using var request = CreateProbeRequest(factory, "route-coexistence");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(response.Content.Headers.ContentType?.MediaType,
            "json",
            StringComparison.OrdinalIgnoreCase,
            "Local API route should return JSON, not a fallback HTML document.");

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream).ConfigureAwait(false);
        AssertEx.Equal("route-coexistence", document.RootElement.GetProperty("name").GetString());
    }

    [Test]
    public async Task RootRoute_WhenTransitionPrefixIsActive_RemainsBlazorOwned()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(response.Content.Headers.ContentType?.MediaType, "html", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(body, "_framework/blazor.web.js", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(body.Contains("/app/assets/", StringComparison.OrdinalIgnoreCase), "Root route should not serve the React /app shell during transition.");
    }

    [Test]
    public async Task ReactDeepLink_WhenUnderAppPrefix_ServesSpaShellWithoutBlazorScript()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/app/dashboard").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(response.Content.Headers.ContentType?.MediaType, "html", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(body, "<div id=\"root\"></div>", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(body, "/app/assets/", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(body.Contains("_framework/blazor.web.js", StringComparison.OrdinalIgnoreCase), "React deep links should not serve the Blazor shell.");
    }

    [Test]
    public async Task FileLikeAppPath_WhenAssetIsMissing_DoesNotUseSpaFallback()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/app/assets/missing-route-coexistence-file.js").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.False(string.Equals(response.Content.Headers.ContentType?.MediaType,
                "text/html",
                StringComparison.OrdinalIgnoreCase),
            "File-like /app asset requests should not be rewritten to the SPA shell.");
    }

    [Test]
    public async Task SignalRPaths_WhenBlazorIsMounted_DoNotCollideWithFutureLocalChatHubPath()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var blazorNegotiateRequest = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        using var blazorNegotiateResponse = await client.SendAsync(blazorNegotiateRequest).ConfigureAwait(false);
        using var localChatNegotiateRequest = CreateLocalChatNegotiateRequest(factory);
        using var localChatNegotiateResponse = await client.SendAsync(localChatNegotiateRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, blazorNegotiateResponse.StatusCode);
        AssertEx.Contains(blazorNegotiateResponse.Content.Headers.ContentType?.MediaType,
            "json",
            StringComparison.OrdinalIgnoreCase,
            "Blazor SignalR negotiate should remain available on _blazor.");

        AssertEx.Equal(HttpStatusCode.NotFound, localChatNegotiateResponse.StatusCode);
        AssertEx.False(string.Equals(localChatNegotiateResponse.Content.Headers.ContentType?.MediaType,
                "text/html",
                StringComparison.OrdinalIgnoreCase),
            "The future local chat hub path must not be swallowed by Blazor or the React SPA fallback before the hub is mapped.");
    }

    private static HttpRequestMessage CreateProbeRequest(TestingWebAppFactory factory, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/diagnostics/validation-probe")
        {
            Content = JsonContent.Create(new { Name = name })
        };
        AddLocalOperatorHeaders(factory, request);
        return request;
    }

    private static HttpRequestMessage CreateLocalChatNegotiateRequest(TestingWebAppFactory factory)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/chat/hub/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        AddLocalOperatorHeaders(factory, request);
        return request;
    }

    private static void AddLocalOperatorHeaders(TestingWebAppFactory factory, HttpRequestMessage request)
    {
        var token = factory.Services.GetRequiredService<ILocalOperatorTokenProvider>().Token;
        request.Headers.Add(LocalOperatorAuthorization.HeaderName, token);
        request.Headers.Add("Origin", "http://localhost");
    }
}
