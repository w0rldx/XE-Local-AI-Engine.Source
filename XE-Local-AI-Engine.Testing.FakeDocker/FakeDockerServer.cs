namespace XE_Local_AI_Engine.Testing.FakeDocker;

using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

/// <summary>
///     An in-memory Docker Engine API server: the subset of routes <c>DockerDotNetRuntimeClient</c> calls, served
///     over loopback HTTP so a test can exercise the real wire client end to end without a daemon.
///     <para>
///         Loopback TCP rather than a Unix socket is enough on purpose. The <c>unix</c>/<c>npipe</c> restriction is a
///         policy in <c>ContainerRuntimeResolver</c>, not in the wire client, and every test that builds the client
///         directly bypasses it — so the fake can be exactly the shape <c>FakeOllamaServer</c> already is.
///     </para>
///     <para>
///         One server per test, disposed with the test. A shared instance would carry a container another test
///         created into the next one's label filter, and the reset API that would need is more machinery than a
///         Kestrel start on a dynamic loopback port costs.
///     </para>
/// </summary>
public sealed class FakeDockerServer : IAsyncDisposable
{
    [SuppressMessage("Major Code Smell", "S1075:URIs should not be hardcoded", Justification = "The fake test server must bind to loopback on a dynamic port.")]
    private const string LoopbackDynamicPortUrl = "http://127.0.0.1:0";

    private readonly WebApplication _app;

    private FakeDockerServer(WebApplication app, Uri baseAddress, FakeDockerState state)
    {
        _app = app;
        BaseAddress = baseAddress;
        State = state;
    }

    /// <summary>The bound loopback address, in the form <c>ContainerRuntimeOptions.DaemonEndpoint</c> accepts.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The daemon's whole world: its identity, its images, containers and networks, and the scripts.</summary>
    public FakeDockerState State { get; }

    /// <summary>Every request the fake has served, oldest first.</summary>
    public IReadOnlyList<FakeDockerRequest> RecordedRequests => State.RecordedRequests;

    /// <summary>Start a fake daemon on a dynamic loopback port.</summary>
    /// <param name="options">The daemon identity to answer with, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static async Task<FakeDockerServer> StartAsync(FakeDockerOptions? options = null, CancellationToken cancellationToken = default)
    {
        var state = new FakeDockerState(options ?? new FakeDockerOptions());
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseKestrel().UseUrls(LoopbackDynamicPortUrl);

        var app = builder.Build();
        app.MapFakeDockerEndpoints(state);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
                      ?? throw new InvalidOperationException("Fake Docker server did not publish a listening address.");

        return new FakeDockerServer(app, new Uri(address), state);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
