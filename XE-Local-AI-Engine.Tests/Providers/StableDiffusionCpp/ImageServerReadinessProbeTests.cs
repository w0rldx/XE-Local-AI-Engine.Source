namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Net;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the sd-server readiness probe polls <c>GET /sdcpp/v1/capabilities</c> (sd-server has NO <c>/health</c>):
///     the first success means ready, and connection-refused while the daemon is still loading is retried.
/// </summary>
public sealed class ImageServerReadinessProbeTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:18200/");

    [Test]
    public async Task WaitForReady_ProbesCapabilitiesRoute_AndReturnsTrueOnSuccess()
    {
        using var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new ImageServerReadinessProbe(http);

        var ready = await probe.WaitForReadyAsync(BaseAddress, TimeSpan.FromSeconds(5), CancellationToken.None);

        AssertEx.True(ready);
        AssertEx.True(handler.LastPath!.EndsWith("/sdcpp/v1/capabilities", StringComparison.Ordinal),
            $"Readiness must probe the capabilities route, not '{handler.LastPath}'.");
    }

    [Test]
    public async Task WaitForReady_RetriesConnectionRefused_UntilBound()
    {
        // Two connection-refused failures (socket not yet bound during model load), then a success.
        using var handler = new SequenceHandler(callIndex => callIndex < 2
            ? throw new HttpRequestException("Connection refused.")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new ImageServerReadinessProbe(http);

        var ready = await probe.WaitForReadyAsync(BaseAddress, TimeSpan.FromSeconds(5), CancellationToken.None);

        AssertEx.True(ready);
        AssertEx.True(handler.CallCount >= 3, "The probe must retry connection-refused failures before succeeding.");
    }

    [Test]
    public async Task WaitForReady_NeverBinds_ReturnsFalseAtDeadline()
    {
        using var handler = new SequenceHandler(_ => throw new HttpRequestException("Connection refused."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new ImageServerReadinessProbe(http);

        var ready = await probe.WaitForReadyAsync(BaseAddress, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        AssertEx.False(ready);
    }

    private sealed class SequenceHandler(Func<int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            var index = CallCount;
            CallCount++;
            return Task.FromResult(responder(index));
        }
    }
}
