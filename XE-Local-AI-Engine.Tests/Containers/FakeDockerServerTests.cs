namespace XE_Local_AI_Engine.Tests.Containers;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using XE_Local_AI_Engine.Testing.FakeDocker;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The fake Docker Engine API server's own lifecycle and state model, proved without the wire client.
///     <para>
///         These are the claims every other fake-server test rests on and none of them assert: that the server really
///         binds a loopback port of its own rather than answering from memory, that disposing it really releases that
///         port, and that the scenario knobs a test sets are the ones the endpoints will read.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class FakeDockerServerTests
{
    [Test]
    public async Task StartAsync_BindsADynamicLoopbackPortThatReallyAccepts()
    {
        await using var docker = await FakeDockerServer.StartAsync();

        AssertEx.Equal("http", docker.BaseAddress.Scheme);
        AssertEx.Equal("127.0.0.1", docker.BaseAddress.Host);
        AssertEx.True(docker.BaseAddress.Port > 0, "The fake daemon did not publish a bound port.");

        // A real TCP connect, not a request: the point of this fixture over an in-memory handler is that the
        // production client opens a socket, and a fake that only answered an HttpClient would prove nothing about
        // the transport classification that client wraps every failure in.
        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, docker.BaseAddress.Port);
        AssertEx.True(probe.Connected, "The fake daemon published a port that refuses connections.");
    }

    [Test]
    public async Task DisposeAsync_ReleasesThePort()
    {
        var docker = await FakeDockerServer.StartAsync();
        var port = docker.BaseAddress.Port;
        await docker.DisposeAsync();

        using var probe = new TcpClient();
        var exception = await AssertEx.ThrowsAsync<SocketException>(async () => await probe.ConnectAsync(IPAddress.Loopback, port));

        AssertEx.Equal(SocketError.ConnectionRefused, exception.SocketErrorCode);
    }

    [Test]
    public async Task StartAsync_TakesItsDaemonIdentityFromTheOptions()
    {
        await using var docker = await FakeDockerServer.StartAsync(new FakeDockerOptions
        {
            DaemonId = "fakedaemon:pinned",
            ApiVersion = "1.41",
            Rootless = true,
            SupportsSeccomp = false
        });

        AssertEx.Equal("fakedaemon:pinned", docker.State.DaemonId);
        AssertEx.Equal("1.41", docker.State.ApiVersion);
        AssertEx.True(docker.State.Rootless, "The rootless option did not reach the state the /info route reads.");
        AssertEx.False(docker.State.SupportsSeccomp, "The seccomp option did not reach the state the /info route reads.");
    }

    [Test]
    public async Task DefaultOptions_ReportAnApiVersionTheProductionMinimumAccepts()
    {
        // ContainerRuntimeOptions.MinimumApiVersion defaults to "1.41" and is compared component-wise, so a fake
        // that defaulted to, say, "1.9" would fail every resolution that goes through the daemon probe.
        await using var docker = await FakeDockerServer.StartAsync();

        var parts = docker.State.ApiVersion.Split('.');
        AssertEx.Equal(expected: 2, parts.Length, "The fake daemon's API version is not in major.minor form.");
        AssertEx.Equal(expected: 1, int.Parse(parts[0], CultureInfo.InvariantCulture));
        AssertEx.True(int.Parse(parts[1], CultureInfo.InvariantCulture) >= 41,
            $"The fake daemon reports API version {docker.State.ApiVersion}, below the production minimum of 1.41.");

        AssertEx.NotNullOrEmpty(docker.State.DaemonId);
        AssertEx.Equal("linux", docker.State.OperatingSystem);
    }

    [Test]
    public async Task ResolveExec_PrefersTheContainerScriptThenTheWildcardThenAZeroExit()
    {
        await using var docker = await FakeDockerServer.StartAsync();

        docker.State.ScriptExec(FakeDockerState.AnyContainer, "sh -c probe", exitCode: 1, standardError: "wildcard");
        docker.State.ScriptExec("container-a", "sh -c probe", exitCode: 7, standardOutput: "specific");

        AssertEx.Equal(expected: 7, docker.State.ResolveExec("container-a", "sh -c probe").ExitCode);
        AssertEx.Equal("specific", docker.State.ResolveExec("container-a", "sh -c probe").StandardOutput);
        AssertEx.Equal(expected: 1, docker.State.ResolveExec("container-b", "sh -c probe").ExitCode);
        AssertEx.Equal(expected: 0, docker.State.ResolveExec("container-a", "sh -c other").ExitCode);
    }

    [Test]
    public async Task ResolveExec_AnswersTheCommandWildcardWhenNoCommandMatches()
    {
        // The write probe's command is a shell fragment the client builds itself and no caller supplies, so the
        // command wildcard is the only way a test can script its outcome without copying that fragment verbatim.
        await using var docker = await FakeDockerServer.StartAsync();

        docker.State.ScriptExec("container-a", FakeDockerState.AnyCommand, exitCode: 5);

        AssertEx.Equal(expected: 5, docker.State.ResolveExec("container-a", "anything at all").ExitCode);
        AssertEx.Equal(expected: 0, docker.State.ResolveExec("container-b", "anything at all").ExitCode);

        // A command-specific script still wins over the wildcard: the wildcard is a fallback, not an override.
        docker.State.ScriptExec("container-a", "anything at all", exitCode: 9);
        AssertEx.Equal(expected: 9, docker.State.ResolveExec("container-a", "anything at all").ExitCode);
    }

    [Test]
    public async Task NextEphemeralPort_HandsOutADistinctPortEachTime()
    {
        // Two published ports that read back as one port would hide a mapping that lost one of them.
        await using var docker = await FakeDockerServer.StartAsync();

        var ports = Enumerable.Range(start: 0, count: 4).Select(_ => docker.State.NextEphemeralPort()).ToArray();

        AssertEx.Equal(ports.Length, ports.Distinct().Count(), "The fake daemon handed out the same host port twice.");
        AssertEx.True(ports.All(port => port is >= 32768 and <= 65535),
            $"A daemon-assigned port fell outside the ephemeral range: {string.Join(", ", ports)}.");
    }

    [Test]
    public async Task ResolvePull_DefaultsToATwoLayerStreamWhoseNarrationLineIsNotALayer()
    {
        await using var docker = await FakeDockerServer.StartAsync();

        var lines = docker.State.ResolvePull("busybox@sha256:" + new string('a', count: 64));

        // The opening narration line carries an id and is still not a layer. PullProgressAggregator special-cases
        // exactly this string, and a fake that never produced it would leave that special case untested forever.
        var opening = lines[0];
        AssertEx.NotNullOrEmpty(opening.Id);
        AssertEx.Contains(opening.Status, "Pulling from");

        var layerIds = lines.Where(line => line.Id is not null && line.Status?.StartsWith("Pulling from", StringComparison.Ordinal) != true)
                            .Select(line => line.Id!)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
        AssertEx.Equal(expected: 2, layerIds.Length, "The default pull script does not describe exactly two layers.");

        foreach (var layerId in layerIds)
        {
            AssertEx.Contains(lines, line => string.Equals(line.Id, layerId, StringComparison.Ordinal)
                                             && line.Status?.StartsWith("Pull complete", StringComparison.Ordinal) == true,
                $"Layer '{layerId}' never completes, so a pull against the default script would end with layers outstanding.");
        }
    }

    [Test]
    public async Task ScriptPull_ReplacesTheDefaultStreamForThatImageOnly()
    {
        await using var docker = await FakeDockerServer.StartAsync();

        docker.State.ScriptPull("scripted@sha256:abc",
            new FakeDockerPullLine
            {
                Error = "manifest unknown"
            });

        var scripted = docker.State.ResolvePull("scripted@sha256:abc");
        AssertEx.Equal(expected: 1, scripted.Count);
        AssertEx.Equal("manifest unknown", scripted[0].Error);

        AssertEx.True(docker.State.ResolvePull("other@sha256:abc").Count > 1,
            "Scripting one image's pull replaced the default stream of another image too.");
    }

    [Test]
    public async Task SeedImageAndSeedNetwork_PopulateTheStoresTheRoutesRead()
    {
        await using var docker = await FakeDockerServer.StartAsync();

        var image = docker.State.SeedImage("redis@sha256:abc", "/data");
        var network = docker.State.SeedNetwork("foreign-net", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owner"] = "someone-else"
        });

        AssertEx.Contains(docker.State.Images["redis@sha256:abc"].DeclaredVolumes, "/data");
        AssertEx.Equal("redis@sha256:abc", image.Reference);
        AssertEx.Equal("someone-else", docker.State.Networks["foreign-net"].Labels["owner"]);
        AssertEx.NotNullOrEmpty(network.Id);
        AssertEx.Empty(network.AttachedContainerIds);
    }

    [Test]
    public async Task LastQueryValue_ReadsTheMostRecentMatchingRequest()
    {
        await using var docker = await FakeDockerServer.StartAsync();

        docker.State.Record(new FakeDockerRequest
        {
            Method = "POST",
            Path = "/containers/a/stop",
            Query = Query("t", "30")
        });
        docker.State.Record(new FakeDockerRequest
        {
            Method = "POST",
            Path = "/containers/b/stop",
            Query = Query("t", "5")
        });

        AssertEx.Equal("5", docker.State.LastQueryValue("/stop", "t"));
        AssertEx.Null(docker.State.LastQueryValue("/start", "t"));
        AssertEx.Equal(expected: 2, docker.RecordedRequests.Count);
    }

    private static IReadOnlyDictionary<string, string> Query(string key, string value)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [key] = value
        };
    }
}
