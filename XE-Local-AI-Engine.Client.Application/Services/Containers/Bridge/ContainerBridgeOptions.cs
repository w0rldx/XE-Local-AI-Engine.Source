namespace XE_Local_AI_Engine.Client.Services.Containers.Bridge;

using System.ComponentModel.DataAnnotations;

/// <summary>Operator-configurable settings for the container bridge: the one extra Kestrel listener the engine opens on a LAN-facing interface.</summary>
/// <remarks>
///     It lets an application container on an engine-owned network reach this node's inference surface; every other
///     listener the engine opens is loopback-only and stays that way. <b>The fail-closed asymmetry is
///     deliberate</b>: <see cref="Enabled" /> defaults to <see langword="false" /> in code while the shipped
///     <c>appsettings.json</c> sets it <see langword="true" />, so a node with missing or unreadable configuration
///     opens no routable listener. The listener needs this AND <c>ExternalApps:Enabled</c> on.
/// </remarks>
public sealed record ContainerBridgeOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "ContainerBridge";

    /// <summary>Whether the engine opens the bridge listener at all. Default <see langword="false" />; see the type remarks.</summary>
    public bool Enabled { get; init; }

    /// <summary>The IPv4 address the bridge listens on, or <see langword="null" /> to detect the host's LAN-facing interface.</summary>
    /// <remarks>
    ///     Set it where the default-route interface is not the one the container network reaches back through. A
    ///     wildcard (<c>0.0.0.0</c>) is refused: the bridge names ONE interface so the startup bind guard allows
    ///     exactly that address and keeps failing closed on every other non-loopback bind. <b>IPv4 only</b> — an IPv6
    ///     literal is rejected and a host with no IPv4 opens no bridge, the engine's container networks being IPv4.
    ///     An unowned address is rejected, so a typo costs the bridge, not the boot; a loopback one is accepted.
    /// </remarks>
    public string? BindAddress { get; init; }

    /// <summary>The port the bridge listens on, 18790 by default.</summary>
    /// <remarks>
    ///     Outside the Linux ephemeral range (32768-60999), so the kernel never hands it to an outbound socket
    ///     between two engine starts, and clear of the ports the curated catalog's applications publish.
    /// </remarks>
    [Range(1024, 65535)]
    public int Port { get; init; } = 18790;
}
