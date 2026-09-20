namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     Chooses the loopback host port every published <c>ui</c> port of one deployment attempt is bound to, and holds
///     it until the container that publishes it is created.
/// </summary>
/// <remarks>
///     The engine always passes an explicit host port rather than letting the daemon assign one, because
///     <c>XE_UI_HOST_PORT_&lt;service&gt;</c> may appear in ANOTHER service's environment, so a daemon-assigned port
///     would not be known in time to build that container's environment. The bind probe is the one Development Mode
///     and the llama-server supervisor use, with the difference that decides the feature: the listener is KEPT OPEN,
///     because probing and closing leaves a window per port in which another process takes it.
/// </remarks>
internal static class ExternalAppPortAllocator
{
    private const string UiRole = "ui";

    /// <summary>
    ///     Probes and holds one host port for every <c>ui</c> port the manifest declares, in manifest order.
    /// </summary>
    /// <remarks>
    ///     The caller releases each one immediately before creating the container that publishes it, and disposes the
    ///     hold to release whatever is left when the attempt ends either way.
    /// </remarks>
    public static ExternalAppPortHold Hold(ApplicationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var hold = new ExternalAppPortHold();
        try
        {
            foreach (var service in manifest.Services)
            {
                foreach (var port in service.Ports.Where(static candidate => string.Equals(candidate.Role, UiRole, StringComparison.Ordinal)))
                {
                    hold.Add(service.Name, port.ContainerPort, port.PreferredHostPort);
                }
            }
        }
        catch
        {
            hold.Dispose();
            throw;
        }

        return hold;
    }

    /// <summary>
    ///     Whether a host port can be bound on loopback right now. The failure translator asks this to tell a lost
    ///     port race apart from every other create or start failure, which is a question about the box rather than
    ///     about the daemon's prose.
    /// </summary>
    public static bool IsBindable(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>One host port held for one published container port of one service.</summary>
internal sealed class ExternalAppHostPort
{
    public required string Service { get; init; }

    public required int ContainerPort { get; init; }

    public required int HostPort { get; init; }
}

/// <summary>
///     The host ports of one deployment attempt, each still bound by this process until it is released or the hold is
///     disposed. Not thread-safe: one attempt runs on one operation.
/// </summary>
internal sealed class ExternalAppPortHold : IDisposable
{
    private readonly List<ExternalAppHostPort> _ports = [];
    private readonly Dictionary<string, Socket> _listeners = new(StringComparer.Ordinal);

    /// <summary>Every held port, in manifest order.</summary>
    public IReadOnlyList<ExternalAppHostPort> Ports => _ports;

    /// <summary>
    ///     The host port each service's FIRST <c>ui</c> port was given — what
    ///     <c>XE_UI_HOST_PORT_&lt;service&gt;</c> resolves to.
    /// </summary>
    public IReadOnlyDictionary<string, int> ByService
    {
        get
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var port in _ports)
            {
                _ = map.TryAdd(port.Service, port.HostPort);
            }

            return map;
        }
    }

    /// <summary>
    ///     Releases the ports of one service immediately before its container is created. Releasing a service twice,
    ///     or one that publishes nothing, is a no-op.
    /// </summary>
    public void Release(string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        foreach (var port in _ports.Where(candidate => string.Equals(candidate.Service, service, StringComparison.Ordinal)))
        {
            if (_listeners.Remove(Key(port.Service, port.ContainerPort), out var socket))
            {
                socket.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var socket in _listeners.Values)
        {
            socket.Dispose();
        }

        _listeners.Clear();
    }

    internal void Add(string service, int containerPort, int? preferredHostPort)
    {
        // The preferred port first, an OS-assigned ephemeral one when it is taken — including when the sibling that
        // took it is another service of this same attempt, which is why nothing tracks the chosen ports separately.
        var socket = (preferredHostPort is > 0 ? BindLoopback(preferredHostPort.Value) : null)
                     ?? BindLoopback(0)
                     ?? throw new InvalidOperationException($"No loopback host port could be reserved for service '{service}' port {containerPort.ToString(CultureInfo.InvariantCulture)}.");

        _listeners[Key(service, containerPort)] = socket;
        _ports.Add(new ExternalAppHostPort { Service = service, ContainerPort = containerPort, HostPort = ((IPEndPoint)socket.LocalEndPoint!).Port });
    }

    private static Socket? BindLoopback(int hostPort)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, hostPort));

            // Listening, not merely bound: Linux lets two SO_REUSEADDR sockets bind one address as long as neither is in the LISTEN state, so a bound-only
            // "hold" holds nothing — the daemon would take the port anyway and the reservation this whole type exists for would be a no-op.
            socket.Listen(backlog: 1);
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    private static string Key(string service, int containerPort)
    {
        return service + ":" + containerPort.ToString(CultureInfo.InvariantCulture);
    }
}
