namespace XE_Local_AI_Engine.Testing.FakeDocker;

/// <summary>
///     The daemon identity a <see cref="FakeDockerServer" /> answers <c>GET /version</c> and <c>GET /info</c> with.
///     <para>
///         Only the fields the production client actually reads are here. Everything else the Engine API reports is
///         omitted rather than defaulted, because a knob nothing consumes is a knob that can drift from the real
///         daemon without any test noticing.
///     </para>
/// </summary>
public sealed record FakeDockerOptions
{
    /// <summary>
    ///     The daemon installation id. Non-blank by default: a blank one makes <c>ProbeAsync</c> fail closed, which is
    ///     a scenario a test must opt into rather than inherit.
    /// </summary>
    public string DaemonId { get; init; } = "fakedaemon:" + Guid.NewGuid().ToString("N");

    /// <summary>The <c>Version</c> field — the daemon's own version, not the API's.</summary>
    public string ServerVersion { get; init; } = "29.4.1";

    /// <summary>
    ///     The <c>ApiVersion</c> field. Must stay at or above <c>ContainerRuntimeOptions.MinimumApiVersion</c>
    ///     ("1.41"), which is compared component-wise rather than numerically.
    /// </summary>
    public string ApiVersion { get; init; } = "1.51";

    /// <summary>The <c>MinAPIVersion</c> field — the oldest API version this daemon still serves.</summary>
    public string MinimumApiVersion { get; init; } = "1.24";

    /// <summary>The <c>Os</c> field. Linux, because bind storage and loopback publishing mean something else elsewhere.</summary>
    public string OperatingSystem { get; init; } = "linux";

    /// <summary>Whether <c>docker info</c> lists <c>name=rootless</c> among the daemon's security options.</summary>
    public bool Rootless { get; init; }

    /// <summary>Whether <c>docker info</c> lists <c>name=seccomp</c> among the daemon's security options.</summary>
    public bool SupportsSeccomp { get; init; } = true;
}
