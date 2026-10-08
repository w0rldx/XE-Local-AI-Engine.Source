namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Testing.FakeOpenAiGateway.Endpoints;

/// <summary>
///     A loopback OpenAI-compatible gateway: <c>GET models</c> and <c>POST chat/completions</c> behind a bearer and
///     required-header check, with a fault queue and a request log, so a test drives the real external-connection
///     client over a socket.
/// </summary>
public sealed class FakeOpenAiGatewayServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeOpenAiGatewayServer(WebApplication app, Uri baseAddress, FakeOpenAiGatewayState state)
    {
        _app = app;
        BaseAddress = baseAddress;
        State = state;
    }

    /// <summary>The OpenAI base URL, ending with the base path and a slash, e.g. <c>http://127.0.0.1:N/v1/</c>.</summary>
    public Uri BaseAddress { get; }

    public FakeOpenAiGatewayState State { get; }

    /// <summary>Every API request the fake has received, oldest first.</summary>
    public IReadOnlyList<FakeOpenAiGatewayRequest> RecordedRequests => State.RecordedRequests;

    /// <summary>Start a fake gateway on loopback.</summary>
    /// <param name="options">What the gateway demands and answers, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    [SuppressMessage("Major Code Smell", "S1075:URIs should not be hardcoded", Justification = "The fake test server must bind to loopback.")]
    public static async Task<FakeOpenAiGatewayServer> StartAsync(FakeOpenAiGatewayOptions? options = null, CancellationToken cancellationToken = default)
    {
        var state = new FakeOpenAiGatewayState(options ?? new FakeOpenAiGatewayOptions());
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseKestrel().UseUrls(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{state.Options.Port}"));
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(index: 0, FakeOpenAiGatewayJsonContext.Default));

        var app = builder.Build();
        app.MapFakeOpenAiGatewayEndpoints(state);

        await app.StartAsync(cancellationToken);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
                      ?? throw new InvalidOperationException("Fake OpenAI gateway did not publish a listening address.");

        return new FakeOpenAiGatewayServer(app, new Uri(new Uri(address), state.Options.BasePath.Trim('/') + "/"), state);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
    }
}
