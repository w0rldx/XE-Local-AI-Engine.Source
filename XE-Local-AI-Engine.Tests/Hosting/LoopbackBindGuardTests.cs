namespace XE_Local_AI_Engine.Tests.Hosting;

using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Coverage for the loopback-only bind classifier (MED-001 startup guard). The wiring stops the app on a routable
///     bind; here the pure classifier is exercised directly so the "rejects a routable bind" decision is asserted without
///     spinning a real externally-bound listener.
/// </summary>
public sealed class LoopbackBindGuardTests
{
    [Test]
    public void FindNonLoopbackAddresses_WhenAllLoopback_ReturnsEmpty()
    {
        var addresses = new[]
        {
            "http://127.0.0.1:5000",
            "http://localhost:5001",
            "https://localhost:5002",
            "http://[::1]:5003"
        };

        var result = LoopbackBindGuard.FindNonLoopbackAddresses(addresses);

        AssertEx.Empty(result);
    }

    [Test]
    public void FindNonLoopbackAddresses_FlagsRoutableAndWildcardBinds()
    {
        var addresses = new[]
        {
            "http://127.0.0.1:5000",   // loopback, kept out of the result
            "http://0.0.0.0:5000",     // wildcard
            "http://192.168.1.10:5000", // routable LAN address
            "http://+:5000",           // Kestrel wildcard
            "http://*:5000"            // Kestrel wildcard
        };

        var result = LoopbackBindGuard.FindNonLoopbackAddresses(addresses);

        AssertEx.Equal(4, result.Count);
        AssertEx.False(result.Contains("http://127.0.0.1:5000"), "The loopback bind must not be flagged.");
    }
}
