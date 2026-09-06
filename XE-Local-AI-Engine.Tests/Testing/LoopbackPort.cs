namespace XE_Local_AI_Engine.Tests.Testing;

using System.Globalization;
using System.Net;
using System.Net.Sockets;

/// <summary>
///     Loopback port selection for tests that must hand a concrete port number to something that binds
///     it later — a child engine process, a Kestrel host — rather than letting the OS pick at bind time.
/// </summary>
internal static class LoopbackPort
{
    /// <summary>
    ///     Binds a loopback socket on port 0, releases it, and returns the number the OS chose.
    ///     <para>
    ///         The number is a <b>candidate</b>, not a reservation: the port is free at the instant it is
    ///         returned and any other process on the machine may claim it before the caller's real bind.
    ///         A caller that passes the number to a child process or a server MUST retry on that product's
    ///         own in-use signal — use <see cref="BindWithRetryAsync{T}" />. When the port is the
    ///         <i>target</i> of the test (an occupied-port negative case), do not use this method: bind a
    ///         <see cref="TcpListener" /> on port 0 and keep it started for the life of the test instead.
    ///     </para>
    /// </summary>
    internal static int Reserve()
    {
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    ///     Calls <paramref name="attempt" /> with a fresh <see cref="Reserve" /> candidate until it returns
    ///     a result. <paramref name="attempt" /> returns <c>null</c> when — and only when — the product
    ///     reported that this port was already in use (the engine's port-in-use exit code, Kestrel's
    ///     <c>AddressInUseException</c>); every other failure must propagate as an exception so a real
    ///     defect is never retried away. No sleeps and no timing assumptions: the retry is driven by the
    ///     product's own signal.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     All <paramref name="maxAttempts" /> candidates were reported in use.
    /// </exception>
    internal static async Task<T> BindWithRetryAsync<T>(Func<int, Task<T?>> attempt, int maxAttempts = 5)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, other: 1);

        var tried = new List<int>(maxAttempts);
        for (var i = 0; i < maxAttempts; i++)
        {
            var candidate = Reserve();
            tried.Add(candidate);
            if (await attempt(candidate).ConfigureAwait(false) is { } bound)
            {
                return bound;
            }
        }

        throw new InvalidOperationException($"No loopback port could be bound: all {maxAttempts.ToString(CultureInfo.InvariantCulture)} candidates were "
                                            + $"reported in use ({string.Join(", ", tried.Select(static port => port.ToString(CultureInfo.InvariantCulture)))}).");
    }
}
