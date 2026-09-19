namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class RouteCoexistenceTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task HealthEndpoints_WhenSpaOwnsRoot_DoNotUseSpaFallback()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var liveResponse = await client.GetAsync("/health/live");
        using var readyResponse = await client.GetAsync("/health/ready");

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
        var factory = Factory;
        using var client = factory.CreateClient();
        using var request = CreateProbeRequest(factory, "route-coexistence");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(response.Content.Headers.ContentType?.MediaType,
            "json",
            StringComparison.OrdinalIgnoreCase,
            "Local API route should return JSON, not a fallback HTML document.");

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        AssertEx.Equal("route-coexistence", document.RootElement.GetProperty("name").GetString());
    }

    [Test]
    public async Task RootRoute_AfterCutover_ServesReactSpaShellWithoutBlazorScript()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

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
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/dashboard");
        var body = await response.Content.ReadAsStringAsync();

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
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/assets/missing-route-coexistence-file.js");

        // 401, not 404: the SPA fallback's {*path:nonfile} constraint excludes a dotted path, so routing matches no
        // endpoint at all — and the FallbackPolicy answers exactly that case, so an unmatched path fails closed.
        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertEx.False(string.Equals(response.Content.Headers.ContentType?.MediaType,
                "text/html",
                StringComparison.OrdinalIgnoreCase),
            "File-like asset requests should not be rewritten to the SPA shell.");
    }

    [Test]
    public async Task UnknownApiRoute_WhenOperatorAuthenticated_Returns404InsteadOfSpaShell()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/local/v1/definitely-not-a-route");
        AddNodeAuthHeaders(factory, request);

        using var response = await client.SendAsync(request);

        // 404, not the 200 HTML shell: {*path:nonfile} still selects the SPA fallback, but the middleware after
        // UseRouting recognises it by its marker on a path under the API prefix and detaches it, so no endpoint is
        // selected and the request ends in the pipeline's bare 404. Same pair as the dotted-asset case above.
        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.False(string.Equals(response.Content.Headers.ContentType?.MediaType,
                "text/html",
                StringComparison.OrdinalIgnoreCase),
            "An unmatched API route must not be rewritten to the SPA shell.");
    }

    [Test]
    public async Task UnknownApiRoute_WhenAnonymous_Returns401NotSpaShell()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/local/v1/definitely-not-a-route");
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        // Detaching the SPA fallback leaves routing with no endpoint selected, which is precisely the case the
        // existing FallbackPolicy answers with a challenge — so an anonymous caller learns nothing about which API
        // paths exist. The shell's own AllowAnonymous is what used to hand it a 200 instead.
        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertEx.False(string.Equals(response.Content.Headers.ContentType?.MediaType,
                "text/html",
                StringComparison.OrdinalIgnoreCase),
            "An unmatched API route must not be rewritten to the SPA shell for an anonymous caller either.");
    }

    [Test]
    public async Task UnknownApiRoute_WhenPosted_IsNeverTheSpaShell()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/definitely-not-a-route")
        {
            Content = JsonContent.Create(new
            {
                name = "route-coexistence"
            })
        };
        AddNodeAuthHeaders(factory, request);

        using var response = await client.SendAsync(request);

        // 405, and that is the point: MapFallbackToFile registers a GET/HEAD-only catch-all, so a POST to an unmatched
        // API path never reached the SPA shell in the first place and routing answers it itself. Pinned here because
        // the obvious alternative fix — a second routed catch-all over the prefix accepting every verb — joins the
        // candidate set of every real route below it and replaces routing's 405 with 404 across the whole API.
        AssertEx.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        AssertEx.False(string.Equals(response.Content.Headers.ContentType?.MediaType,
                "text/html",
                StringComparison.OrdinalIgnoreCase),
            "An unmatched API route must not be rewritten to the SPA shell for a non-GET verb.");
    }

    [Test]
    public async Task UnknownNonApiRoute_WhenExtensionless_StillServesSpaShell()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/definitely-not-a-react-route");
        var body = await response.Content.ReadAsStringAsync();

        // The detach must not widen past the API prefix: everything outside it keeps being served the SPA shell, so
        // client-side routing still resolves a deep link the server knows nothing about.
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(response.Content.Headers.ContentType?.MediaType, "html", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(body, "<div id=\"root\"></div>", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task LocalChatHubPath_AfterCutover_IsNotSwallowedBySpaFallback()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var localChatNegotiateRequest = CreateLocalChatNegotiateRequest(factory);
        using var localChatNegotiateResponse = await client.SendAsync(localChatNegotiateRequest);

        AssertEx.Equal(HttpStatusCode.OK, localChatNegotiateResponse.StatusCode);
        AssertEx.False(string.Equals(localChatNegotiateResponse.Content.Headers.ContentType?.MediaType,
                "text/html",
                StringComparison.OrdinalIgnoreCase),
            "The local chat hub negotiate path must not be swallowed by the React SPA fallback after cutover.");
    }

    private static HttpRequestMessage CreateProbeRequest(TestServerWebAppFactory factory, string name)
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

    private static HttpRequestMessage CreateLocalChatNegotiateRequest(TestServerWebAppFactory factory)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/chat/hub/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        AddNodeAuthHeaders(factory, request);
        return request;
    }

    private static void AddNodeAuthHeaders(TestServerWebAppFactory factory, HttpRequestMessage request)
    {
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
    }
}
