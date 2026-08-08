namespace XE_Local_AI_Engine.Testing.FakeOllama;

using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Testing.FakeOllama.Endpoints;

/// <summary>
///     Represents fake ollama server.
/// </summary>
public sealed class FakeOllamaServer : IAsyncDisposable
{
    [SuppressMessage("Major Code Smell", "S1075:URIs should not be hardcoded", Justification = "The fake test server must bind to loopback on a dynamic port.")]
    private const string LoopbackDynamicPortUrl = "http://127.0.0.1:0";

    private readonly WebApplication _app;

    private FakeOllamaServer(WebApplication app, Uri baseAddress, FakeOllamaState state)
    {
        _app = app;
        BaseAddress = baseAddress;
        State = state;
    }

    public Uri BaseAddress { get; }

    public FakeOllamaState State { get; }

    public IReadOnlyList<FakeOllamaRequest> RecordedRequests => State.RecordedRequests;

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    [SuppressMessage("Major Code Smell", "S1075:URIs should not be hardcoded", Justification = "The fake test server must bind to loopback on a dynamic port.")]
    public static async Task<FakeOllamaServer> StartAsync(FakeOllamaOptions? options = null, CancellationToken ct = default)
    {
        var effectiveOptions = options ?? new FakeOllamaOptions();
        var state = new FakeOllamaState(effectiveOptions);
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseKestrel().UseUrls(LoopbackDynamicPortUrl);
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(index: 0, FakeOllamaJsonContext.Default));

        var app = builder.Build();
        app.MapFakeOllamaEndpoints(state);

        await app.StartAsync(ct).ConfigureAwait(false);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
                      ?? throw new InvalidOperationException("Fake Ollama server did not publish a listening address.");

        return new FakeOllamaServer(app, new Uri(address), state);
    }
}
