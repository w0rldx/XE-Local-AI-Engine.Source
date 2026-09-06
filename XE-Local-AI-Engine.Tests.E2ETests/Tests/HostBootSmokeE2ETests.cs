namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.AspNetCore.Connections;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the XE node host boots under <see cref="XENodeE2EWebApplicationFactory" />
///     on a real, pre-chosen loopback port and answers liveness. No browser, no React build —
///     this is the fast gate that runs before the heavier Playwright layer. Uses a throwaway
///     web root since <c>/health/live</c> does not depend on the SPA assets.
/// </summary>
public sealed class HostBootSmokeE2ETests
{
    [Test]
    public async Task Host_Boots_On_Real_Port_And_Health_Live_Returns_200()
    {
        var webRoot = Directory.CreateTempSubdirectory("xe-e2e-webroot-").FullName;

        // The candidate port is not a reservation (see LoopbackPort.Reserve): another process can take it
        // between the probe and Kestrel's real bind, which surfaces as AddressInUseException. Retry that
        // one signal with a fresh candidate; any other startup failure still fails the test.
        var bound = await LoopbackPort.BindWithRetryAsync(async candidate =>
        {
            var factory = new XENodeE2EWebApplicationFactory(candidate, webRoot);
            try
            {
                await factory.InitializeAsync();
            }
            catch (Exception exception) when (IsAddressInUse(exception))
            {
                await factory.DisposeAsync();
                return null;
            }

            return new BoundHost(factory, candidate);
        });

        await using var factory = bound.Factory;

        await Assert.That(factory.ServerAddress).Contains($":{bound.Port}");

        using var client = new HttpClient();
        var response = await client.GetAsync($"{factory.ServerAddress.TrimEnd('/')}/health/live");

        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    /// <summary>
    ///     Kestrel reports a taken port as <see cref="AddressInUseException" />, wrapped by
    ///     <see cref="IOException" /> from the address binder and possibly further by the host builder,
    ///     so the whole chain is searched rather than the outermost type alone.
    /// </summary>
    private static bool IsAddressInUse(Exception exception) =>
        exception switch
        {
            AddressInUseException => true,
            AggregateException aggregate => aggregate.InnerExceptions.Any(IsAddressInUse),
            { InnerException: { } inner } => IsAddressInUse(inner),
            _ => false,
        };

    private sealed record BoundHost(XENodeE2EWebApplicationFactory Factory, int Port);
}
