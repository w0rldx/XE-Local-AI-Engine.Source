namespace XE_Local_AI_Engine.Tests.Containers;

using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The named-pipe preflight a stopped Docker Desktop on Windows goes through: the pipe name an endpoint yields,
///     the namespace lookup, and that only Windows short-circuits on it. Off Windows the real transport answers.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class DockerNamedPipePreflightTests
{
    [Test]
    [Arguments("npipe://./pipe/docker_engine", "docker_engine")]
    [Arguments("npipe://./pipe/dockerDesktopLinuxEngine", "dockerDesktopLinuxEngine")]
    [Arguments("npipe://build-server/pipe/docker_engine", null)]
    [Arguments("unix:///var/run/docker.sock", null)]
    [Arguments("tcp://127.0.0.1:2375", null)]
    public async Task NamedPipeName_IsSetOnlyForALocalPipe(string endpoint, string? expected)
    {
        var name = new DockerDaemonEndpoint
        {
            Uri = new Uri(endpoint),
            Source = DockerDaemonEndpointSource.Configuration
        }.NamedPipeName;

        AssertEx.True(string.Equals(expected, name, StringComparison.Ordinal), $"expected [{expected}], got [{name}]");
        await Task.CompletedTask;
    }

    [Test]
    public async Task NamedPipeExists_ReportsAMissingPipeOnWindowsAndNeverClaimsAbsenceElsewhere()
    {
        var missing = "xe-no-such-pipe-" + Guid.NewGuid().ToString("N");

        // Windows lists its pipe namespace, so a fresh name is absent. Elsewhere the namespace cannot be read, and
        // "cannot tell" must not turn into a DaemonUnreachable the transport never produced.
        AssertEx.Equal(!OperatingSystem.IsWindows(), DockerDotNetRuntimeClient.NamedPipeExists(missing));
        await Task.CompletedTask;
    }

    [Test]
    public async Task OnWindows_AMissingNamedPipe_ShortCircuitsToDaemonUnreachable()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SkipTestException("SKIPPED — the named-pipe preflight runs only on Windows; "
                                        + nameof(OffWindows_ANamedPipeEndpoint_IsNotShortCircuited) + " covers this host.");
        }

        await using var client = ClientFor($"npipe://./pipe/xe-no-such-pipe-{Guid.NewGuid():N}");

        var failure = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => client.ProbeAsync());

        AssertEx.Equal(DockerDaemonPreflightStatus.DaemonUnreachable, failure.Status);
        AssertEx.Contains(failure.Message, "No Docker named pipe exists");
    }

    [Test]
    public async Task OffWindows_ANamedPipeEndpoint_IsNotShortCircuited()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new SkipTestException("SKIPPED — on Windows the preflight is live; "
                                        + nameof(OnWindows_AMissingNamedPipe_ShortCircuitsToDaemonUnreachable) + " covers this host.");
        }

        await using var client = ClientFor("npipe://./pipe/docker_engine");

        // Whatever the transport says off Windows, it is the transport speaking: the preflight must not have run.
        var failure = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => client.ProbeAsync());

        AssertEx.False(failure.Message.Contains("No Docker named pipe exists", StringComparison.Ordinal), failure.Message);
    }

    private static DockerDotNetRuntimeClient ClientFor(string endpoint)
    {
        // DockerClientBuilder opens nothing at construction; only ProbeAsync touches the transport.
        return new DockerDotNetRuntimeClient(new DockerDaemonEndpoint
            {
                Uri = new Uri(endpoint),
                Source = DockerDaemonEndpointSource.Configuration
            },
            TimeSpan.FromSeconds(1),
            TimeProvider.System);
    }
}
