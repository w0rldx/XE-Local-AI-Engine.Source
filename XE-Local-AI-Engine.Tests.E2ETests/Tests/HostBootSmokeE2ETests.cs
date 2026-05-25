namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Net;
using System.Net.Sockets;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
/// Step-3 evidence: the XE node host boots under <see cref="XENodeE2EWebApplicationFactory"/>
/// on a real, pre-chosen loopback port and answers liveness. No browser, no React build —
/// this gates the Playwright layer (plan R1 retirement). Uses a throwaway web root since
/// <c>/health/live</c> does not depend on the SPA assets.
/// </summary>
public sealed class HostBootSmokeE2ETests
{
    [Test]
    public async Task Host_Boots_On_Real_Port_And_Health_Live_Returns_200()
    {
        var port = GetFreeLoopbackPort();
        var webRoot = Directory.CreateTempSubdirectory("xe-e2e-webroot-").FullName;

        await using var factory = new XENodeE2EWebApplicationFactory(port, webRoot);
        await factory.InitializeAsync();

        await Assert.That(factory.ServerAddress).Contains($":{port}");

        using var client = new HttpClient();
        var response = await client.GetAsync($"{factory.ServerAddress.TrimEnd('/')}/health/live");

        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
