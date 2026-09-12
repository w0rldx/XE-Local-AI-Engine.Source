namespace XE_Local_AI_Engine.Client.Services.Containers.Bridge;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Operator-configurable settings for the container bridge: the one extra Kestrel listener the engine opens on a
///     LAN-facing interface so an application container on an engine-owned network can reach this node's own
///     inference surface. Every other listener the engine opens is loopback-only and stays that way.
///     <para>
///         <b>The fail-closed asymmetry is deliberate.</b> <see cref="Enabled" /> defaults to <see langword="false" />
///         in code while the shipped <c>appsettings.json</c> sets it to <see langword="true" />, so a node whose
///         configuration is missing or unreadable opens no routable listener at all. The listener is opened only when
///         this AND <c>ExternalApps:Enabled</c> are both on: the bridge exists for application containers, and a node
///         with that feature off has none.
///     </para>
/// </summary>
public sealed record ContainerBridgeOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "ContainerBridge";

    /// <summary>Whether the engine opens the bridge listener at all. Default <see langword="false" />; see the type remarks.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    ///     The IPv4 address the bridge listens on, or <see langword="null" /> to detect the host's LAN-facing
    ///     interface. Set it on a box whose default-route interface is not the one the container network reaches back
    ///     through. A wildcard (<c>0.0.0.0</c>) is refused: the bridge names one interface so the startup bind guard
    ///     can allow exactly that address and keep failing closed on every other non-loopback bind.
    ///     <para>
    ///         <b>IPv4 only.</b> An IPv6 literal here is rejected rather than used, and a host with no IPv4 address at
    ///         all opens no bridge. The bridge is the hop a container makes to the host, and the container networks
    ///         the engine creates are IPv4.
    ///     </para>
    ///     <para>
    ///         An address this host does not own is rejected too, so a typo costs the node its bridge and not its
    ///         boot. A loopback address is owned and therefore accepted, but it produces a bridge that binds
    ///         successfully and that no container can reach — a container cannot reach the host's loopback, which is
    ///         the entire reason this listener exists.
    ///     </para>
    /// </summary>
    public string? BindAddress { get; init; }

    /// <summary>
    ///     The port the bridge listens on. 18790 by default: outside the Linux ephemeral range (32768-60999), so the
    ///     kernel never hands it to an outbound socket between two engine starts, and clear of the ports the curated
    ///     catalog's applications publish.
    /// </summary>
    [Range(1024, 65535)]
    public int Port { get; init; } = 18790;
}
