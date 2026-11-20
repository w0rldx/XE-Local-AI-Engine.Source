namespace XE_Local_AI_Engine.Client.Hosting;

using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Connections;

/// <summary>
///     Recognizes a host start that lost its port to another process after the startup probe found it free, so startup
///     exits with the port-in-use code instead of crashing.
/// </summary>
/// <remarks>
///     <c>DesktopPortStore.IsPortAvailable</c> releases its probe socket before Kestrel binds. When another process
///     takes the port in that gap, Kestrel throws an <see cref="IOException" /> naming the address, wrapping an
///     <see cref="AddressInUseException" />.
/// </remarks>
internal static class PortInUseFailure
{
    /// <summary>Finds an <see cref="AddressInUseException" /> in <paramref name="exception" /> or its inner exceptions.</summary>
    /// <param name="exception">The startup failure.</param>
    /// <param name="bindFailure">The message of the exception that wraps it (Kestrel's names the address), or its own.</param>
    internal static bool TryFind(Exception? exception, [NotNullWhen(true)] out string? bindFailure)
    {
        for (Exception? outer = null, current = exception; current is not null; outer = current, current = current.InnerException)
        {
            if (current is AddressInUseException)
            {
                bindFailure = (outer ?? current).Message;
                return true;
            }
        }

        bindFailure = null;
        return false;
    }
}
