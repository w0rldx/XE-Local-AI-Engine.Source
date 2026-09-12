namespace XE_Local_AI_Engine.Testing.FakeDocker;

using System.Text.Json.Nodes;

/// <summary>
///     One container the fake daemon holds, across both creation surfaces.
///     <para>
///         The create request is kept <em>verbatim</em> as the JSON object the client sent, and inspect echoes it
///         back rather than projecting it through a model of the fake's own. That is what keeps the two production
///         creation paths honest: the sandbox path never sends <c>ExtraHosts</c>, a restart policy or a healthcheck,
///         and the application path never sends <c>Tmpfs</c>, so a fake that filled either in from a default would
///         report fields a real daemon would have left at their zero value.
///     </para>
/// </summary>
public sealed class FakeDockerContainer
{
    /// <summary>The 64-hex container id the fake minted at create.</summary>
    public required string Id { get; init; }

    /// <summary>The container name without the leading slash the daemon renders it with.</summary>
    public required string Name { get; init; }

    /// <summary>The <c>POST /containers/create</c> body exactly as it arrived.</summary>
    public required JsonObject CreateRequest { get; init; }

    /// <summary>The daemon's lower-case state word: <c>created</c>, <c>running</c> or <c>exited</c>.</summary>
    public string State { get; set; } = "created";

    /// <summary>Whether the container is running. Kept beside <see cref="State" /> because inspect reports both.</summary>
    public bool Running { get; set; }

    /// <summary>The exit code inspect reports, and the one the list endpoint renders into its status prose.</summary>
    public long ExitCode { get; set; }

    /// <summary>
    ///     The daemon's health word (<c>starting</c>, <c>healthy</c>, <c>unhealthy</c>), or null when the container
    ///     declared no healthcheck — which is not the same as an unrecognised one and must not read as it.
    /// </summary>
    public string? HealthStatus { get; set; }

    /// <summary>RFC 3339 start time, or null for "never started" (which the daemon renders as year one).</summary>
    public DateTimeOffset? StartedAtUtc { get; set; }

    /// <summary>RFC 3339 finish time, or null for "never finished".</summary>
    public DateTimeOffset? FinishedAtUtc { get; set; }

    /// <summary>
    ///     What the daemon actually bound, reported as <c>NetworkSettings.Ports</c> and filled in only once the
    ///     container has been started. Empty before that, which is precisely what the requested/effective split
    ///     exists to expose — so it is an empty object rather than an absent one, exactly as the daemon reports it.
    /// </summary>
    public JsonObject EffectivePorts { get; } = new();

    /// <summary>
    ///     The effective mount set reported as the response's top-level <c>Mounts[]</c>: the requested bind mounts
    ///     plus one anonymous volume per <c>VOLUME</c> the image declares.
    /// </summary>
    public IList<FakeDockerMountPoint> EffectiveMounts { get; } = [];

    /// <summary>Ids of the networks this container is attached to, which block that network's removal.</summary>
    public ISet<string> AttachedNetworkIds { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>What <c>GET /containers/{id}/logs</c> serves, in the order the daemon framed it.</summary>
    public IList<FakeDockerLogFrame> Logs { get; } = [];
}
