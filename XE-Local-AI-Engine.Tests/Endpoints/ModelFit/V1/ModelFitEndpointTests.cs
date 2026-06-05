namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint integration tests for the model-fit local API. Covers: 401 on every route without a bearer
///     token; reachability with an operator token; the latest endpoint's explicit cache-miss state (hasCache:false, 200,
///     never a 404); the refresh endpoint's template guard (a random scheduled-job id is rejected, not executed); and
///     redaction (the latest response carries no raw output / stderr / diagnostics keys). These run against the real DI
///     host with an empty DB (the scheduler is not started in the test host).
/// </summary>
public sealed class ModelFitEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static string ApprovedImagesRoute()
    {
        return $"{ApiPrefix}/model-fit/approved-images";
    }

    private static string RecommendationsLatestRoute()
    {
        return $"{ApiPrefix}/model-fit/recommendations/latest";
    }

    private static string RecommendationsRefreshRoute()
    {
        return $"{ApiPrefix}/model-fit/recommendations/refresh";
    }

    // ──────────────────────────────────────────────────────────────────────
    // 401 — every route requires a bearer token (Operator policy).
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ListApprovedImages_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ApprovedImagesRoute()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task GetLatestRecommendations_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(RecommendationsLatestRoute()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task RefreshRecommendations_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, RecommendationsRefreshRoute())
        {
            Content = JsonContent.Create(new
            {
                scheduledJobId = Guid.NewGuid()
            })
        };
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Reachability with an operator token.
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ListApprovedImages_WhenAuthorized_ReturnsOkWithItemsArray()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ApprovedImagesRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        AssertEx.True(doc.RootElement.TryGetProperty("items", out _),
            "Approved-images response must wrap results in an 'items' array.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Latest endpoint: explicit cache-miss (200 with hasCache:false), not a 404, and no raw fields.
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetLatestRecommendations_WhenNoCache_ReturnsOkWithHasCacheFalse()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{RecommendationsLatestRoute()}?useCase=coding&providerName=ollama");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // Cache-miss is an explicit empty state, never a 404.
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        AssertEx.True(doc.RootElement.TryGetProperty("hasCache", out var hasCache), "Latest response must carry a hasCache flag.");
        AssertEx.False(hasCache.GetBoolean(), "An empty DB is a cache-miss → hasCache:false.");
        AssertEx.True(doc.RootElement.TryGetProperty("recommendations", out var recs) && recs.GetArrayLength() == 0,
            "A cache-miss has an empty recommendations array.");
    }

    [Test]
    public async Task GetLatestRecommendations_ResponseDoesNotContainRawSnapshotFields()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, RecommendationsLatestRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // The sanitized recommendation response must never expose any raw output / stderr / diagnostics keys.
        AssertEx.False(json.Contains("rawJson", StringComparison.OrdinalIgnoreCase), "Latest response must not expose rawJson.");
        AssertEx.False(json.Contains("raw_json", StringComparison.OrdinalIgnoreCase), "Latest response must not expose raw_json.");
        AssertEx.False(json.Contains("stderr", StringComparison.OrdinalIgnoreCase), "Latest response must not expose stderr.");
        AssertEx.False(json.Contains("diagnostics", StringComparison.OrdinalIgnoreCase), "Latest response must not expose diagnostics.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Refresh endpoint: the template guard rejects an unknown/non-model-fit job id (400, not 500/no-op execution).
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RefreshRecommendations_WhenJobNotFound_ReturnsBadRequestWithErrorBody()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, RecommendationsRefreshRoute())
        {
            // A random id resolves to no definition → the template guard throws → AddError + Send.ErrorsAsync → 400.
            Content = JsonContent.Create(new
            {
                scheduledJobId = Guid.NewGuid()
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.True(payload.Length > 0, "Validation error response must have a non-empty body.");
    }
}
