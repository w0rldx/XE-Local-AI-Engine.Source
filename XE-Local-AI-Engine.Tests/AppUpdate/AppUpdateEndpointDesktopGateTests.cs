namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Net;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The app-update endpoints are desktop-mode only. The default test host runs in non-desktop mode, so
///     both routes must be ABSENT (the FastEndpoints filter excludes <c>IDesktopOnlyEndpoint</c> off the
///     desktop flag) — the route is never mapped, so a POST to an unmapped path is rejected by routing (404 / 405,
///     since the SPA fallback only handles GET) and a GET answers a bare 404: the SPA shell no longer serves the API
///     prefix, because the middleware after <c>UseRouting</c> detaches it there (see <c>SpaFallbackMarker</c> in
///     <c>Program.cs</c>). Either way the request never reaches an app-update endpoint, and never gets a JSON
///     endpoint response.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AppUpdateEndpointDesktopGateTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

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
        var factory = Factory;
        using var client = factory.CreateClient();

        foreach (var (method, route) in PostRoutes)
        {
            using var request = new HttpRequestMessage(method, route);
            factory.AddNodeBearerToken(request);
            request.Headers.Add("Origin", "http://localhost");

            using var response = await client.SendAsync(request);

            // Unmapped POST path → routing rejects it. A registered endpoint with a valid operator token would have
            // returned 200/400; 404/405 proves the endpoint was never mapped off the desktop flag.
            AssertEx.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{route} should be unmapped off desktop, but returned {response.StatusCode}");
        }
    }

    [Test]
    public async Task GetUpdateEndpoints_WhenNotDesktop_Answer404_NotJsonEndpoint()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        foreach (var route in GetRoutes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            factory.AddNodeBearerToken(request);
            request.Headers.Add("Origin", "http://localhost");

            using var response = await client.SendAsync(request);

            // The GET route is unmapped, so the SPA fallback is selected and then detached on the API prefix, leaving
            // the pipeline's bare 404 rather than a JSON endpoint response — and rather than the SPA shell it used to
            // serve. The decisive check stays: the body is NOT a JSON app-update payload.
            var contentType = response.Content.Headers.ContentType?.MediaType;
            AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
            AssertEx.False(string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase),
                $"{route} must not be served by a JSON endpoint off desktop, but content-type was {contentType}");
        }
    }
}
