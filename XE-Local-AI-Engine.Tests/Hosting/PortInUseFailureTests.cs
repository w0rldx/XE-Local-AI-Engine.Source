namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>A host start that lost its port after the startup probe must exit 6 like a port the probe found taken, not abort.</summary>
/// <remarks>
///     The race cannot be forced in a child engine: the probe and Kestrel open identical loopback sockets, so a holder
///     fails both or neither. The classifier decides the exit code, so it is pinned against a real Kestrel bind failure.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class PortInUseFailureTests
{
    [Test]
    public async Task TryFind_RecognizesKestrelsFailureToBindAPortAnotherProcessHolds()
    {
        // The port is the subject, so hold it for the whole test rather than releasing a candidate.
        using var holder = new TcpListener(IPAddress.Loopback, port: 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;
        var builder = WebApplication.CreateSlimBuilder();
#pragma warning disable S5332 // A loopback listener in a test, matching the engine's own plain-HTTP loopback bind.
        builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}");
#pragma warning restore S5332
        await using var app = builder.Build();

        var thrown = await AssertEx.ThrowsAsync<IOException>(() => app.StartAsync(), "Kestrel must refuse a port another socket holds.");

        AssertEx.Succeeded(PortInUseFailure.TryFind(thrown, out var bindFailure), $"Kestrel's bind failure must map to the port-in-use exit: {thrown}");
        AssertEx.Contains(bindFailure, port.ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    public void TryFind_IgnoresAnIOExceptionThatIsNotAnAddressInUse()
    {
        var unrelated = new IOException("The disk is full.", new SocketException((int)SocketError.ConnectionRefused));

        AssertEx.False(PortInUseFailure.TryFind(unrelated, out _), "Only a lost bind maps to exit 6; every other startup failure keeps crashing loudly.");
    }
}
