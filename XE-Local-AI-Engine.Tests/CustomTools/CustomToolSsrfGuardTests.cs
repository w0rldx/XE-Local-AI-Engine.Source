namespace XE_Local_AI_Engine.Tests.CustomTools;

using System.Net;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     SSRF guard: every private/reserved/metadata range is denied (on the PARSED address), IPv4-mapped IPv6 is
///     re-tested as v4, decimal/octal/hex host literals are rejected, the scheme/userinfo rules hold, and the pinned
///     connect callback denies a hostname that resolves to a loopback address (the DNS-rebind pin).
/// </summary>
[Category(TestCategories.Unit)]
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
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolSsrfGuard.ValidateRequestUrl(new Uri(url), [], hostIsParameterized: false, privateHosts: []));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ValidateRequestUrl_ForNonHttpScheme_Rejects()
    {
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolSsrfGuard.ValidateRequestUrl(new Uri("ftp://example.com/"), [], hostIsParameterized: false, privateHosts: []));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ValidateRequestUrl_ForUserInfo_Rejects()
    {
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolSsrfGuard.ValidateRequestUrl(new Uri("https://user:pass@example.com/"), [], hostIsParameterized: false, privateHosts: []));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ValidateRequestUrl_ParameterizedHostWithoutAllowlist_Rejects()
    {
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolSsrfGuard.ValidateRequestUrl(new Uri("https://evil.example.com/"), [], hostIsParameterized: true, privateHosts: []));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ValidateRequestUrl_HostNotInAllowlist_Rejects()
    {
        AssertEx.Throws<CustomToolExecutionException>(() =>
            CustomToolSsrfGuard.ValidateRequestUrl(new Uri("https://evil.example.com/"), ["good.example.com"], hostIsParameterized: true, privateHosts: []));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ValidateRequestUrl_PublicHostInAllowlist_Passes()
    {
        // Should not throw.
        CustomToolSsrfGuard.ValidateRequestUrl(new Uri("https://good.example.com/path"), ["good.example.com"], hostIsParameterized: true, privateHosts: []);
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

    [Test]
    [Arguments("http://192.168.1.20/api")]
    [Arguments("http://10.0.0.5:8123/api")]
    [Arguments("http://[fd00::20]/api")]
    public async Task ValidateRequestUrl_ListedPrivateLiteral_Passes(string url)
    {
        var host = new Uri(url).Host.Trim('[', ']');

        // Should not throw: the literal is in the tool's private-host exception and is a LAN address.
        CustomToolSsrfGuard.ValidateRequestUrl(new Uri(url), [host], hostIsParameterized: false, [host]);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ValidateRequestUrl_UnlistedPrivateLiteral_Rejects()
    {
        AssertEx.Throws<CustomToolExecutionException>(() =>
            CustomToolSsrfGuard.ValidateRequestUrl(new Uri("http://192.168.1.21/api"), [], hostIsParameterized: false, ["192.168.1.20"]));
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("http://127.0.0.1/")]
    [Arguments("http://169.254.169.254/latest/meta-data")]
    [Arguments("http://[fe80::1]/")]
    [Arguments("http://[::1]/")]
    [Arguments("http://224.0.0.1/")]
    [Arguments("http://0.0.0.0/")]
    public async Task ValidateRequestUrl_AlwaysDeniedLiteral_RejectsEvenWhenListed(string url)
    {
        var host = new Uri(url).Host.Trim('[', ']');
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolSsrfGuard.ValidateRequestUrl(new Uri(url), [host], hostIsParameterized: false, [host]));
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAllowedResolution_ListedHostResolvingToLanAddresses_Passes()
    {
        AssertEx.False(CustomToolSsrfGuard.IsAllowedResolution([IPAddress.Parse("fd00:ec2::254")], hostIsListed: true),
            "A listed host that resolves to the IPv6 metadata endpoint must still be refused.");
        AssertEx.True(CustomToolSsrfGuard.IsAllowedResolution([IPAddress.Parse("192.168.1.20"), IPAddress.Parse("fd00::20")], hostIsListed: true),
            "A listed host resolving only to LAN addresses must be reachable.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAllowedResolution_UnlistedHostResolvingToLanAddress_Refuses()
    {
        AssertEx.False(CustomToolSsrfGuard.IsAllowedResolution([IPAddress.Parse("192.168.1.20")], hostIsListed: false),
            "Without the exception a private address stays denied.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAllowedResolution_ListedHostMixingLanAndAlwaysDenied_Refuses()
    {
        AssertEx.False(CustomToolSsrfGuard.IsAllowedResolution([IPAddress.Parse("192.168.1.20"), IPAddress.Parse("127.0.0.1")], hostIsListed: true),
            "One always-denied address in the resolution refuses the whole set.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAllowedResolution_UnlistedHostMixingPublicAndPrivate_Refuses()
    {
        AssertEx.False(CustomToolSsrfGuard.IsAllowedResolution([IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5")], hostIsListed: false),
            "A rebind mixing a public decoy with a private target stays refused.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAllowedResolution_EmptyResolution_Refuses()
    {
        AssertEx.False(CustomToolSsrfGuard.IsAllowedResolution([], hostIsListed: true), "An empty resolution is never reachable.");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("127.0.0.1")]
    [Arguments("169.254.169.254")]
    [Arguments("fe80::1")]
    [Arguments("ff02::1")]
    [Arguments("::")]
    [Arguments("::ffff:127.0.0.1")]
    [Arguments("64:ff9b::c0a8:114")] // NAT64 embedding 192.168.1.20
    [Arguments("198.18.0.1")]
    [Arguments("fd00:ec2::254")] // IPv6 cloud metadata: unique-local by range, denied by name
    public async Task IsAlwaysDeniedAddress_ForNonLanDeniedRange_ReturnsTrue(string address)
    {
        AssertEx.True(CustomToolSsrfGuard.IsAlwaysDeniedAddress(IPAddress.Parse(address)), $"{address} must stay denied even when listed.");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("10.0.0.5")]
    [Arguments("172.16.0.1")]
    [Arguments("192.168.1.20")]
    [Arguments("100.64.0.1")]
    [Arguments("fd00::20")]
    [Arguments("::ffff:192.168.1.20")]
    public async Task IsAlwaysDeniedAddress_ForLanRange_ReturnsFalse(string address)
    {
        var parsed = IPAddress.Parse(address);
        AssertEx.True(CustomToolSsrfGuard.IsDeniedAddress(parsed), $"{address} must stay denied without the exception.");
        AssertEx.False(CustomToolSsrfGuard.IsAlwaysDeniedAddress(parsed), $"{address} is a LAN address a listed host may reach.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsListedHost_MatchesNameIgnoringCaseAndLiteralByValue()
    {
        AssertEx.True(CustomToolSsrfGuard.IsListedHost("HomeAssistant.LAN", ["homeassistant.lan"]), "Host names match ignoring case.");
        AssertEx.True(CustomToolSsrfGuard.IsListedHost("[fd00:0::20]", ["fd00::20"]), "IP literals match by value.");
        AssertEx.False(CustomToolSsrfGuard.IsListedHost("other.lan", ["homeassistant.lan"]), "An unlisted name does not match.");
        AssertEx.False(CustomToolSsrfGuard.IsListedHost("192.168.1.21", ["192.168.1.20"]), "An unlisted literal does not match.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task PinnedConnectCallback_ListedHostResolvingToLoopback_StillDenies()
    {
        // The exception is on the request, and "localhost" is listed, yet loopback is always denied: the callback must refuse.
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = CustomToolSsrfGuard.CreatePinnedConnectCallback()
        };
        using var client = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://localhost:9/"));
        request.Options.Set(CustomToolSsrfGuard.PrivateHostsOption, (IReadOnlyList<string>)["localhost"]);

        Exception? captured = null;
        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
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
