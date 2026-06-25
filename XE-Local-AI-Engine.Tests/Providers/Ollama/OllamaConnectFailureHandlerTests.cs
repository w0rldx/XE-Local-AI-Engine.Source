namespace XE_Local_AI_Engine.Tests.Providers.Ollama;

using System.Net;
using XE_Local_AI_Engine.Providers.Ollama;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The Ollama client uses a short <see cref="System.Net.Http.SocketsHttpHandler.ConnectTimeout" />; when it fires it
///     throws an <see cref="OperationCanceledException" />, not the <see cref="HttpRequestException" /> the codebase
///     catches to detect an unreachable daemon. <see cref="OllamaConnectFailureHandler" /> normalizes that so a hung
///     connect (no caller cancellation) presents as a connection failure, while a genuine caller cancellation is left
///     untouched.
/// </summary>
// The HttpMessageInvoker (disposeHandler defaults to true) owns and disposes the handler chain it is given, so the
// handler/inner-handler instances constructed inline do not need separate disposal in these tests.
#pragma warning disable CA2000
public sealed class OllamaConnectFailureHandlerTests
{
    private static HttpRequestMessage Request()
    {
        return new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/tags");
    }

    [Test]
    public async Task SendAsync_WhenConnectTimeoutFiresWithoutCallerCancellation_TranslatesToHttpRequestException()
    {
        var connectTimeout = new TaskCanceledException("connect timed out", new TimeoutException());
        using var invoker = new HttpMessageInvoker(new OllamaConnectFailureHandler(new ThrowingHandler(connectTimeout)));

        var thrown = await AssertEx.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(Request(), CancellationToken.None));

        // The original cancellation is preserved as the inner exception so diagnostics still see the timeout cause.
        AssertEx.True(ReferenceEquals(connectTimeout, thrown.InnerException), "the connect-timeout should be the inner exception");
    }

    [Test]
    public async Task SendAsync_WhenCallerCancels_PropagatesOperationCanceledUntranslated()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var invoker = new HttpMessageInvoker(new OllamaConnectFailureHandler(new ThrowingHandler(new OperationCanceledException())));

        // A signalled caller token means a real cancellation: it must NOT be turned into an HttpRequestException.
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => invoker.SendAsync(Request(), cts.Token));
    }

    [Test]
    public async Task SendAsync_WhenInnerSucceeds_PassesResponseThrough()
    {
        using var ok = new HttpResponseMessage(HttpStatusCode.OK);
        using var invoker = new HttpMessageInvoker(new OllamaConnectFailureHandler(new RespondingHandler(ok)));

        var response = await invoker.SendAsync(Request(), CancellationToken.None);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task SendAsync_WhenInnerThrowsHttpRequestException_PropagatesUnchanged()
    {
        var refused = new HttpRequestException("connection refused");
        using var invoker = new HttpMessageInvoker(new OllamaConnectFailureHandler(new ThrowingHandler(refused)));

        var thrown = await AssertEx.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(Request(), CancellationToken.None));

        AssertEx.True(ReferenceEquals(refused, thrown), "an existing HttpRequestException must propagate unchanged");
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }

    private sealed class RespondingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(response);
        }
    }
}

#pragma warning restore CA2000
