namespace XE_Local_AI_Engine.Client.Hosting;

using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

/// <summary>
///     Startup guard enforcing the loopback-only posture of the local API. The <c>/api/local/v1</c> surface is designed
///     for single-user, same-machine operation (remote/headless is unsupported); binding it to a routable interface would
///     expose the anonymous first-run setup endpoint to the network. <see cref="Guard" /> inspects the addresses the
///     server actually bound and shuts the app down if any is non-loopback, unless the operator has explicitly opted out
///     via <see cref="AllowNonLoopbackBindKey" />.
///     <para>
///         Desktop mode always binds <c>127.0.0.1</c> and Aspire dev binds <c>localhost</c> (external exposure is handled
///         by the DCP proxy, not the app process), so this guard is a no-op on every supported launch — it only fires when
///         an operator overrides the bind to a routable address. It is defense-in-depth behind
///         <c>LocalApiSecurityMiddleware</c>, which independently rejects non-loopback peers at request time.
///     </para>
/// </summary>
internal static class LoopbackBindGuard
{
    /// <summary>Config flag (default <c>false</c>) that opts out of the loopback-only bind guard for an operator who has secured the surface themselves.</summary>
    internal const string AllowNonLoopbackBindKey = "Security:AllowNonLoopbackBind";

    /// <summary>
    ///     Registers an application-started hook that shuts the app down when it bound a non-loopback address without the
    ///     opt-out flag. Reading the addresses post-start (rather than the configured URLs) reflects what Kestrel actually
    ///     bound, including an OS-assigned port and wildcard expansion.
    /// </summary>
    internal static void Guard(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

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

            ShutDownIfBindIsRoutable(addresses, lifetime, logger);
        });
    }

    /// <summary>
    ///     Shuts the app down with a non-zero exit code when any of <paramref name="addresses" /> is a non-loopback bind.
    ///     Returns <c>true</c> when a routable bind was detected (and shutdown was triggered), <c>false</c> when the bind
    ///     is safe (including no resolvable addresses). Exposed for unit testing with a stub lifetime, so the exit-code
    ///     and stop behavior can be asserted without a real routable listener.
    /// </summary>
    internal static bool ShutDownIfBindIsRoutable(IEnumerable<string>? addresses, IHostApplicationLifetime lifetime, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);

        // No resolvable addresses (e.g. the in-memory TestServer) means there is no routable surface to guard.
        if (addresses is null)
        {
            return false;
        }

        var nonLoopback = FindNonLoopbackAddresses(addresses);
        if (nonLoopback.Count == 0)
        {
            return false;
        }

        logger.LogCritical("The local-only API bound to non-loopback address(es) {Addresses}, which would expose the anonymous setup surface beyond this machine. "
                           + "The local API supports loopback-only operation; set '{Flag}=true' only if you have secured the surface yourself. Shutting down.",
            string.Join(", ", nonLoopback), AllowNonLoopbackBindKey);

        // Fail with a non-zero process exit so a supervisor/CI treats the guarded shutdown as an error rather than a
        // clean stop. Program's final return reads Environment.ExitCode; set it before StopApplication so it is in place
        // before the host begins tearing down.
        Environment.ExitCode = 1;
        lifetime.StopApplication();
        return true;
    }

    /// <summary>
    ///     Returns the subset of <paramref name="addresses" /> that are NOT loopback-only binds. An empty result means the
    ///     bind is safe. Exposed for unit testing without spinning a real routable listener.
    /// </summary>
    internal static IReadOnlyList<string> FindNonLoopbackAddresses(IEnumerable<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        return addresses.Where(static address => !IsLoopbackAddress(address)).ToArray();
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
