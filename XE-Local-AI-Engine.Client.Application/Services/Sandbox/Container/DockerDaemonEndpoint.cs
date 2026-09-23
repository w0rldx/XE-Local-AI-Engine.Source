namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>Where a resolved Docker daemon endpoint came from.</summary>
/// <remarks>
///     Reported to the operator and persisted with the attestation, because "a socket was found" is not "the operator intended this
///     daemon", and the difference between the two is almost entirely which of these values produced the endpoint.
/// </remarks>
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
public sealed record DockerDaemonEndpoint
{
    /// <summary>The endpoint URI (<c>unix://</c>, <c>npipe://</c> or <c>tcp://</c>).</summary>
    public required Uri Uri { get; init; }

    /// <summary>Which discovery step produced it.</summary>
    public required DockerDaemonEndpointSource Source { get; init; }

    /// <summary>What <see cref="Redact(string)" /> renders for a stored endpoint that is not an absolute URI.</summary>
    internal const string UnparsableEndpoint = "an endpoint that is not a URI";

    /// <summary>
    ///     For a <c>unix://</c> endpoint, the filesystem path of the socket; null otherwise. Used to tell "there is no
    ///     socket at that path" apart from "there is a socket and it refused us", which are different operator actions.
    /// </summary>
    public string? UnixSocketPath => Uri.Scheme.Equals("unix", StringComparison.OrdinalIgnoreCase) ? Uri.LocalPath : null;

    /// <summary>
    ///     For a local <c>npipe://./pipe/NAME</c> endpoint, NAME; null otherwise, a remote pipe included. The named-pipe
    ///     counterpart of <see cref="UnixSocketPath" />, for the same "is anything there" question.
    /// </summary>
    internal string? NamedPipeName =>
        Uri.Scheme.Equals("npipe", StringComparison.OrdinalIgnoreCase)
        && Uri.Host == "."
        && Uri.LocalPath.StartsWith(NamedPipePrefix, StringComparison.OrdinalIgnoreCase)
            ? Uri.LocalPath[NamedPipePrefix.Length..]
            : null;

    private const string NamedPipePrefix = "/pipe/";

    /// <summary>A stable, log-safe rendering: scheme, host, port and path, and nothing else.</summary>
    /// <remarks>
    ///     An endpoint is not always a local socket — <c>tcp://user:secret@host:2375/?token=…</c> is a <c>DOCKER_HOST</c> an operator can
    ///     set — and this string reaches logs, resolution records, the persisted pin, operator messages and a 503 body, on paths that
    ///     refuse that endpoint before connecting to it, so echoing the secret would disclose it while declining to use it. User
    ///     information, query and fragment are dropped WHOLE: all operator-supplied, none addressing a daemon, each somewhere a token
    ///     fits. An endpoint carrying none of the three renders exactly as <see cref="Uri.ToString" />, so no existing message changes.
    /// </remarks>
    public string Display => Redact(Uri);

    /// <summary>
    ///     The component this endpoint carries that must never be rendered — named, never quoted — or null when it carries none.
    /// </summary>
    /// <remarks>
    ///     Both consumers of a daemon refuse such an endpoint before a client exists, and the refusal says which component is at fault
    ///     without repeating what was in it. One member rather than a predicate per consumer: the check and the word the refusal uses are
    ///     the same fact, and a consumer re-deriving either would be the one that drifts.
    /// </remarks>
    internal string? DisclosingComponent
    {
        get
        {
            if (!string.IsNullOrEmpty(Uri.UserInfo))
            {
                return "user information";
            }

            if (!string.IsNullOrEmpty(Uri.Query))
            {
                return "a query string";
            }

            return string.IsNullOrEmpty(Uri.Fragment) ? null : "a fragment";
        }
    }

    /// <summary>
    ///     The redaction <see cref="Display" /> is, exposed so an endpoint that was persisted as a plain string can be
    ///     put through the same rendering on its way back in.
    /// </summary>
    internal static string Redact(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }

    /// <summary>The same redaction for an endpoint that is already a string.</summary>
    /// <remarks>
    ///     A pin written before the redaction existed holds whatever <c>DOCKER_HOST</c> held, so the value coming back off disk is not
    ///     covered by new pins being written through <see cref="Display" />. A string that is not an absolute URI cannot be taken apart
    ///     into components, so it renders as <see cref="UnparsableEndpoint" /> rather than being echoed: a hand-edited pin is precisely
    ///     where a value this cannot parse would sit.
    /// </remarks>
    internal static string Redact(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? Redact(uri) : UnparsableEndpoint;
    }
}
