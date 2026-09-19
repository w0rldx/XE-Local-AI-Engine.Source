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
    ///     A stable, log-safe rendering: scheme, host, port and path, and nothing else.
    ///     <para>
    ///         A local socket carries no secrets, but an endpoint is not always a local socket: a <c>DOCKER_HOST</c>
    ///         of <c>tcp://user:secret@host:2375/?token=…</c> is a value an operator can set, and this string reaches
    ///         logs, resolution records, the persisted pin, operator messages and the 503 body an API returns — on
    ///         paths that refuse that endpoint before they ever connect to it. Echoing the secret while refusing the
    ///         endpoint would disclose it in the course of declining to use it. User information, the query and the
    ///         fragment are dropped whole: all three are operator-supplied, none of them addresses a Docker daemon, and
    ///         any of the three is somewhere a token fits. Redaction lives here rather than at any one call site
    ///         because every consumer of an endpoint renders it through this member.
    ///     </para>
    ///     <para>
    ///         An endpoint carrying none of the three renders exactly as <see cref="Uri.ToString" /> does, so no
    ///         existing message changes.
    ///     </para>
    /// </summary>
    public string Display => Redact(Uri);

    /// <summary>
    ///     The component this endpoint carries that must never be rendered — named, never quoted — or null when it
    ///     carries none. Both consumers of a daemon refuse such an endpoint before a client exists, and the refusal
    ///     says which component is at fault without repeating what was in it.
    ///     <para>
    ///         One member rather than a predicate per consumer: the check and the word the refusal uses are the same
    ///         fact, and a consumer that re-derived either would be the one that drifts.
    ///     </para>
    /// </summary>
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

    /// <summary>
    ///     The same redaction for an endpoint that is already a string.
    ///     <para>
    ///         A pin written before the redaction existed holds whatever <c>DOCKER_HOST</c> held — user information, a
    ///         query, a fragment — so the value coming back off disk is not covered by the fact that new pins are
    ///         written through <see cref="Display" />. A string that is not an absolute URI cannot be taken apart into
    ///         components, so it renders as <see cref="UnparsableEndpoint" /> rather than being echoed: a hand-edited
    ///         pin is precisely where a value this cannot parse would be sitting.
    ///     </para>
    /// </summary>
    internal static string Redact(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? Redact(uri) : UnparsableEndpoint;
    }
}
