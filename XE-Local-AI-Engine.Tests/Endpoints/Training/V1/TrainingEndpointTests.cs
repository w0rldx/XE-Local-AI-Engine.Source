namespace XE_Local_AI_Engine.Tests.Endpoints.Training.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint integration tests for the training runtime and base-artifact routes. Covers 401 on every route without
///     a bearer token (all are Operator-gated, none desktop-gated), reachability with an operator token, and the
///     404/validation shapes. Runs against the real DI host with an empty database.
/// </summary>
public sealed class TrainingEndpointTests
{
    private const string ApiPrefix = "/api/local/v1/training";

    [Test]
    public async Task RuntimeStatus_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{ApiPrefix}/runtime/status").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task BaseArtifacts_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{ApiPrefix}/base-artifacts").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task RuntimeInstall_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/runtime/install");
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task RuntimeStatus_WithOperatorToken_ReportsAnIdleRuntimeOnACleanBox()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/runtime/status");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        AssertEx.True(document.RootElement.TryGetProperty("phase", out _), "The status must always carry a phase.");
        AssertEx.True(document.RootElement.TryGetProperty("logLines", out var logLines) && logLines.ValueKind == JsonValueKind.Array,
            "The status must carry the log ring, empty or not.");

        // Integrity inputs stay server-side; they are not something the UI renders.
        AssertEx.False(json.Contains("uvSha256", StringComparison.OrdinalIgnoreCase), "The uv digest must not reach the wire.");
        AssertEx.False(json.Contains("lockfileSha256", StringComparison.OrdinalIgnoreCase), "The lockfile hash must not reach the wire.");
    }

    [Test]
    public async Task RuntimePrerequisites_WithOperatorToken_ReportsEveryItem()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/runtime/prerequisites");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.True(document.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0,
            "The prerequisite report is per-item; an empty list would tell the operator nothing.");
        AssertEx.True(document.RootElement.TryGetProperty("canInstall", out _));
    }

    [Test]
    public async Task ListBaseArtifacts_WithOperatorToken_ReturnsAnEmptyCollectionOnACleanDatabase()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/base-artifacts");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.True(document.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() == 0,
            "An empty database is an empty list, never a 404.");
    }

    [Test]
    public async Task GetBaseArtifact_WhenUnknown_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/base-artifacts/{Guid.NewGuid()}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task DeleteBaseArtifact_WhenUnknown_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ApiPrefix}/base-artifacts/{Guid.NewGuid()}");
        request.Headers.Add("Origin", "http://localhost");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task CreateBaseArtifact_WhenTheRepoIdIsBlank_IsRejectedByValidation()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/base-artifacts")
        {
            Content = JsonContent.Create(new
            {
                repoId = string.Empty
            })
        };
        request.Headers.Add("Origin", "http://localhost");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
