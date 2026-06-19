namespace XE_Local_AI_Engine.Client.Hosting;

/// <summary>
///     Resolves the concrete bound loopback URL after Kestrel binds <c>http://127.0.0.1:0</c> and the OS assigns a free
///     port. The actual address is only known post-bind, so it is read from <c>IServerAddressesFeature.Addresses</c>.
/// </summary>
internal static class LoopbackUrlResolver
{
    /// <summary>
    ///     Picks the loopback HTTP address from a server's bound addresses. Prefers an explicit <c>127.0.0.1</c> address;
    ///     falls back to the first HTTP address (rewriting a wildcard host to loopback) so the browser always targets the
    ///     local interface.
    /// </summary>
    /// <param name="addresses">The bound server addresses (e.g. from <c>IServerAddressesFeature.Addresses</c>).</param>
    /// <returns>The resolved loopback URL, or <c>null</c> when no usable HTTP address is present.</returns>
    internal static string? Resolve(IEnumerable<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var httpAddresses = addresses
                            .Where(static address => !string.IsNullOrWhiteSpace(address))
                            .Where(static address => address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                            .ToList();

        if (httpAddresses.Count == 0)
        {
            return null;
        }

        var loopback = httpAddresses.FirstOrDefault(static address =>
            address.Contains("127.0.0.1", StringComparison.Ordinal) ||
            address.Contains("localhost", StringComparison.OrdinalIgnoreCase));

        if (loopback is not null)
        {
            return Normalize(loopback);
        }

        // Kestrel can report a wildcard host (e.g. http://[::]:5000 / http://0.0.0.0:5000) for the bound listener even
        // when it physically bound loopback; rewrite the host to 127.0.0.1 so the browser targets the local interface.
        return Normalize(httpAddresses[0]);
    }

    private static string Normalize(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return address;
        }

        // Desktop mode binds 127.0.0.1 exclusively, so the browser must only ever receive a loopback host — never a
        // wildcard (0.0.0.0/::) NOR a routable interface — regardless of what the listener reports.
        var host = uri.Host;
        var isLoopback = host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
        var resolvedHost = isLoopback ? host : "127.0.0.1";

        return $"{uri.Scheme}://{resolvedHost}:{uri.Port}/";
    }
}
