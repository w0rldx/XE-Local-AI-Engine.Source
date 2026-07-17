namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net.Sockets;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

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

    // ---- Lease refused while an eject is draining → typed operator-ejected failure, request never started ----------
    // Running the request leaseless instead would slip under the eject drain (which sees zero leases), be killed
    // mid-flight by the teardown, and — because IsServerGone matches the kill — self-heal-RESPAWN the just-ejected
    // model, so the eject would never stick.

    [Test]
    public async Task GetResponse_WhileEjectDraining_FailsAsOperatorEjected()
    {
        using var client = new DeferredLlamaServerChatClient(EvictingSupervisor(), "model-a", TimeSpan.FromSeconds(5));

        await AssertEx.ThrowsAsync<LlamaServerModelEjectedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
    }

    [Test]
    public async Task GetStreamingResponse_WhileEjectDraining_FailsAsOperatorEjected_BeforeAnyChunk()
    {
        using var client = new DeferredLlamaServerChatClient(EvictingSupervisor(), "model-a", TimeSpan.FromSeconds(5));

        var yielded = 0;
        await AssertEx.ThrowsAsync<LlamaServerModelEjectedException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            {
                yielded++;
            }
        });

        AssertEx.Equal(expected: 0, yielded); // the typed failure fired before any chunk was produced.
    }

    /// <summary>A supervisor whose (never-contacted) endpoint resolves but whose lease is refused as eject-in-progress.</summary>
    private static FakeProcessSupervisor EvictingSupervisor()
    {
        return new FakeProcessSupervisor
        {
            EnsureEndpoint = new Uri("http://127.0.0.1:9/"),
            LeaseAcquisition = LlamaServerLeaseAcquisition.Evicting
        };
    }
}
