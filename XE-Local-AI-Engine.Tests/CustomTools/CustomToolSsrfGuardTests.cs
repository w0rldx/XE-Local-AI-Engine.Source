namespace XE_Local_AI_Engine.Tests.CustomTools;

using System.Net;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     SSRF guard: every private/reserved/metadata range is denied (on the PARSED address), IPv4-mapped IPv6 is
///     re-tested as v4, decimal/octal/hex host literals are rejected, the scheme/userinfo rules hold, and the pinned
///     connect callback denies a hostname that resolves to a loopback address (the DNS-rebind pin).
/// </summary>
public sealed class CustomToolSsrfGuardTests
{
    [Test]
    [Arguments("127.0.0.1")]
    [Arguments("127.9.9.9")]
    [Arguments("10.0.0.5")]
    [Arguments("10.255.255.255")]
    [Arguments("172.16.0.1")]
    [Arguments("172.31.255.255")]
    [Arguments("192.168.1.1")]
    [Arguments("169.254.0.1")]
    [Arguments("169.254.169.254")] // cloud metadata
    [Arguments("0.0.0.0")]
    [Arguments("100.64.0.1")] // CGNAT
    [Arguments("192.0.0.1")]
    [Arguments("198.18.0.1")] // benchmarking
    [Arguments("255.255.255.255")] // broadcast
    [Arguments("224.0.0.1")] // multicast
    [Arguments("::1")] // IPv6 loopback
    [Arguments("fc00::1")] // ULA
    [Arguments("fd12:3456::1")] // ULA
    [Arguments("fe80::1")] // link-local
    [Arguments("fec0::1")] // deprecated site-local
    [Arguments("ff02::1")] // IPv6 multicast
    [Arguments("64:ff9b::7f00:1")] // NAT64
    [Arguments("::")] // unspecified
    [Arguments("::ffff:127.0.0.1")] // IPv4-mapped loopback → re-tested as v4
    [Arguments("::ffff:169.254.169.254")] // IPv4-mapped metadata
    [Arguments("::169.254.169.254")] // deprecated IPv4-compatible metadata → re-tested as v4
    [Arguments("::127.0.0.1")] // deprecated IPv4-compatible loopback → re-tested as v4
    public async Task IsDeniedAddress_ForReservedRange_ReturnsTrue(string address)
    {
        AssertEx.True(CustomToolSsrfGuard.IsDeniedAddress(IPAddress.Parse(address)),
            $"Expected {address} to be denied.");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("8.8.8.8")]
    [Arguments("1.1.1.1")]
    [Arguments("93.184.216.34")]
    [Arguments("2606:4700:4700::1111")] // Cloudflare v6 (global unicast)
    public async Task IsDeniedAddress_ForPublicAddress_ReturnsFalse(string address)
    {
        AssertEx.False(CustomToolSsrfGuard.IsDeniedAddress(IPAddress.Parse(address)),
            $"Expected {address} to be allowed.");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("http://2130706433/")] // decimal 127.0.0.1
    [Arguments("http://0177.0.0.1/")] // octal
    [Arguments("http://0x7f.0.0.1/")] // hex
    [Arguments("http://127.0.0.1/")] // loopback literal
    [Arguments("https://169.254.169.254/latest/meta-data/")] // metadata literal
    [Arguments("http://[::1]/")] // IPv6 loopback literal
    public async Task ValidateRequestUrl_ForNumericOrPrivateLiteral_Rejects(string url)
    {
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolSsrfGuard.ValidateRequestUrl(new Uri(url), [], hostIsParameterized: false));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ValidateRequestUrl_ForNonHttpScheme_Rejects()
    {
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolSsrfGuard.ValidateRequestUrl(new Uri("ftp://example.com/"), [], hostIsParameterized: false));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ValidateRequestUrl_ForUserInfo_Rejects()
    {
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolSsrfGuard.ValidateRequestUrl(new Uri("https://user:pass@example.com/"), [], hostIsParameterized: false));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ValidateRequestUrl_ParameterizedHostWithoutAllowlist_Rejects()
    {
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolSsrfGuard.ValidateRequestUrl(new Uri("https://evil.example.com/"), [], hostIsParameterized: true));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ValidateRequestUrl_HostNotInAllowlist_Rejects()
    {
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolSsrfGuard.ValidateRequestUrl(new Uri("https://evil.example.com/"), ["good.example.com"], hostIsParameterized: true));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ValidateRequestUrl_PublicHostInAllowlist_Passes()
    {
        // Should not throw.
        CustomToolSsrfGuard.ValidateRequestUrl(new Uri("https://good.example.com/path"), ["good.example.com"], hostIsParameterized: true);
        await Task.CompletedTask;
    }

    [Test]
    public async Task PinnedConnectCallback_ForHostResolvingToLoopback_Denies()
    {
        // The URL host "localhost" passes the URL-only validation (it is not a literal or numeric host), so the DNS pin
        // is what must deny it: the callback resolves localhost → 127.0.0.1 and refuses the connection.
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = CustomToolSsrfGuard.CreatePinnedConnectCallback()
        };
        using var client = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Exception? captured = null;
        try
        {
            using var response = await client.GetAsync(new Uri("http://localhost:9/"), timeout.Token);
        }
        catch (Exception exception)
        {
            captured = exception;
        }

        var notNull = AssertEx.NotNull(captured);
        var blocked = notNull as CustomToolExecutionException ?? notNull.InnerException as CustomToolExecutionException;
        AssertEx.NotNull(blocked, $"Expected the connect pin to raise a block, but got {notNull.GetType().Name}: {notNull.Message}");
    }
}
