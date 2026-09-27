namespace XE_Local_AI_Engine.Tests.Endpoints.Python.V1;

using System.Net;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The Managed Python routes: 401 without a bearer, the status shape with no host path or digest on the wire, and the
///     typed 409 of the Compute actions (the test host leaves <c>Compute:Enabled</c> off).
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ManagedPythonEndpointTests
{
    private const string ApiPrefix = "/api/local/v1/python";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    [Arguments("GET", "status")]
    [Arguments("POST", "environments/compute/repair")]
    [Arguments("POST", "environments/compute/remove")]
    public async Task EveryRoute_WhenNoBearerToken_ReturnsUnauthorized(string method, string route)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"{ApiPrefix}/{route}");
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    [Arguments("GET", "status")]
    [Arguments("POST", "environments/compute/repair")]
    [Arguments("POST", "environments/compute/remove")]
    public async Task EveryRoute_WithANonOperatorToken_ReturnsForbidden(string method, string route)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"{ApiPrefix}/{route}");
        request.Headers.Add("Origin", "http://localhost");
        Factory.AddNonOperatorBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Status_WithOperatorToken_ReportsTheToolchainAndBothEnvironments_WithoutPathsOrDigests()
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/status");
        Factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var toolchain = document.RootElement.GetProperty("toolchain");
        AssertEx.NotNullOrEmpty(toolchain.GetProperty("uvVersion").GetString());
        AssertEx.True(toolchain.TryGetProperty("uvPresent", out _));
        AssertEx.Equal(JsonValueKind.Array, toolchain.GetProperty("pythonInstalls").ValueKind);

        var environments = document.RootElement.GetProperty("environments").EnumerateArray().ToArray();
        AssertEx.Equal("training,compute", string.Join(",", environments.Select(static row => row.GetProperty("profileId").GetString())));
        foreach (var row in environments)
        {
            AssertEx.NotNullOrEmpty(row.GetProperty("state").GetString());
            AssertEx.Equal(JsonValueKind.Array, row.GetProperty("mismatches").ValueKind);
        }

        // The lockfile digest is an integrity input and a host path is not the operator UI's business.
        AssertEx.False(json.Contains("sha256", StringComparison.OrdinalIgnoreCase), "No digest may reach the wire.");
        AssertEx.Empty(StringValues(document.RootElement).Where(static value => Path.IsPathFullyQualified(value)), "No absolute path may reach the wire.");
    }

    [Test]
    [Arguments("repair")]
    [Arguments("remove")]
    public async Task ComputeAction_WhenComputeIsUnsupported_ReturnsTheTypedConflict(string action)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/environments/compute/{action}");
        request.Headers.Add("Origin", "http://localhost");
        Factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal(expected: 2, document.RootElement.EnumerateObject().Count());
        AssertEx.Equal("unsupported", document.RootElement.GetProperty("reason").GetString());
        AssertEx.NotNullOrEmpty(document.RootElement.GetProperty("message").GetString());
    }

    private static IEnumerable<string> StringValues(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => [element.GetString()!],
            JsonValueKind.Object => element.EnumerateObject().SelectMany(static property => StringValues(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().SelectMany(StringValues),
            _ => []
        };
    }
}
