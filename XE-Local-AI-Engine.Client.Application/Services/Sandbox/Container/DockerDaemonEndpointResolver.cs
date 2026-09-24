namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>Resolves the Docker daemon endpoint this node will talk to, in a fixed, reportable order.</summary>
/// <remarks>
///     Discovery is deliberately shallow: it reads no <c>~/.docker/config.json</c> contexts and does not probe, naming one endpoint and
///     where that name came from, and the preflight then decides whether the operator approved it. The order is explicit engine
///     configuration, then <c>DOCKER_HOST</c>, then the platform default — the variable outranks the default because that is what every
///     Docker tool does, and does not outrank engine configuration, which is the one input a stray shell export cannot reach.
/// </remarks>
internal static class DockerDaemonEndpointResolver
{
    internal const string DockerHostVariable = "DOCKER_HOST";
    internal const string UserRuntimeDirectoryVariable = "XDG_RUNTIME_DIR";
    internal const string DefaultUnixSocketPath = "/var/run/docker.sock";
    internal const string WindowsNamedPipeEndpoint = "npipe://./pipe/docker_engine";

    /// <summary>
    ///     Resolve the endpoint. <paramref name="environmentReader" /> and <paramref name="fileExists" /> are injected
    ///     so the resolution order is testable without mutating the test process's environment.
    /// </summary>
    public static DockerDaemonEndpoint Resolve(ContainerSandboxOptions options,
        Func<string, string?> environmentReader,
        Func<string, bool> fileExists,
        bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Resolve(options.DaemonEndpoint, environmentReader, fileExists, isWindows);
    }

    /// <summary>Resolve the endpoint from a plain configured endpoint string rather than from Development Mode's options record.</summary>
    /// <param name="configuredEndpoint">
    ///     The consumer's explicit endpoint setting, or null when it has none. Pass a typed <c>(string?)null</c>, a bare <c>null</c>
    ///     being CS0121-ambiguous against the <see cref="ContainerSandboxOptions" /> overload.
    /// </param>
    /// <remarks>
    ///     The discovery order is a property of this HOST, not of any one consumer, so a second consumer supplies its own configured value
    ///     here rather than constructing a <see cref="ContainerSandboxOptions" /> it does not own or forking a resolver that would drift
    ///     the first time a platform default changed.
    /// </remarks>
    /// <param name="environmentReader">Reads an environment variable; injected so the order is testable.</param>
    /// <param name="fileExists">Whether a path exists; injected so the order is testable.</param>
    /// <param name="isWindows">Whether the engine is running on Windows.</param>
    public static DockerDaemonEndpoint Resolve(string? configuredEndpoint,
        Func<string, string?> environmentReader,
        Func<string, bool> fileExists,
        bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(environmentReader);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (TryParseEndpoint(configuredEndpoint, out var configured))
        {
            return new DockerDaemonEndpoint
            {
                Uri = configured,
                Source = DockerDaemonEndpointSource.Configuration
            };
        }

        if (TryParseEndpoint(environmentReader(DockerHostVariable), out var fromEnvironment))
        {
            return new DockerDaemonEndpoint
            {
                Uri = fromEnvironment,
                Source = DockerDaemonEndpointSource.DockerHostEnvironmentVariable
            };
        }

        if (isWindows)
        {
            return new DockerDaemonEndpoint
            {
                Uri = new Uri(WindowsNamedPipeEndpoint),
                Source = DockerDaemonEndpointSource.WindowsNamedPipe
            };
        }

        // The per-user socket is preferred only when the system-wide one is genuinely absent: preferring it whenever it exists would move
        // an operator who has both from the daemon they installed to the one their shell runs — the substitution attestation reveals.
        if (!fileExists(DefaultUnixSocketPath))
        {
            var runtimeDirectory = environmentReader(UserRuntimeDirectoryVariable);
            if (!string.IsNullOrWhiteSpace(runtimeDirectory))
            {
                var userSocket = Path.Combine(runtimeDirectory, "docker.sock");
                if (fileExists(userSocket))
                {
                    return new DockerDaemonEndpoint
                    {
                        Uri = BuildUnixEndpoint(userSocket),
                        Source = DockerDaemonEndpointSource.UserRuntimeUnixSocket
                    };
                }
            }
        }

        return new DockerDaemonEndpoint
        {
            Uri = BuildUnixEndpoint(DefaultUnixSocketPath),
            Source = DockerDaemonEndpointSource.DefaultUnixSocket
        };
    }

    /// <summary>Production entry point: reads the real environment and filesystem.</summary>
    public static DockerDaemonEndpoint Resolve(ContainerSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Resolve(options.DaemonEndpoint);
    }

    /// <summary>Production entry point for a consumer that holds its own endpoint setting rather than Development Mode's.</summary>
    public static DockerDaemonEndpoint Resolve(string? configuredEndpoint)
    {
        return Resolve(configuredEndpoint,
            Environment.GetEnvironmentVariable,
            static path => File.Exists(path) || Directory.Exists(path),
            OperatingSystem.IsWindows());
    }

    /// <summary>A <c>unix://</c> URI for an absolute socket path, in the triple-slash form the client expects.</summary>
    internal static Uri BuildUnixEndpoint(string socketPath)
    {
        return new Uri("unix://" + socketPath);
    }

    private static bool TryParseEndpoint(string? raw, out Uri endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();

        // A bare absolute path is accepted as a Unix socket: people write `DOCKER_HOST=/run/user/1000/docker.sock`, and rejecting it as
        // malformed would send an operator hunting for a syntax error rather than telling them which daemon they reached.
        if (trimmed.StartsWith('/'))
        {
            endpoint = BuildUnixEndpoint(trimmed);
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }
}
