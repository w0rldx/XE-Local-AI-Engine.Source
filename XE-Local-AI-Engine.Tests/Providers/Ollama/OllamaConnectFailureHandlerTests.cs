namespace XE_Local_AI_Engine.Tests.Providers.Ollama;

using System.Net;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The Ollama client uses a short <see cref="System.Net.Http.SocketsHttpHandler.ConnectTimeout" />, which throws
///     <see cref="OperationCanceledException" /> rather than the <see cref="HttpRequestException" /> the codebase
///     treats as an unreachable daemon. <see cref="OllamaConnectFailureHandler" /> turns a hung connect (no caller
///     cancellation) into a connection failure and leaves a genuine caller cancellation untouched.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class OllamaConnectFailureHandlerTests
{
    private static HttpRequestMessage Request()
    {
        return new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/tags");
    }

    private static HttpMessageInvoker Invoker(Exception failure)
    {
#pragma warning disable CA2000 // The invoker (disposeHandler defaults to true) owns and disposes the handler chain.
        return new HttpMessageInvoker(new OllamaConnectFailureHandler(new ThrowingHttpMessageHandler(failure)));
#pragma warning restore CA2000
    }

    private static HttpMessageInvoker Invoker(HttpResponseMessage response)
    {
#pragma warning disable CA2000 // The invoker (disposeHandler defaults to true) owns and disposes the handler chain.
        return new HttpMessageInvoker(new OllamaConnectFailureHandler(new RespondingHandler(response)));
#pragma warning restore CA2000
    }

    [Test]
    public async Task SendAsync_WhenConnectTimeoutFiresWithoutCallerCancellation_TranslatesToHttpRequestException()
    {
        var connectTimeout = new TaskCanceledException("connect timed out", new TimeoutException());
        using var invoker = Invoker(connectTimeout);

        var thrown = await AssertEx.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(Request(), CancellationToken.None));

        // The original cancellation is preserved as the inner exception so diagnostics still see the timeout cause.
        AssertEx.True(ReferenceEquals(connectTimeout, thrown.InnerException), "the connect-timeout should be the inner exception");
    }

    [Test]
    public async Task SendAsync_WhenCallerCancels_PropagatesOperationCanceledUntranslated()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var invoker = Invoker(new OperationCanceledException());

        // A signaled caller token means a real cancellation: it must NOT be turned into an HttpRequestException.
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => invoker.SendAsync(Request(), cts.Token));
    }

    [Test]
    public async Task SendAsync_WhenInnerSucceeds_PassesResponseThrough()
    {
        using var ok = new HttpResponseMessage(HttpStatusCode.OK);
        using var invoker = Invoker(ok);

        using var request = Request();
        var response = await invoker.SendAsync(request, CancellationToken.None);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task SendAsync_WhenInnerThrowsHttpRequestException_PropagatesUnchanged()
    {
        var refused = new HttpRequestException("connection refused");
        using var invoker = Invoker(refused);

        var thrown = await AssertEx.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(Request(), CancellationToken.None));

        AssertEx.True(ReferenceEquals(refused, thrown), "an existing HttpRequestException must propagate unchanged");
    }

    private sealed class RespondingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RespondingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }
}
