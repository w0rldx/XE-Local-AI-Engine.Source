namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Net;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The app-update endpoints are desktop-mode only. The default test host runs in non-desktop mode, so
///     both routes must be ABSENT (the FastEndpoints filter excludes <c>IDesktopOnlyEndpoint</c> off the
///     desktop flag) — the route is never mapped, so a POST to an unmapped path is rejected by routing
///     (404 / 405, since the SPA fallback only handles GET) and a GET falls through to the SPA fallback (HTML, NOT a JSON
///     endpoint response). Either way the request never reaches an app-update endpoint.
/// </summary>
public sealed class AppUpdateEndpointDesktopGateTests
{
    private static readonly (HttpMethod Method, string Route)[] PostRoutes =
    [
        (HttpMethod.Post, "/api/local/v1/app-update/apply")
    ];

    private static readonly string[] GetRoutes =
    [
        "/api/local/v1/app-update/status"
    ];

    [Test]
    public async Task PostUpdateEndpoints_WhenNotDesktop_AreUnmapped()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        foreach (var (method, route) in PostRoutes)
        {
            using var request = new HttpRequestMessage(method, route);
            factory.AddNodeBearerToken(request);
            request.Headers.Add("Origin", "http://localhost");

            using var response = await client.SendAsync(request).ConfigureAwait(false);

            // Unmapped POST path → routing rejects it. A registered endpoint with a valid operator token would have
            // returned 200/400; 404/405 proves the endpoint was never mapped off the desktop flag.
            AssertEx.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{route} should be unmapped off desktop, but returned {response.StatusCode}");
        }
    }

    [Test]
    public async Task GetUpdateEndpoints_WhenNotDesktop_FallThroughToSpa_NotJsonEndpoint()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        foreach (var route in GetRoutes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            factory.AddNodeBearerToken(request);
            request.Headers.Add("Origin", "http://localhost");

            using var response = await client.SendAsync(request).ConfigureAwait(false);

            // The GET route is unmapped, so it falls through to the SPA fallback (HTML) rather than producing a JSON
            // endpoint response. The decisive check: the body is NOT a JSON app-update payload.
            var contentType = response.Content.Headers.ContentType?.MediaType;
            AssertEx.False(string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase),
                $"{route} must not be served by a JSON endpoint off desktop, but content-type was {contentType}");
        }
    }
}
