namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     Resolves the Docker daemon endpoint this node will talk to, in a fixed, reportable order. Discovery is
///     deliberately shallow — it does not read <c>~/.docker/config.json</c> contexts, and it does not probe: it names
///     one endpoint and says where that name came from, and the preflight then decides whether the operator has
///     approved it.
///     <para>
///         Order: explicit engine configuration, then <c>DOCKER_HOST</c>, then the platform default. The environment
///         variable outranks the default because that is what every Docker tool does and an operator who sets it means
///         it; it does not outrank engine configuration, because engine configuration is the one input a stray shell
///         export cannot reach.
///     </para>
/// </summary>
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
        ArgumentNullException.ThrowIfNull(environmentReader);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (TryParseEndpoint(options.DaemonEndpoint, out var configured))
        {
            return new DockerDaemonEndpoint(configured, DockerDaemonEndpointSource.Configuration);
        }

        if (TryParseEndpoint(environmentReader(DockerHostVariable), out var fromEnvironment))
        {
            return new DockerDaemonEndpoint(fromEnvironment, DockerDaemonEndpointSource.DockerHostEnvironmentVariable);
        }

        if (isWindows)
        {
            return new DockerDaemonEndpoint(new Uri(WindowsNamedPipeEndpoint), DockerDaemonEndpointSource.WindowsNamedPipe);
        }

        // The per-user socket is only preferred when the system-wide one is genuinely absent. Preferring it whenever
        // it exists would silently move an operator who has both from the daemon they installed to the one their shell
        // happens to run — exactly the substitution the daemon attestation exists to make visible rather than to perform.
        if (!fileExists(DefaultUnixSocketPath))
        {
            var runtimeDirectory = environmentReader(UserRuntimeDirectoryVariable);
            if (!string.IsNullOrWhiteSpace(runtimeDirectory))
            {
                var userSocket = Path.Combine(runtimeDirectory, "docker.sock");
                if (fileExists(userSocket))
                {
                    return new DockerDaemonEndpoint(BuildUnixEndpoint(userSocket), DockerDaemonEndpointSource.UserRuntimeUnixSocket);
                }
            }
        }

        return new DockerDaemonEndpoint(BuildUnixEndpoint(DefaultUnixSocketPath), DockerDaemonEndpointSource.DefaultUnixSocket);
    }

    /// <summary>Production entry point: reads the real environment and filesystem.</summary>
    public static DockerDaemonEndpoint Resolve(ContainerSandboxOptions options)
    {
        return Resolve(options,
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

        // A bare absolute path is accepted as a Unix socket. `DOCKER_HOST=/run/user/1000/docker.sock` is a thing
        // people write, and rejecting it as "malformed" would send an operator hunting for a syntax error rather than
        // telling them which daemon they reached.
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
