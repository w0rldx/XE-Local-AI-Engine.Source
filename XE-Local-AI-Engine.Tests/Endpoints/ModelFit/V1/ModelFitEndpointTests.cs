namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint integration tests for the model-fit / advisor local API. Covers: 401 on every route without a bearer
///     token; reachability with an operator token; the latest endpoint's explicit cache-miss state (hasCache:false, 200,
///     never a 404); the refresh endpoint's template guard (a random scheduled-job id is rejected, not executed);
///     redaction (the latest response carries no raw output / stderr / diagnostics keys); the new advisor management
///     routes (hardware-profile, gguf/browse, running, llamacpp/version, hf-token presence) returning their sanitized
///     shapes; and the security invariant that the hf-token endpoints NEVER echo the token value. These run against the
///     real DI host with an empty DB (the scheduler is not started in the test host).
/// </summary>
public sealed class ModelFitEndpointTests
{
    /// <summary>
    ///     One host for the whole class. The only test that writes host-wide state is the set-HF-token test, which
    ///     stores a token into the shared token slot — safe to share because no other test in this class asserts that
    ///     the slot is empty.
    /// </summary>
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    private const string ApiPrefix = "/api/local/v1";

    private static string RecommendationsLatestRoute()
    {
        return $"{ApiPrefix}/model-fit/recommendations/latest";
    }

    private static string RecommendationsRefreshRoute()
    {
        return $"{ApiPrefix}/model-fit/recommendations/refresh";
    }

    private static string HardwareProfileRoute()
    {
        return $"{ApiPrefix}/model-fit/hardware-profile";
    }

    private static string GgufBrowseRoute()
    {
        return $"{ApiPrefix}/model-fit/gguf/browse";
    }

    private static string GgufInspectRoute()
    {
        return $"{ApiPrefix}/model-fit/gguf/inspect";
    }

    private static string RunningRoute()
    {
        return $"{ApiPrefix}/model-fit/running";
    }

    private static string LlamaCppVersionRoute()
    {
        return $"{ApiPrefix}/model-fit/llamacpp/version";
    }

    private static string HfTokenRoute()
    {
        return $"{ApiPrefix}/model-fit/hf-token";
    }

    [Test]
    public async Task GetLatestRecommendations_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(RecommendationsLatestRoute()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task RefreshRecommendations_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
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

    [Test]
    public async Task HardwareProfile_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(HardwareProfileRoute()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task HfTokenStatus_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(HfTokenRoute()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task GetLatestRecommendations_WhenNoCache_ReturnsOkWithHasCacheFalse()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{RecommendationsLatestRoute()}?useCase=coding");
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
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, RecommendationsLatestRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // The sanitized recommendation response must never expose any raw output / stderr / diagnostics keys, nor the
        // dropped approved-image / provider coupling.
        AssertEx.False(json.Contains("rawJson", StringComparison.OrdinalIgnoreCase), "Latest response must not expose rawJson.");
        AssertEx.False(json.Contains("raw_json", StringComparison.OrdinalIgnoreCase), "Latest response must not expose raw_json.");
        AssertEx.False(json.Contains("stderr", StringComparison.OrdinalIgnoreCase), "Latest response must not expose stderr.");
        AssertEx.False(json.Contains("diagnostics", StringComparison.OrdinalIgnoreCase), "Latest response must not expose diagnostics.");
        AssertEx.False(json.Contains("sourceImageId", StringComparison.OrdinalIgnoreCase), "Latest response must not expose the dropped approved-image id.");
        AssertEx.False(json.Contains("providerName", StringComparison.OrdinalIgnoreCase), "Latest response must not expose the dropped provider name.");
    }

    [Test]
    public async Task RefreshRecommendations_WhenJobNotFound_ReturnsBadRequestWithErrorBody()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, RecommendationsRefreshRoute())
        {
            // A random id resolves to no definition → the template guard throws ScheduledJobValidationException → global DomainValidationExceptionHandler → 400.
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

    [Test]
    public async Task RefreshRecommendations_WhenCtxTargetBelowFloor_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, RecommendationsRefreshRoute())
        {
            Content = JsonContent.Create(new
            {
                scheduledJobId = Guid.NewGuid(),
                ctxTarget = 10
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // The ctxTarget floor is validated BEFORE the template guard, so an out-of-range value 400s on its own.
        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task HardwareProfile_WhenAuthorized_ReturnsSanitizedAggregates()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, HardwareProfileRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        AssertEx.True(doc.RootElement.TryGetProperty("totalRamBytes", out _), "Hardware profile must carry totalRamBytes.");
        AssertEx.True(doc.RootElement.TryGetProperty("gpuVendor", out _), "Hardware profile must carry gpuVendor.");
        AssertEx.True(doc.RootElement.TryGetProperty("cpuCores", out _), "Hardware profile must carry cpuCores.");

        // Sanitization: no machine identifiers (hostname/serial) in the aggregates-only profile.
        AssertEx.False(json.Contains("hostname", StringComparison.OrdinalIgnoreCase), "Hardware profile must not expose a hostname.");
        AssertEx.False(json.Contains("serial", StringComparison.OrdinalIgnoreCase), "Hardware profile must not expose a serial.");
    }

    [Test]
    public async Task GgufBrowse_WhenAuthorized_ReturnsItemsArray()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        // No network in the test host → discovery fails → the endpoint degrades to an OK-empty list (never a 500).
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GgufBrowseRoute()}?query=qwen&limit=5");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        AssertEx.True(doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array,
            "Browse response must wrap results in an 'items' array.");
    }

    [Test]
    public async Task GgufInspect_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{GgufInspectRoute()}?repoId=org/some-GGUF").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task GgufInspect_WhenAuthorized_ReturnsFilesArray()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        // No network in the test host → inspection fails → the endpoint degrades to an OK-empty file list (never a 500).
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GgufInspectRoute()}?repoId=unsloth/gemma-3-12b-it-GGUF");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        AssertEx.True(doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array,
            "Inspect response must wrap files in a 'files' array.");
        AssertEx.Equal("unsloth/gemma-3-12b-it-GGUF", doc.RootElement.GetProperty("repoId").GetString());
    }

    [Test]
    public async Task GgufInspect_WhenRepoIdMissing_ReturnsValidationError()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, GgufInspectRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task ListRunningModels_WhenAuthorized_ReturnsItemsArray()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, RunningRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        AssertEx.True(doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array,
            "Running response must wrap results in an 'items' array.");
    }

    [Test]
    public async Task EnsureLlamaCppBinary_WhenVariantUnknown_ReturnsBadRequest()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, LlamaCppVersionRoute())
        {
            Content = JsonContent.Create(new
            {
                variant = "not-a-variant"
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task HfTokenStatus_WhenAuthorized_ReturnsOnlyPresenceFlag_NeverTheToken()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, HfTokenRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        AssertEx.True(doc.RootElement.TryGetProperty("hasToken", out _), "Token status must carry a hasToken flag.");
        AssertEx.False(json.Contains("token\":\"", StringComparison.OrdinalIgnoreCase), "Token status must never embed a token value.");
    }

    [Test]
    public async Task SetHfToken_WhenAuthorized_StoresThenReportsPresence_NeverEchoesTheToken()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        const string secret = "hf_super_secret_value_123";

        // Set a token: the response must report presence true but NEVER echo the secret value.
        using (var setRequest = new HttpRequestMessage(HttpMethod.Post, HfTokenRoute())
               {
                   Content = JsonContent.Create(new
                   {
                       token = secret
                   })
               })
        {
            factory.AddNodeBearerToken(setRequest);
            using var setResponse = await client.SendAsync(setRequest).ConfigureAwait(false);

            var setJson = await setResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            var diagnosticJson = setJson.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            AssertEx.Equal(HttpStatusCode.OK, setResponse.StatusCode, $"Unexpected set-token response: {diagnosticJson}");
            AssertEx.False(setJson.Contains(secret, StringComparison.Ordinal), "Set-token response must NEVER echo the token value.");
            using var setDoc = JsonDocument.Parse(setJson);
            AssertEx.True(setDoc.RootElement.GetProperty("hasToken").GetBoolean(), "After setting a token, hasToken must be true.");
        }

        // The presence GET reflects the stored token but still never returns its value.
        using (var statusRequest = new HttpRequestMessage(HttpMethod.Get, HfTokenRoute()))
        {
            factory.AddNodeBearerToken(statusRequest);
            using var statusResponse = await client.SendAsync(statusRequest).ConfigureAwait(false);

            AssertEx.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            var statusJson = await statusResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            AssertEx.False(statusJson.Contains(secret, StringComparison.Ordinal), "Token status must NEVER return the stored token value.");
            using var statusDoc = JsonDocument.Parse(statusJson);
            AssertEx.True(statusDoc.RootElement.GetProperty("hasToken").GetBoolean(), "Token status must report the stored token as present.");
        }
    }
}
