namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RouteCoexistenceTests
{
    [Test]
    public async Task HealthEndpoints_WhenSpaOwnsRoot_DoNotUseSpaFallback()
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
            "Ready health endpoint should return JSON instead of the React shell, even though the SPA fallback owns root.");
    }

    [Test]
    public async Task LocalApi_WhenSpaOwnsRoot_ReturnsJsonInsteadOfHtmlShell()
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
    public async Task RootRoute_AfterCutover_ServesReactSpaShellWithoutBlazorScript()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(response.Content.Headers.ContentType?.MediaType, "html", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(body, "<div id=\"root\"></div>", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(body, "/assets/", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(body.Contains("_framework/blazor.web.js", StringComparison.OrdinalIgnoreCase),
            "After cutover the React client owns root; the Blazor shell must no longer be served.");
        AssertEx.False(body.Contains("/app/assets/", StringComparison.OrdinalIgnoreCase),
            "After cutover there is no /app prefix; assets are served from root.");
    }

    [Test]
    public async Task ReactDeepLink_AtRoot_ServesSpaShellWithoutBlazorScript()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/dashboard").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(response.Content.Headers.ContentType?.MediaType, "html", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(body, "<div id=\"root\"></div>", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(body, "/assets/", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(body.Contains("_framework/blazor.web.js", StringComparison.OrdinalIgnoreCase),
            "React deep links should serve the React SPA shell, not the Blazor shell.");
    }

    [Test]
    public async Task FileLikeAssetPath_WhenAssetIsMissing_DoesNotUseSpaFallback()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/assets/missing-route-coexistence-file.js").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.False(string.Equals(response.Content.Headers.ContentType?.MediaType,
                "text/html",
                StringComparison.OrdinalIgnoreCase),
            "File-like asset requests should not be rewritten to the SPA shell.");
    }

    [Test]
    public async Task LocalChatHubPath_AfterCutover_IsNotSwallowedBySpaFallback()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var localChatNegotiateRequest = CreateLocalChatNegotiateRequest(factory);
        using var localChatNegotiateResponse = await client.SendAsync(localChatNegotiateRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, localChatNegotiateResponse.StatusCode);
        AssertEx.False(string.Equals(localChatNegotiateResponse.Content.Headers.ContentType?.MediaType,
                "text/html",
                StringComparison.OrdinalIgnoreCase),
            "The local chat hub negotiate path must not be swallowed by the React SPA fallback after cutover.");
    }

    private static HttpRequestMessage CreateProbeRequest(TestingWebAppFactory factory, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/diagnostics/validation-probe")
        {
            Content = JsonContent.Create(new
            {
                Name = name
            })
        };
        AddNodeAuthHeaders(factory, request);
        return request;
    }

    private static HttpRequestMessage CreateLocalChatNegotiateRequest(TestingWebAppFactory factory)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/chat/hub/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        AddNodeAuthHeaders(factory, request);
        return request;
    }

    private static void AddNodeAuthHeaders(TestingWebAppFactory factory, HttpRequestMessage request)
    {
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
    }
}
