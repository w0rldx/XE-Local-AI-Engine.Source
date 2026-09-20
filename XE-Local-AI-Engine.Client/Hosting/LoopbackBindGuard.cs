namespace XE_Local_AI_Engine.Client.Hosting;

using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

/// <summary>
///     Startup guard enforcing the loopback-only posture of the local API.
/// </summary>
/// <remarks>
///     Binding the <c>/api/local/v1</c> surface to a routable interface would expose the anonymous first-run setup
///     endpoint to the network. <see cref="Guard" /> inspects the addresses the server actually bound and shuts the app
///     down if any is non-loopback, unless the operator opted out via <see cref="AllowNonLoopbackBindKey" /> or the
///     caller named that address as an expected bind. Defense-in-depth behind <c>LocalApiSecurityMiddleware</c>. See
///     docs/wiki/09-api-and-hubs.md ("Security middleware &amp; auth ordering").
/// </remarks>
internal static class LoopbackBindGuard
{
    /// <summary>Config flag (default <c>false</c>) that opts out of the loopback-only bind guard for an operator who has secured the surface themselves.</summary>
    internal const string AllowNonLoopbackBindKey = "Security:AllowNonLoopbackBind";

    /// <summary>
    ///     Registers an application-started hook that shuts the app down when it bound a non-loopback address without
    ///     the opt-out flag.
    /// </summary>
    /// <remarks>
    ///     Reading the addresses post-start rather than the configured URLs reflects what Kestrel actually bound,
    ///     including an OS-assigned port and wildcard expansion.
    /// </remarks>
    /// <param name="app">The built application whose bound addresses are inspected once it has started.</param>
    /// <param name="expectedNonLoopbackBinds">
    ///     Listener URLs the app is expected to bind non-loopback: the container bridge's, when it was opened.
    /// </param>
    internal static void Guard(WebApplication app, IReadOnlyCollection<string> expectedNonLoopbackBinds)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(expectedNonLoopbackBinds);

        if (app.Configuration.GetValue(AllowNonLoopbackBindKey, defaultValue: false))
        {
            return;
        }

        var lifetime = app.Lifetime;
        lifetime.ApplicationStarted.Register(() =>
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LoopbackBindGuard).FullName!);

            ShutDownIfBindIsRoutable(addresses, expectedNonLoopbackBinds, lifetime, logger);
        });
    }

    /// <summary>
    ///     Shuts the app down with a non-zero exit code when any of <paramref name="addresses" /> is a non-loopback
    ///     bind, returning whether one was detected.
    /// </summary>
    /// <remarks>
    ///     <c>false</c> means the bind is safe, including the no-resolvable-addresses case. Exposed for unit testing
    ///     with a stub lifetime, so the exit-code and stop behavior can be asserted without a real routable listener.
    /// </remarks>
    internal static bool ShutDownIfBindIsRoutable(IEnumerable<string>? addresses,
        IReadOnlyCollection<string> expectedNonLoopbackBinds,
        IHostApplicationLifetime lifetime,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(expectedNonLoopbackBinds);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);

        // No resolvable addresses (e.g. the in-memory TestServer) means there is no routable surface to guard.
        if (addresses is null)
        {
            return false;
        }

        var nonLoopback = FindNonLoopbackAddresses(addresses, expectedNonLoopbackBinds);
        if (nonLoopback.Count == 0)
        {
            return false;
        }

        logger.LogCritical("The local-only API bound to non-loopback address(es) {Addresses}, which would expose the anonymous setup surface beyond this machine. "
                           + "The local API supports loopback-only operation; set '{Flag}=true' only if you have secured the surface yourself. Shutting down.",
            string.Join(", ", nonLoopback), AllowNonLoopbackBindKey);

        // Fail with a non-zero process exit so a supervisor/CI treats the guarded shutdown as an error, not a clean stop.
        // Program's final return reads Environment.ExitCode, so set it before StopApplication starts the teardown.
        Environment.ExitCode = 1;
        lifetime.StopApplication();
        return true;
    }

    /// <summary>
    ///     Returns the subset of <paramref name="addresses" /> that are NOT loopback-only binds and were NOT named in
    ///     <paramref name="expectedNonLoopbackBinds" />. An empty result means the bind is safe. Exposed for unit
    ///     testing without spinning a real routable listener.
    /// </summary>
    internal static IReadOnlyList<string> FindNonLoopbackAddresses(IEnumerable<string> addresses, IReadOnlyCollection<string> expectedNonLoopbackBinds)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(expectedNonLoopbackBinds);

        return addresses
               .Where(address => !IsLoopbackAddress(address) && !IsExpectedBind(address, expectedNonLoopbackBinds))
               .ToArray();
    }

    /// <summary>
    ///     Whether <paramref name="address" /> is one the caller declared the app would bind non-loopback.
    /// </summary>
    /// <remarks>
    ///     Compared by scheme, host and port rather than as raw text, because Kestrel reports back what it actually
    ///     bound and that string need not be character-identical to the one it was given.
    /// </remarks>
    private static bool IsExpectedBind(string address, IReadOnlyCollection<string> expectedNonLoopbackBinds)
    {
        if (expectedNonLoopbackBinds.Count == 0 || string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var bound = BindingAddress.Parse(address);
        foreach (var expected in expectedNonLoopbackBinds)
        {
            if (string.IsNullOrWhiteSpace(expected))
            {
                continue;
            }

            var declared = BindingAddress.Parse(expected);

            // A wildcard is never "expected": allow-listing it would exempt every interface the app bound rather than the
            // one named. Both spellings count — Kestrel's "*"/"+" parse to an empty host, "0.0.0.0"/"[::]" to a real one.
            if (IsWildcardHost(declared.Host))
            {
                continue;
            }

            if (declared.Port == bound.Port
                && string.Equals(declared.Host, bound.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(declared.Scheme, bound.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWildcardHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return true;
        }

        var normalizedHost = host.TrimStart('[').TrimEnd(']');
        return IPAddress.TryParse(normalizedHost, out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any));
    }

    private static bool IsLoopbackAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var host = BindingAddress.Parse(address).Host;

        // Wildcard binds ("*", "+", 0.0.0.0, ::) accept connections on every interface — never loopback-only.
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Strip the brackets an IPv6 literal carries in a URL host before parsing.
        var normalizedHost = host.TrimStart('[').TrimEnd(']');
        return IPAddress.TryParse(normalizedHost, out var ip) && IPAddress.IsLoopback(ip);
    }
}
