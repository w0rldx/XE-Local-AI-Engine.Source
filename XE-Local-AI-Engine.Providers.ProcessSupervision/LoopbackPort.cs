namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Net;
using System.Net.Sockets;

/// <summary>The bind probe the runtime supervisors run before handing a loopback port to a child.</summary>
/// <remarks>
///     The probe and the child's own bind are not atomic; a child that loses that race reports it through
///     <see cref="PortBindFailure" />.
/// </remarks>
public static class LoopbackPort
{
    /// <summary>Whether <paramref name="port" /> can be bound on the IPv4 loopback right now.</summary>
    public static bool IsFree(int port)
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
