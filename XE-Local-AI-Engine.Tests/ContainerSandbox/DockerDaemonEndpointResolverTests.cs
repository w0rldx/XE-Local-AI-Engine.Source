namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using System.Globalization;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint discovery order. This is not a formality: the resolved source is what the operator is asked to judge
///     before trusting the daemon, so a resolver that silently preferred the wrong socket would make the attestation prompt describe an
///     endpoint nobody chose.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DockerDaemonEndpointResolverTests
{
    /// <summary>The value that must never survive a rendering, whichever component an operator hid it in.</summary>
    private const string Sentinel = "sekrit-9f3a";

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
        // both from the daemon they installed to whichever their shell happens to run — exactly the substitution attestation exists to
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

    [Test]
    public void Resolve_FromAPlainEndpointString_PrefersItOverEnvironment()
    {
        // The overload a consumer that is not Development Mode uses: it holds its own endpoint setting and must not
        // have to construct, or borrow, a ContainerSandboxOptions it does not own.
        var endpoint = Resolve("unix:///configured.sock",
            environment: new Dictionary<string, string>
            {
                ["DOCKER_HOST"] = "unix:///from-env.sock"
            },
            existingPaths: []);

        AssertEx.Equal("/configured.sock", endpoint.UnixSocketPath);
        AssertEx.Equal(DockerDaemonEndpointSource.Configuration, endpoint.Source);
    }

    [Test]
    public void Resolve_FromANullEndpointString_FallsThroughToTheSameDiscoveryOrder()
    {
        var endpoint = Resolve(configuredEndpoint: null,
            environment: new Dictionary<string, string>
            {
                ["DOCKER_HOST"] = "unix:///from-env.sock"
            },
            existingPaths: ["/var/run/docker.sock"]);

        AssertEx.Equal("/from-env.sock", endpoint.UnixSocketPath);
        AssertEx.Equal(DockerDaemonEndpointSource.DockerHostEnvironmentVariable, endpoint.Source);
    }

    [Test]
    [Arguments("unix:///configured.sock")]
    [Arguments("/run/user/1000/docker.sock")]
    [Arguments("   ")]
    [Arguments(null)]
    public void Resolve_ThroughOptions_AndThroughAPlainString_AgreeForTheSameConfiguredValue(string? configuredEndpoint)
    {
        // The generalisation's whole obligation. Two overloads over one discovery order is fine; two discovery orders
        // is how one consumer silently reaches a different daemon than the other, which is exactly the substitution
        // the daemon attestation exists to surface.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOCKER_HOST"] = "unix:///from-env.sock",
            ["XDG_RUNTIME_DIR"] = "/run/user/1000"
        };

        var throughOptions = Resolve(new ContainerSandboxOptions
            {
                DaemonEndpoint = configuredEndpoint
            },
            environment,
            existingPaths: ["/run/user/1000/docker.sock"]);
        var throughString = Resolve(configuredEndpoint, environment, existingPaths: ["/run/user/1000/docker.sock"]);

        AssertEx.Equal(throughOptions, throughString);
    }

    [Test]
    [Arguments("unix:///var/run/docker.sock")]
    [Arguments("npipe://./pipe/docker_engine")]
    [Arguments("tcp://10.0.0.4:2375")]
    public void Display_WithoutUserInformation_IsTheUriUnchanged(string raw)
    {
        // Every operator-facing daemon message renders through Display, and Development Mode's messages are held
        // verbatim. Redaction must therefore be invisible to an endpoint that carries nothing to redact.
        var endpoint = new DockerDaemonEndpoint(new Uri(raw), DockerDaemonEndpointSource.Configuration);

        AssertEx.Equal(endpoint.Uri.ToString(), endpoint.Display);
    }

    [Test]
    public void Display_WithUserInformation_DropsIt()
    {
        // DOCKER_HOST is an environment variable and a tcp:// endpoint can carry credentials in its authority. The
        // endpoint is refused before anything connects, but the refusal is logged and returned — so the rendering
        // itself has to be safe, or declining to use the daemon is how the password gets written down.
        var endpoint = new DockerDaemonEndpoint(new Uri($"tcp://someone:{Sentinel}@10.0.0.4:2375"),
            DockerDaemonEndpointSource.DockerHostEnvironmentVariable);

        AssertEx.False(endpoint.Display.Contains(Sentinel, StringComparison.Ordinal),
            $"The endpoint rendered as '{endpoint.Display}', which still carries the credential.");
        AssertEx.False(endpoint.Display.Contains("someone", StringComparison.Ordinal),
            "The user name survived redaction; user information is dropped whole, not split.");
        AssertEx.Contains(endpoint.Display, "10.0.0.4:2375");
    }

    [Test]
    [Arguments("tcp://10.0.0.4:2375/?token={0}", "10.0.0.4:2375")]
    [Arguments("tcp://10.0.0.4:2375/#{0}", "10.0.0.4:2375")]
    [Arguments("unix:///var/run/docker.sock?token={0}", "/var/run/docker.sock")]
    public void Display_WithAQueryOrFragment_DropsIt(string template, string survives)
    {
        // The half the user-information redaction missed. A query string and a fragment address nothing on a Docker
        // daemon, are equally operator-supplied, and are equally somewhere a token fits — and unlike user information
        // they survive on a LOCAL socket, which no transport check refuses.
        var raw = string.Format(CultureInfo.InvariantCulture, template, Sentinel);
        var endpoint = new DockerDaemonEndpoint(new Uri(raw), DockerDaemonEndpointSource.DockerHostEnvironmentVariable);

        AssertEx.False(endpoint.Display.Contains(Sentinel, StringComparison.Ordinal),
            $"The endpoint rendered as '{endpoint.Display}', which still carries the value.");
        AssertEx.Contains(endpoint.Display, survives);
    }

    [Test]
    [Arguments("tcp://someone:{0}@10.0.0.4:2375", "user information")]
    [Arguments("tcp://10.0.0.4:2375/?token={0}", "a query string")]
    [Arguments("tcp://10.0.0.4:2375/#{0}", "a fragment")]
    [Arguments("unix:///var/run/docker.sock?token={0}", "a query string")]
    public void DisclosingComponent_NamesTheComponentAndNeverItsValue(string template, string expected)
    {
        // The word both refusals use. Naming the component is the whole point: an operator who is told only "the
        // endpoint is refused" goes looking for a syntax error, and one who is shown the value has been disclosed to.
        var raw = string.Format(CultureInfo.InvariantCulture, template, Sentinel);
        var endpoint = new DockerDaemonEndpoint(new Uri(raw), DockerDaemonEndpointSource.DockerHostEnvironmentVariable);

        AssertEx.Equal(expected, endpoint.DisclosingComponent);
    }

    [Test]
    [Arguments("unix:///var/run/docker.sock")]
    [Arguments("npipe://./pipe/docker_engine")]
    [Arguments("tcp://10.0.0.4:2375")]
    public void DisclosingComponent_ForAnOrdinaryEndpoint_IsNull(string raw)
    {
        // The negative control for the refusals: every endpoint this node resolves on its own has to pass, or the
        // check would refuse the conventional socket.
        AssertEx.Null(new DockerDaemonEndpoint(new Uri(raw), DockerDaemonEndpointSource.DefaultUnixSocket).DisclosingComponent);
    }

    [Test]
    public void Redact_ForAStringThatIsNotAnAbsoluteUri_IsAPlaceholderRatherThanTheValue()
    {
        // The pin on disk is hand-editable and a string, so it can hold something with no components to strip. It is
        // replaced rather than echoed: a value this cannot take apart is exactly the one whose shape is unknown.
        var redacted = DockerDaemonEndpoint.Redact("docker daemon over there, token=" + Sentinel);

        AssertEx.Equal(DockerDaemonEndpoint.UnparsableEndpoint, redacted);
        AssertEx.False(redacted.Contains(Sentinel, StringComparison.Ordinal), "The unparsable pin was echoed back.");
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

    private static DockerDaemonEndpoint Resolve(string? configuredEndpoint,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> existingPaths)
    {
        return DockerDaemonEndpointResolver.Resolve(configuredEndpoint,
            name => environment.TryGetValue(name, out var value) ? value : null,
            path => existingPaths.Contains(path, StringComparer.Ordinal),
            isWindows: false);
    }
}
