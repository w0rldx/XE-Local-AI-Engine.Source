namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint discovery order. This is not a formality: the resolved source is what the operator is asked to judge
///     before trusting the daemon, so a resolver that silently preferred the wrong socket would make the attestation prompt describe an
///     endpoint nobody chose.
/// </summary>
public sealed class DockerDaemonEndpointResolverTests
{
    [Test]
    public void Resolve_PrefersConfigurationOverEnvironment()
    {
        var endpoint = Resolve(new ContainerSandboxOptions
            {
                DaemonEndpoint = "unix:///configured.sock"
            },
            environment: new Dictionary<string, string>
            {
                ["DOCKER_HOST"] = "unix:///from-env.sock"
            },
            existingPaths: []);

        AssertEx.Equal("/configured.sock", endpoint.UnixSocketPath);
        AssertEx.Equal(DockerDaemonEndpointSource.Configuration, endpoint.Source);
    }

    [Test]
    public void Resolve_WhenDockerHostIsSet_UsesItAndSaysSo()
    {
        var endpoint = Resolve(new ContainerSandboxOptions(),
            environment: new Dictionary<string, string>
            {
                ["DOCKER_HOST"] = "tcp://10.0.0.5:2375"
            },
            existingPaths: ["/var/run/docker.sock"]);

        AssertEx.Equal("tcp://10.0.0.5:2375/", endpoint.Uri.ToString());
        // The source, not just the endpoint. An operator asked to approve a daemon needs to know it was named by an
        // environment variable rather than chosen by this node.
        AssertEx.Equal(DockerDaemonEndpointSource.DockerHostEnvironmentVariable, endpoint.Source);
    }

    [Test]
    public void Resolve_AcceptsABareSocketPathInDockerHost()
    {
        var endpoint = Resolve(new ContainerSandboxOptions(),
            environment: new Dictionary<string, string>
            {
                ["DOCKER_HOST"] = "/run/user/1000/docker.sock"
            },
            existingPaths: []);

        AssertEx.Equal("/run/user/1000/docker.sock", endpoint.UnixSocketPath);
        AssertEx.Equal(DockerDaemonEndpointSource.DockerHostEnvironmentVariable, endpoint.Source);
    }

    [Test]
    public void Resolve_WhenTheSystemSocketExists_PrefersItOverAPerUserSocket()
    {
        // The load-bearing direction. Preferring a per-user socket whenever one exists would move an operator who has
        // both from the daemon they installed to whichever their shell happens to run — a substitution D10 exists to
        // surface, not to perform silently.
        var endpoint = Resolve(new ContainerSandboxOptions(),
            environment: new Dictionary<string, string>
            {
                ["XDG_RUNTIME_DIR"] = "/run/user/1000"
            },
            existingPaths: ["/var/run/docker.sock", "/run/user/1000/docker.sock"]);

        AssertEx.Equal("/var/run/docker.sock", endpoint.UnixSocketPath);
        AssertEx.Equal(DockerDaemonEndpointSource.DefaultUnixSocket, endpoint.Source);
    }

    [Test]
    public void Resolve_WhenOnlyThePerUserSocketExists_UsesIt()
    {
        if (OperatingSystem.IsWindows())
        {
            // The rootless per-user socket under XDG_RUNTIME_DIR is a Linux concept; on Windows the resolver reaches
            // for the npipe endpoint instead and never considers it.
            Skip.Test("The per-user Docker socket under XDG_RUNTIME_DIR exists on Linux only.");
        }

        var endpoint = Resolve(new ContainerSandboxOptions(),
            environment: new Dictionary<string, string>
            {
                ["XDG_RUNTIME_DIR"] = "/run/user/1000"
            },
            existingPaths: ["/run/user/1000/docker.sock"]);

        AssertEx.Equal("/run/user/1000/docker.sock", endpoint.UnixSocketPath);
        AssertEx.Equal(DockerDaemonEndpointSource.UserRuntimeUnixSocket, endpoint.Source);
    }

    [Test]
    public void Resolve_WhenNothingExists_StillNamesTheDefaultSocketSoTheFailureCanNameIt()
    {
        var endpoint = Resolve(new ContainerSandboxOptions(), environment: new Dictionary<string, string>(StringComparer.Ordinal), existingPaths: []);

        AssertEx.Equal("/var/run/docker.sock", endpoint.UnixSocketPath);
        AssertEx.Equal(DockerDaemonEndpointSource.DefaultUnixSocket, endpoint.Source);
    }

    [Test]
    public void Resolve_OnWindows_UsesTheNamedPipe()
    {
        var endpoint = DockerDaemonEndpointResolver.Resolve(new ContainerSandboxOptions(),
            _ => null,
            _ => false,
            isWindows: true);

        AssertEx.Equal(DockerDaemonEndpointSource.WindowsNamedPipe, endpoint.Source);
        AssertEx.Contains(endpoint.Uri.ToString(), "npipe");
    }

    private static DockerDaemonEndpoint Resolve(ContainerSandboxOptions options,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> existingPaths)
    {
        return DockerDaemonEndpointResolver.Resolve(options,
            name => environment.TryGetValue(name, out var value) ? value : null,
            path => existingPaths.Contains(path, StringComparer.Ordinal),
            isWindows: false);
    }
}
