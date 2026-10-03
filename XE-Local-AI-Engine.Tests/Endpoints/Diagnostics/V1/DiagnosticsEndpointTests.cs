namespace XE_Local_AI_Engine.Tests.Endpoints.Diagnostics.V1;

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two Diagnostics routes on the real host: operator-gated, the node-info JSON free of absolute paths and the
///     host name, and the bundle a zip carrying the manifest and node info even though the Testing host has no log sink.
/// </summary>
[Category(TestCategories.Integration)]
public sealed partial class DiagnosticsEndpointTests
{
    private const string LogLevelPath = "/api/local/v1/diagnostics/log-level";
    private const string NodeInfoPath = "/api/local/v1/diagnostics/node-info";
    private const string SupportBundlePath = "/api/local/v1/diagnostics/support-bundle";

    [Test]
    [Arguments(NodeInfoPath)]
    [Arguments(SupportBundlePath)]
    [Arguments(LogLevelPath)]
    public async Task Get_WithoutOperatorToken_ReturnsUnauthorized(string path)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task NodeInfo_ReturnsTheReport_WithoutAbsolutePathsOrTheHostName()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = Authorized(factory, NodeInfoPath);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        AssertEx.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("version").GetString()), body);
        AssertEx.True(document.RootElement.TryGetProperty("warnings", out _), body);
        AssertEx.True(document.RootElement.TryGetProperty("runningModels", out _), body);
        AssertEx.True(document.RootElement.TryGetProperty("residents", out _), body);
        AssertNoHostIdentity(body);
    }

    [Test]
    public async Task SupportBundle_ReturnsAZip_WithManifestAndNodeInfo()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = Authorized(factory, SupportBundlePath);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? string.Empty;
        AssertEx.True(fileName.StartsWith("xe-support-", StringComparison.Ordinal) && fileName.EndsWith(".zip", StringComparison.Ordinal), fileName);

        await using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        var names = zip.Entries.Select(static entry => entry.FullName).ToArray();
        AssertEx.Contains(names, "manifest.json");
        AssertEx.Contains(names, "node-info.json");
        await using var nodeInfo = await zip.GetEntry("node-info.json")!.OpenAsync();
        using var reader = new StreamReader(nodeInfo);
        AssertNoHostIdentity(await reader.ReadToEndAsync());
    }

    [Test]
    public async Task LogLevel_PutWithoutOperatorToken_ReturnsUnauthorized()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, LogLevelPath)
        {
            Content = JsonContent.Create(new
            {
                verbose = true
            })
        };
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task LogLevel_Put_TogglesVerbose_AndNodeInfoReportsIt()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        AssertEx.True(await PutVerboseAsync(factory, client, verbose: true));
        AssertEx.True(await ReadBoolAsync(factory, client, LogLevelPath, "verbose"));
        AssertEx.True(await ReadBoolAsync(factory, client, NodeInfoPath, "verboseLogging"));

        AssertEx.False(await PutVerboseAsync(factory, client, verbose: false));
        AssertEx.False(await ReadBoolAsync(factory, client, NodeInfoPath, "verboseLogging"));
    }

    private static async Task<bool> PutVerboseAsync(TestServerWebAppFactory factory, HttpClient client, bool verbose)
    {
        using var request = Authorized(factory, LogLevelPath, HttpMethod.Put);
        request.Content = JsonContent.Create(new
        {
            verbose
        });
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("verbose").GetBoolean();
    }

    private static async Task<bool> ReadBoolAsync(TestServerWebAppFactory factory, HttpClient client, string path, string property)
    {
        using var request = Authorized(factory, path);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty(property).GetBoolean();
    }

    private static void AssertNoHostIdentity(string json)
    {
        AssertEx.False(AbsolutePathRegex().IsMatch(json), "The report must carry no absolute path: " + AbsolutePathRegex().Match(json).Value);
        var host = Environment.MachineName;
        if (host.Length >= 4)
        {
            AssertEx.False(json.Contains(host, StringComparison.OrdinalIgnoreCase), "The report must not name the host.");
        }
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, string path, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    // A POSIX path of two or more segments (not part of a URL), or a JSON-escaped Windows drive path.
    [GeneratedRegex(@"(?<![\w:/.])/[^\s""/]+/[^\s""]|[A-Za-z]:\\\\")]
    private static partial Regex AbsolutePathRegex();
}
