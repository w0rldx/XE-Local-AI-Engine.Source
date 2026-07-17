namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net.Sockets;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>
///     Pins the exception shapes <see cref="DeferredLlamaServerChatClient.IsServerGone" /> must recognize as
///     "the llama-server process is gone", because that predicate gates BOTH the operator-eject translation
///     (ejected lease → <c>LlamaServerModelEjectedException</c> → Cancelled terminal) and the pre-first-chunk
///     self-heal. The mid-response kill shape — <see cref="HttpIOException" /> with
///     <see cref="HttpRequestError.ResponseEnded" /> ("The response ended prematurely.") — was live-observed
///     during a force-eject and was NOT matched originally, so the run misclassified as a generic provider
///     failure. Connect-time shapes (refused/reset sockets, ConnectionError) were already covered.
/// </summary>
public sealed class DeferredLlamaServerChatClientServerGoneTests
{
    [Test]
    public async Task ResponseEndedMidStream_IsServerGone()
    {
        // The live force-eject shape: HttpIOException(ResponseEnded) wrapped by an SDK-level exception.
        var wrapped = new InvalidOperationException("adapter wrapper",
            new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

        await Assert.That(DeferredLlamaServerChatClient.IsServerGone(wrapped)).IsTrue();
    }

    [Test]
    public async Task ConnectionRefusedSocket_IsServerGone()
    {
        var wrapped = new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));

        await Assert.That(DeferredLlamaServerChatClient.IsServerGone(wrapped)).IsTrue();
    }

    [Test]
    public async Task ConnectionErrorHttpRequest_IsServerGone()
    {
        var exception = new HttpRequestException(HttpRequestError.ConnectionError, "connection error");

        await Assert.That(DeferredLlamaServerChatClient.IsServerGone(exception)).IsTrue();
    }

    [Test]
    public async Task UnrelatedException_IsNotServerGone()
    {
        // A model/tooling error must NOT be treated as a dead server: it would wrongly trigger self-heal or the
        // eject translation for failures the server is still alive to explain.
        var exception = new InvalidOperationException("schema validation failed");

        await Assert.That(DeferredLlamaServerChatClient.IsServerGone(exception)).IsFalse();
    }

    [Test]
    public async Task AggregateWithNestedResponseEnded_IsServerGone()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("unrelated"),
            new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

        await Assert.That(DeferredLlamaServerChatClient.IsServerGone(aggregate)).IsTrue();
    }
}
