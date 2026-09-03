namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net.Sockets;
using Microsoft.Extensions.AI;
using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
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
    public async Task BoundBenchmarkEndpoint_BypassesSupervisorAndExternalEndpointResolution()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        var externalEndpoint = new LlamaServerEndpoint("model-a", ModelRole.Chat, new Uri("http://external.example/v1"));
        supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, Arg.Any<CancellationToken>()).Returns(externalEndpoint);
        var binding = Substitute.For<ILlamaServerEndpointBinding>();
        var pinnedEndpoint = new LlamaServerEndpoint("model-a", ModelRole.Chat, new Uri("http://127.0.0.1:19002/v1"));
        binding.GetBoundEndpoint("model-a", ModelRole.Chat).Returns(pinnedEndpoint);
        using var client = new DeferredLlamaServerChatClient(supervisor,
            "model-a",
            TimeSpan.FromSeconds(5),
            endpointBinding: binding);

        var resolved = await client.ResolveEndpointAsync(CancellationToken.None);

        AssertEx.Equal(pinnedEndpoint, resolved);
        _ = supervisor.DidNotReceiveWithAnyArgs().EnsureRunningAsync(default!, default, default);
    }

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
        var aggregate = new AggregateException(new InvalidOperationException("unrelated"),
            new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

        await Assert.That(DeferredLlamaServerChatClient.IsServerGone(aggregate)).IsTrue();
    }

    // Running the request leaseless instead would slip under the eject drain (which sees zero leases), be killed
    // mid-flight by the teardown, and — because IsServerGone matches the kill — self-heal-RESPAWN the just-ejected
    // model, so the eject would never stick.

    [Test]
    public async Task GetResponse_WhileEjectDraining_FailsAsOperatorEjected()
    {
        var scheduler = new RecordingCalibrationScheduler();
        using var client = new DeferredLlamaServerChatClient(EvictingSupervisor(),
            "model-a",
            TimeSpan.FromSeconds(5),
            scheduler);

        await AssertEx.ThrowsAsync<LlamaServerModelEjectedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        AssertEx.Equal(0, scheduler.Scheduled);
    }

    [Test]
    public async Task GetStreamingResponse_WhileEjectDraining_FailsAsOperatorEjected_BeforeAnyChunk()
    {
        var scheduler = new RecordingCalibrationScheduler();
        using var client = new DeferredLlamaServerChatClient(EvictingSupervisor(),
            "model-a",
            TimeSpan.FromSeconds(5),
            scheduler);

        var yielded = 0;
        await AssertEx.ThrowsAsync<LlamaServerModelEjectedException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            {
                yielded++;
            }
        });

        AssertEx.Equal(expected: 0, yielded); // the typed failure fired before any chunk was produced.
        AssertEx.Equal(0, scheduler.Scheduled);
    }

    // A profiling spawn replaces the chat process between the endpoint resolve and the lease lookup. Reported as
    // "not running" this used to license the request to go out leaseless — against a CACHED endpoint whose port the
    // measurement spawn commonly inherits, contaminating the measurement and then dying to its teardown.

    [Test]
    public async Task GetResponse_WhenProfilingOwnsTheKey_ReEnsuresAndNeverEngagesTheProfilingEndpoint()
    {
        var scheduler = new RecordingCalibrationScheduler();
        var supervisor = ProfilingThenOwnSupervisor();
        using var client = new DeferredLlamaServerChatClient(supervisor, "model-a", TimeSpan.FromSeconds(5), scheduler);

        // Nothing listens on either port, so the eventual transport failure is expected; the endpoint the request was
        // cleared against is what this pins.
        await AssertEx.ThrowsAsync<Exception>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        AssertEx.True(supervisor.EnsureCalls >= 2, "The refusal must re-ensure, not proceed on the cached endpoint.");
        AssertEx.Empty(scheduler.Addresses.Where(address => address == ProfilingEndpoint),
            "No request may be cleared against the profiling process.");
        AssertEx.Contains(scheduler.Addresses, OwnEndpoint);
    }

    [Test]
    public async Task GetStreamingResponse_WhenProfilingOwnsTheKey_ReEnsuresAndNeverEngagesTheProfilingEndpoint()
    {
        var scheduler = new RecordingCalibrationScheduler();
        var supervisor = ProfilingThenOwnSupervisor();
        using var client = new DeferredLlamaServerChatClient(supervisor, "model-a", TimeSpan.FromSeconds(5), scheduler);

        await AssertEx.ThrowsAsync<Exception>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            {
                AssertEx.NotNull(update);
            }
        });

        AssertEx.True(supervisor.EnsureCalls >= 2, "The refusal must re-ensure, not proceed on the cached endpoint.");
        AssertEx.Empty(scheduler.Addresses.Where(address => address == ProfilingEndpoint),
            "No stream may be opened against the profiling process.");
        AssertEx.Contains(scheduler.Addresses, OwnEndpoint);
    }

    [Test]
    public async Task GetResponse_WhenProfilingNeverReleasesTheKey_FailsAsRetryableRatherThanRunningUnleased()
    {
        // Back-to-back measurements: the retry is bounded, and what comes out is a sanitized runtime failure the
        // caller can retry — never a request that went out against the measurement process.
        var scheduler = new RecordingCalibrationScheduler();
        var supervisor = new FakeProcessSupervisor
        {
            EnsureEndpoint = ProfilingEndpoint,
            LeaseAcquisition = LlamaServerLeaseAcquisition.ProfilingOwned
        };
        using var client = new DeferredLlamaServerChatClient(supervisor, "model-a", TimeSpan.FromSeconds(5), scheduler);

        var failure = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        AssertEx.Contains(failure.Message, "being profiled", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(expected: 0, scheduler.Scheduled, "No request may be cleared while profiling owns the key.");
    }

    [Test]
    public async Task GetResponse_OnABoundBenchmarkEndpoint_TakesNoLeaseAndNeverReEnsures()
    {
        // The benchmark's OWN requests: RunExclusiveBenchmarkAsync binds its profiling endpoint and the body chats
        // over it. Asking for a lease here is answered ProfilingOwned — refusing the measurement's own request — and
        // re-ensuring would park on the per-key gate the benchmark itself holds.
        var scheduler = new RecordingCalibrationScheduler();
        var supervisor = new FakeProcessSupervisor
        {
            EnsureEndpoint = new Uri("http://127.0.0.1:11/"),
            LeaseAcquisition = LlamaServerLeaseAcquisition.ProfilingOwned
        };
        var binding = Substitute.For<ILlamaServerEndpointBinding>();
        var boundEndpoint = new LlamaServerEndpoint("model-a", ModelRole.Chat, ProfilingEndpoint);
        binding.GetBoundEndpoint("model-a", ModelRole.Chat).Returns(boundEndpoint);
        using var client = new DeferredLlamaServerChatClient(supervisor, "model-a", TimeSpan.FromSeconds(5), scheduler, binding);

        // Nothing listens on the bound port, so the request fails at transport — that it was CLEARED to go out at all,
        // rather than refused as profiling-owned, is the point.
        var failure = await AssertEx.ThrowsAsync<Exception>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        AssertEx.False(failure is LlamaRuntimeException, "The benchmark's own request must not be refused as profiling-owned.");
        AssertEx.Empty(supervisor.LeasedRoles, "A bound endpoint is the caller's own process; no lease may be attempted.");
        AssertEx.Equal(expected: 0, supervisor.EnsureCalls, "Re-ensuring would park on the gate the benchmark holds.");
        AssertEx.Equal(expected: 0, scheduler.Scheduled,
            "The bound endpoint is a transient profiling port; seeding calibration with it probes a process about to be torn down.");
    }

    [Test]
    public async Task GetStreamingResponse_OnABoundBenchmarkEndpoint_TakesNoLeaseAndNeverReEnsures()
    {
        var scheduler = new RecordingCalibrationScheduler();
        var supervisor = new FakeProcessSupervisor
        {
            EnsureEndpoint = new Uri("http://127.0.0.1:11/"),
            LeaseAcquisition = LlamaServerLeaseAcquisition.ProfilingOwned
        };
        var binding = Substitute.For<ILlamaServerEndpointBinding>();
        binding.GetBoundEndpoint("model-a", ModelRole.Chat)
               .Returns(new LlamaServerEndpoint("model-a", ModelRole.Chat, ProfilingEndpoint));
        using var client = new DeferredLlamaServerChatClient(supervisor, "model-a", TimeSpan.FromSeconds(5), scheduler, binding);

        await AssertEx.ThrowsAsync<Exception>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            {
                AssertEx.NotNull(update);
            }
        });

        AssertEx.Empty(supervisor.LeasedRoles, "A bound endpoint is the caller's own process; no lease may be attempted.");
        AssertEx.Equal(expected: 0, supervisor.EnsureCalls, "Re-ensuring would park on the gate the benchmark holds.");
        AssertEx.Equal(expected: 0, scheduler.Scheduled, "A bound endpoint must not seed the calibration target.");
    }

    private static readonly Uri ProfilingEndpoint = new("http://127.0.0.1:9/");
    private static readonly Uri OwnEndpoint = new("http://127.0.0.1:10/");

    /// <summary>
    ///     A supervisor whose first ensure hands back the port a profiling spawn now owns (and refuses the lease as
    ///     such), and whose second hands back a process of the caller's own.
    /// </summary>
    private static FakeProcessSupervisor ProfilingThenOwnSupervisor()
    {
        var supervisor = new FakeProcessSupervisor
        {
            // The transport failure against the dead port triggers ONE self-heal round, which ensures again; it must
            // keep landing on the caller's own endpoint, never back on the profiling one.
            EnsureEndpoint = OwnEndpoint
        };
        supervisor.EnsureEndpointSequence.Enqueue(ProfilingEndpoint);
        supervisor.EnsureEndpointSequence.Enqueue(OwnEndpoint);
        supervisor.LeaseSequence.Enqueue(LlamaServerLeaseAcquisition.ProfilingOwned);
        supervisor.LeaseSequence.Enqueue(LlamaServerLeaseAcquisition.NotRunning);
        return supervisor;
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

    private sealed class RecordingCalibrationScheduler : ITokenEstimatorCalibrationScheduler
    {
        public int Scheduled { get; private set; }

        /// <summary>
        ///     Every base address the request path actually engaged. Scheduling happens only once a request is cleared
        ///     to go out, so this is the witness for WHICH endpoint a request would have been sent to.
        /// </summary>
        public List<Uri> Addresses { get; } = [];

        public void Schedule(string modelName, Uri llamaServerBaseAddress)
        {
            Scheduled++;
            Addresses.Add(llamaServerBaseAddress);
        }

        public void Invalidate(string modelName)
        {
        }
    }
}
