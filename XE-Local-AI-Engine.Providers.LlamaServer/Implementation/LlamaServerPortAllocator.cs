namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Net;
using System.Net.Sockets;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     The set of localhost ports reserved for <c>llama-server</c> children, allocated from the configured range with a
///     bind probe so a port an exiting child still holds is skipped rather than handed to the next spawn.
/// </summary>
/// <remarks>
///     Deliberately NOT self-synchronized: every member is called by <see cref="LlamaServerProcessSupervisor" /> under
///     its admission gate. That is what makes <see cref="ReservedCount" /> a race-free measure of the loaded-model cap —
///     a port is reserved before the process registers and released when it fails or is evicted, so the count already
///     includes in-flight spawns. A second lock here would only invite a caller to read the count outside the gate.
/// </remarks>
internal sealed class LlamaServerPortAllocator(LlamaServerSupervisorOptions options)
{
    private readonly HashSet<int> _allocatedPorts = [];
    private readonly LlamaServerSupervisorOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Reserved ports — the count the loaded-model cap is measured against (caller holds the admission gate).</summary>
    public int ReservedCount => _allocatedPorts.Count;

    /// <summary>Allocates a free port from the configured range (caller holds the admission gate).</summary>
    public int Allocate()
    {
        for (var port = _options.PortRangeStart; port <= _options.PortRangeEnd; port++)
        {
            if (_allocatedPorts.Contains(port) || !IsPortFree(port))
            {
                continue;
            }

            _allocatedPorts.Add(port);
            return port;
        }

        throw LlamaServerProcessSupervisor.NonRetryable("No free local port is available for the model runtime.");
    }

    /// <summary>Drops a port reservation (caller holds the admission gate). Releasing an unreserved port is a no-op.</summary>
    public void Release(int port)
    {
        _allocatedPorts.Remove(port);
    }

    private static bool IsPortFree(int port)
    {
        // Probe by binding loopback; collision (another process owns it) means skip-and-retry.
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
