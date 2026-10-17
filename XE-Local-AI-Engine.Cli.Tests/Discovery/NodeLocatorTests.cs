namespace XE_Local_AI_Engine.Cli.Tests.Discovery;

using System.Text.Json;
using XE_Local_AI_Engine.Cli.Discovery;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class NodeLocatorTests
{
    private const int LivePid = 4242;

    private const string ValidReady =
        """{"version":"1.0.0","url":"http://127.0.0.1:5123","mcpUrl":"http://127.0.0.1:5123/api/local/v1/mcp/server","dataDir":"{dir}","pid":4242,"startedAtUtc":"2026-10-17T00:00:00Z"}""";

    [Test]
    [Arguments("http loopback ip", "http://127.0.0.1:5123", "http://127.0.0.1:5123/")]
    [Arguments("https localhost (dev host)", "https://localhost:7001/", "https://localhost:7001/")]
    [Arguments("ipv6 loopback", "http://[::1]:5123", "http://[::1]:5123/")]
    public async Task LocateAsync_WhenAnExplicitUrlIsALoopbackOrigin_UsesItWithoutReadingReadyJson(string row, string url, string expected)
    {
        var node = await NodeLocator.LocateAsync(url, environmentUrl: null, MissingDirectory, NoProcessIsAlive, CancellationToken.None);

        AssertEx.Equal(expected, node.Origin.ToString(), row);
        AssertEx.Null(node.ProcessId, row);
    }

    [Test]
    [Arguments("routable host", "http://example.com:5123")]
    [Arguments("lan address", "http://192.168.1.10:5123")]
    [Arguments("credentials", "http://user:pw@127.0.0.1:5123")]
    [Arguments("a path", "http://127.0.0.1:5123/api")]
    [Arguments("a query", "http://127.0.0.1:5123/?x=1")]
    [Arguments("a fragment", "http://127.0.0.1:5123/#x")]
    [Arguments("another scheme", "ftp://127.0.0.1:5123")]
    [Arguments("not a url", "127.0.0.1:5123")]
    public async Task LocateAsync_WhenAnExplicitUrlIsNotALoopbackOrigin_FailsAsInvalidUrl(string row, string url)
    {
        var failure = await AssertEx.ThrowsAsync<NodeDiscoveryException>(() =>
            NodeLocator.LocateAsync(url, environmentUrl: null, MissingDirectory, NoProcessIsAlive, CancellationToken.None));

        AssertEx.Equal(NodeDiscoveryFailure.InvalidUrl, failure.Failure, row);
    }

    [Test]
    public async Task LocateAsync_UsesXeUrlWhenNoExplicitUrl_AndTheExplicitUrlWinsOverIt()
    {
        var fromEnvironment = await NodeLocator.LocateAsync(explicitUrl: null, "http://127.0.0.1:6000", MissingDirectory, NoProcessIsAlive, CancellationToken.None);
        var explicitWins = await NodeLocator.LocateAsync("http://127.0.0.1:7000", "http://127.0.0.1:6000", MissingDirectory, NoProcessIsAlive, CancellationToken.None);

        AssertEx.Equal(6000, fromEnvironment.Origin.Port);
        AssertEx.Equal(7000, explicitWins.Origin.Port);
    }

    [Test]
    public async Task LocateAsync_WhenReadyJsonIsValidAndItsProcessLives_ReturnsItsOrigin()
    {
        using var directory = new TemporaryDirectory();
        await directory.WriteReadyAsync(ValidReady);

        var node = await NodeLocator.LocateAsync(explicitUrl: null, environmentUrl: null, () => directory.Path, static pid => pid == LivePid, CancellationToken.None);

        AssertEx.Equal("http://127.0.0.1:5123/", node.Origin.ToString());
        AssertEx.Equal(LivePid, node.ProcessId);
        AssertEx.Equal("1.0.0", node.Version);
    }

    [Test]
    [Arguments("missing file", null, NodeDiscoveryFailure.NotRunning)]
    [Arguments("malformed json", "{\"url\":", NodeDiscoveryFailure.InvalidReadyFile)]
    [Arguments("json null", "null", NodeDiscoveryFailure.InvalidReadyFile)]
    [Arguments("routable url", "url=http://10.0.0.5:5123|mcp=http://10.0.0.5:5123/api/local/v1/mcp/server", NodeDiscoveryFailure.InvalidReadyFile)]
    [Arguments("https url (the engine writes http)", "url=https://127.0.0.1:5123|mcp=https://127.0.0.1:5123/api/local/v1/mcp/server", NodeDiscoveryFailure.InvalidReadyFile)]
    [Arguments("localhost name (the engine writes an ip)", "url=http://localhost:5123|mcp=http://localhost:5123/api/local/v1/mcp/server", NodeDiscoveryFailure.InvalidReadyFile)]
    [Arguments("url with a path", "url=http://127.0.0.1:5123/x", NodeDiscoveryFailure.InvalidReadyFile)]
    [Arguments("mcp url for another origin", "mcp=http://127.0.0.1:9999/api/local/v1/mcp/server", NodeDiscoveryFailure.InvalidReadyFile)]
    [Arguments("another data directory", "dir=/elsewhere", NodeDiscoveryFailure.InvalidReadyFile)]
    [Arguments("relative data directory", "dir=relative/path", NodeDiscoveryFailure.InvalidReadyFile)]
    [Arguments("no pid", "pid=0", NodeDiscoveryFailure.InvalidReadyFile)]
    [Arguments("dead pid", "pid=777", NodeDiscoveryFailure.StaleReadyFile)]
    public async Task LocateAsync_WhenReadyJsonIsNotTrustworthy_FailsWithItsReason(string row, string? edit, NodeDiscoveryFailure expected)
    {
        using var directory = new TemporaryDirectory();
        if (edit is not null)
        {
            await directory.WriteReadyAsync(edit.StartsWith('{') || edit == "null" ? edit : Edited(edit));
        }

        var failure = await AssertEx.ThrowsAsync<NodeDiscoveryException>(() =>
            NodeLocator.LocateAsync(explicitUrl: null, environmentUrl: null, () => directory.Path, static pid => pid == LivePid, CancellationToken.None));

        AssertEx.Equal(expected, failure.Failure, row);
    }

    [Test]
    [Arguments("explicit url", "http://127.0.0.1:7000", null)]
    [Arguments("XE_URL", null, "http://127.0.0.1:6000")]
    public async Task LocateAsync_WhenAUrlNamesTheNode_NeverResolvesTheDataDirectory(string row, string? explicitUrl, string? environmentUrl)
    {
        // The production overload resolves XE_DATA_DIR through this callback: a relative value must not block a URL.
        var node = await NodeLocator.LocateAsync(explicitUrl,
            environmentUrl,
            static () => NodeLocator.ResolveDataDirectory("relative/data", Path.GetTempPath()),
            NoProcessIsAlive,
            CancellationToken.None);

        AssertEx.Equal((explicitUrl ?? environmentUrl)!, node.Origin.GetLeftPart(UriPartial.Authority), row);
        AssertEx.Null(node.DataDirectory, row);
    }

    [Test]
    public void ResolveDataDirectory_UsesTheOverrideOrThePlatformFolder_AndRejectsARelativeOverride()
    {
        var root = Path.GetTempPath();

        AssertEx.Equal(Path.Combine(Path.GetFullPath(root), "XE-Local-AI-Engine"), NodeLocator.ResolveDataDirectory(overridden: null, root));
        AssertEx.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), NodeLocator.ResolveDataDirectory(root, "/unused"));
        var failure = AssertEx.Throws<NodeDiscoveryException>(() => NodeLocator.ResolveDataDirectory("relative/data", root));
        AssertEx.Equal(NodeDiscoveryFailure.InvalidDataDirectory, failure.Failure);
    }

    private static bool NoProcessIsAlive(int pid) =>
        false;

    private static string MissingDirectory() =>
        Path.Combine(Path.GetTempPath(), $"xe-cli-missing-{Guid.NewGuid():N}");

    // "key=value|key=value" edits over the valid file: url, mcp, dir, pid.
    private static string Edited(string edits)
    {
        var url = "http://127.0.0.1:5123";
        var mcp = "http://127.0.0.1:5123/api/local/v1/mcp/server";
        string? dir = null;
        var pid = LivePid;
        foreach (var edit in edits.Split('|'))
        {
            var value = edit[(edit.IndexOf('=', StringComparison.Ordinal) + 1)..];
            switch (edit[..edit.IndexOf('=', StringComparison.Ordinal)])
            {
                case "url":
                    url = value;
                    break;
                case "mcp":
                    mcp = value;
                    break;
                case "dir":
                    dir = value;
                    break;
                default:
                    pid = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            version = "1.0.0",
            url,
            mcpUrl = mcp,
            dataDir = dir ?? "{dir}",
            pid
        });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xe-cli-ready-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public Task WriteReadyAsync(string content) =>
            File.WriteAllTextAsync(System.IO.Path.Combine(Path, NodeLocator.ReadyFileName),
                content.Replace("{dir}", JsonEncodedText.Encode(Path).ToString(), StringComparison.Ordinal));

        public void Dispose() =>
            Directory.Delete(Path, recursive: true);
    }
}
