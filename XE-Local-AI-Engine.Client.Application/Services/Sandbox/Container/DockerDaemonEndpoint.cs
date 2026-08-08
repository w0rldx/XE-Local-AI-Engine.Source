namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     Where a resolved Docker daemon endpoint came from. This is reported to the operator and persisted with the
///     attestation because "a socket was found" is not "the operator intended this daemon" — and the
///     difference between the two is almost entirely which of these values produced the endpoint.
/// </summary>
public enum DockerDaemonEndpointSource
{
    /// <summary>Explicit engine configuration (<c>Development:ContainerSandbox:DaemonEndpoint</c>). The strongest signal of intent.</summary>
    Configuration = 0,

    /// <summary>The <c>DOCKER_HOST</c> environment variable — user-controllable, and the reason the daemon attestation exists.</summary>
    DockerHostEnvironmentVariable = 1,

    /// <summary>The conventional system-wide Unix socket at <c>/var/run/docker.sock</c>.</summary>
    DefaultUnixSocket = 2,

    /// <summary>A per-user socket under <c>$XDG_RUNTIME_DIR</c>, the layout a rootless installation produces.</summary>
    UserRuntimeUnixSocket = 3,

    /// <summary>The conventional Windows named pipe at <c>npipe://./pipe/docker_engine</c>.</summary>
    WindowsNamedPipe = 4
}

/// <summary>
///     A Docker daemon endpoint together with how it was arrived at. Both halves matter: the endpoint is what gets
///     connected to, and the source is what the operator is asked to judge when the attestation no longer matches.
/// </summary>
/// <param name="Uri">The endpoint URI (<c>unix://</c>, <c>npipe://</c> or <c>tcp://</c>).</param>
/// <param name="Source">Which discovery step produced it.</param>
public sealed record DockerDaemonEndpoint(Uri Uri, DockerDaemonEndpointSource Source)
{
    /// <summary>
    ///     For a <c>unix://</c> endpoint, the filesystem path of the socket; null otherwise. Used to tell "there is no
    ///     socket at that path" apart from "there is a socket and it refused us", which are different operator actions.
    /// </summary>
    public string? UnixSocketPath => Uri.Scheme.Equals("unix", StringComparison.OrdinalIgnoreCase) ? Uri.LocalPath : null;

    /// <summary>A stable, log-safe rendering. A daemon endpoint carries no credentials, so no redaction is required.</summary>
    public string Display => Uri.ToString();
}
