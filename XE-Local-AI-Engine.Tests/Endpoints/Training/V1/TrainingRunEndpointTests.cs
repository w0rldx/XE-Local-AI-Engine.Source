namespace XE_Local_AI_Engine.Tests.Endpoints.Training.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint integration tests for the training run routes against the real DI host and an empty database. Every
///     route is Operator-gated, and the license confirmation is enforced at the boundary so an operator gets a 4xx
///     rather than a fault — the store enforces it a second time so no other caller can bypass it.
/// </summary>
public sealed class TrainingRunEndpointTests
{
    private const string ApiPrefix = "/api/local/v1/training/runs";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task EveryRunRoute_WithoutABearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var list = await client.GetAsync(ApiPrefix).ConfigureAwait(false);
        using var byId = await client.GetAsync($"{ApiPrefix}/{Guid.NewGuid()}").ConfigureAwait(false);
        using var defaults = await client.GetAsync($"{ApiPrefix}/defaults?baseArtifactId={Guid.NewGuid()}").ConfigureAwait(false);
        using var create = new HttpRequestMessage(HttpMethod.Post, ApiPrefix);
        create.Headers.Add("Origin", "http://localhost");
        using var createResponse = await client.SendAsync(create).ConfigureAwait(false);
        using var cancel = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/{Guid.NewGuid()}/cancel");
        cancel.Headers.Add("Origin", "http://localhost");
        using var cancelResponse = await client.SendAsync(cancel).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, byId.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, defaults.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, createResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, cancelResponse.StatusCode);
    }

    [Test]
    public async Task ListRuns_WithOperatorToken_ReturnsAnEmptyPageOnACleanDatabase()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ApiPrefix);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.True(document.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() == 0,
            "An empty database is an empty page, never a 404.");
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Test]
    public async Task GetRun_WhenUnknown_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/{Guid.NewGuid()}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task CancelRun_WhenUnknown_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/{Guid.NewGuid()}/cancel");
        request.Headers.Add("Origin", "http://localhost");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task RunCreate_WithoutLicenseConfirmation_Rejected()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiPrefix)
        {
            Content = JsonContent.Create(new
            {
                datasetId = Guid.NewGuid(),
                expectedDatasetVersion = 1,
                baseArtifactId = Guid.NewGuid(),
                licenseConfirmed = false
            })
        };
        request.Headers.Add("Origin", "http://localhost");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // The licensing gate is checked before anything else, so this is a 400 rather than the 400 an unknown dataset
        // would produce later — and nothing is queued either way, which the service suite pins against a real store.
        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.True(body.Contains("licens", StringComparison.OrdinalIgnoreCase), $"The refusal must name the licensing gate. Body: {body}");
    }

    [Test]
    public async Task GetRunDefaults_ForAnUnknownCheckpoint_IsRejectedRatherThanFaulting()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/defaults?baseArtifactId={Guid.NewGuid()}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task ListRuns_WithAnOutOfRangePageSize_IsRejected()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}?page=1&pageSize=5000");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
