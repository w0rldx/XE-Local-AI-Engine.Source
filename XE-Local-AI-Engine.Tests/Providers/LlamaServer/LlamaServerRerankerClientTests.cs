namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the local reranker client: it POSTs the query + documents to the rerank-role server's
///     <c>/v1/rerank</c> route and projects the (possibly score-sorted) results back into an input-aligned score list,
///     and it degrades to <see langword="null" /> — so the caller keeps its fusion order — whenever the model is not
///     installed, the server is down, the status is non-success, or the response is malformed.
/// </summary>
public sealed class LlamaServerRerankerClientTests
{
    private const string ModelName = "bge-reranker-v2-m3";
    private static readonly Uri Endpoint = new("http://127.0.0.1:18100/v1");

    [Test]
    public async Task RerankAsync_ProjectsScoreSortedResultsBackIntoInputOrder_AndTargetsRerankRoute()
    {
        // Server returns results sorted by relevance (index 2 best), so the `index` field must realign to input order.
        using var handler = new CapturingHandler(_ => JsonOk("""{"results":[{"index":2,"relevance_score":0.9},{"index":0,"relevance_score":0.5},{"index":1,"relevance_score":0.1}]}"""));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerRerankerClient(ReadySupervisor(), http, NullLogger<LlamaServerRerankerClient>.Instance);

        var scores = await client.RerankAsync(ModelName, "the query", ["doc zero", "doc one", "doc two"], CancellationToken.None);

        AssertEx.True(scores is not null, "A well-formed rerank response must yield aligned scores.");
        AssertEx.Equal(3, scores!.Count);
        AssertEx.Equal(0.5, scores[0]);
        AssertEx.Equal(0.1, scores[1]);
        AssertEx.Equal(0.9, scores[2]);
        AssertEx.True(handler.LastRequestUri!.AbsoluteUri.EndsWith("/v1/rerank", StringComparison.Ordinal),
            $"The reranker must POST /v1/rerank, not '{handler.LastRequestUri}'.");
    }

    [Test]
    public async Task RerankAsync_ServerDown_ReturnsNullToDegrade()
    {
        using var handler = new CapturingHandler(_ => throw new HttpRequestException("Connection refused."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerRerankerClient(ReadySupervisor(), http, NullLogger<LlamaServerRerankerClient>.Instance);

        var scores = await client.RerankAsync(ModelName, "the query", ["a", "b"], CancellationToken.None);

        AssertEx.Null(scores, "A transport failure must degrade to null (keep fusion order), not throw.");
    }

    [Test]
    public async Task RerankAsync_NonSuccessStatus_ReturnsNullToDegrade()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerRerankerClient(ReadySupervisor(), http, NullLogger<LlamaServerRerankerClient>.Instance);

        var scores = await client.RerankAsync(ModelName, "the query", ["a", "b"], CancellationToken.None);

        AssertEx.Null(scores, "A non-success rerank status must degrade to null.");
    }

    [Test]
    public async Task RerankAsync_ModelNotInstalled_ReturnsNullToDegrade()
    {
        // The supervisor rejects the spawn (model not installed / cap reached) with a sanitized LlamaRuntimeException.
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EnsureRunningAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                  .Returns<Task<LlamaServerEndpoint>>(_ => throw new LlamaRuntimeException("The requested model is not installed."));
        using var handler = new CapturingHandler(_ => JsonOk("""{"results":[]}"""));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerRerankerClient(supervisor, http, NullLogger<LlamaServerRerankerClient>.Instance);

        var scores = await client.RerankAsync(ModelName, "the query", ["a", "b"], CancellationToken.None);

        AssertEx.Null(scores, "An unavailable reranker model must degrade to null.");
    }

    [Test]
    public async Task RerankAsync_ResultCountMismatch_ReturnsNullToDegrade()
    {
        // Two documents in, one score out — a malformed/partial response must never yield a silently wrong ranking.
        using var handler = new CapturingHandler(_ => JsonOk("""{"results":[{"index":0,"relevance_score":0.7}]}"""));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerRerankerClient(ReadySupervisor(), http, NullLogger<LlamaServerRerankerClient>.Instance);

        var scores = await client.RerankAsync(ModelName, "the query", ["a", "b"], CancellationToken.None);

        AssertEx.Null(scores, "A per-document count mismatch must degrade to null.");
    }

    [Test]
    public async Task RerankAsync_ServerHangsMidScoring_DegradesFastWithoutCallerCancellation()
    {
        // The server accepts the request but never responds; the client's own bounded timeout must fire and degrade.
        using var handler = new CapturingHandler(_ => JsonOk("""{"results":[]}"""), TimeSpan.FromSeconds(30));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerRerankerClient(ReadySupervisor(), http, NullLogger<LlamaServerRerankerClient>.Instance,
            requestTimeout: TimeSpan.FromMilliseconds(50));

        using var callerCts = new CancellationTokenSource();
        var scores = await client.RerankAsync(ModelName, "the query", ["a", "b"], callerCts.Token);

        AssertEx.Null(scores, "A hung reranker must degrade to null once the bounded timeout fires.");
        AssertEx.False(callerCts.IsCancellationRequested, "The caller token must not be cancelled by the internal timeout.");
    }

    [Test]
    public void ResolveRequestTimeout_ScalesWithThePool_FlooredAtTheSingleRequestBudget_AndCapped()
    {
        // A cross-encoder scores the pool sequentially on a --parallel 1 server, so a flat budget silently degrades
        // reranking on any box slow enough to need more than it.
        AssertEx.Equal(TimeSpan.FromSeconds(5), LlamaServerRerankerClient.ResolveRequestTimeout(documentCount: 0),
            "An empty pool gets the floor, not zero.");
        AssertEx.Equal(TimeSpan.FromSeconds(5.5), LlamaServerRerankerClient.ResolveRequestTimeout(documentCount: 1),
            "The floor still applies with the per-document allowance on top.");
        AssertEx.Equal(TimeSpan.FromSeconds(15), LlamaServerRerankerClient.ResolveRequestTimeout(documentCount: 20),
            "The default max(20, 4 x limit) pool gets a budget that scales with it.");
        AssertEx.Equal(TimeSpan.FromSeconds(30), LlamaServerRerankerClient.ResolveRequestTimeout(documentCount: 500),
            "The budget is capped however large the pool grows.");
    }

    [Test]
    public async Task RerankAsync_WhenScoringExceedsTheBudget_RecordsTheTimeoutReason()
    {
        using var handler = new CapturingHandler(_ => JsonOk("""{"results":[]}"""), TimeSpan.FromSeconds(30));
        using var http = new HttpClient(handler, disposeHandler: false);
        var logger = new CapturingLogger<LlamaServerRerankerClient>();
        var client = new LlamaServerRerankerClient(ReadySupervisor(), http, logger, requestTimeout: TimeSpan.FromMilliseconds(50));

        var scores = await client.RerankAsync(ModelName, "the query", ["a", "b"], CancellationToken.None);

        AssertEx.Null(scores, "An over-budget rerank degrades to fusion order.");
        AssertEx.Contains(logger.AllText, "Reason: timeout",
            message: "A budget-exhausted rerank must be distinguishable from an absent reranker.");
    }

    [Test]
    public async Task RerankAsync_WhenTheServerIsDown_RecordsTheUnavailableReason()
    {
        using var handler = new CapturingHandler(_ => throw new HttpRequestException("Connection refused."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var logger = new CapturingLogger<LlamaServerRerankerClient>();
        var client = new LlamaServerRerankerClient(ReadySupervisor(), http, logger);

        var scores = await client.RerankAsync(ModelName, "the query", ["a", "b"], CancellationToken.None);

        AssertEx.Null(scores, "A transport failure degrades to fusion order.");
        AssertEx.Contains(logger.AllText, "Reason: unavailable",
            message: "An absent reranker must be distinguishable from one that ran out of time.");
    }

    [Test]
    public async Task RerankAsync_EmptyDocuments_ReturnsNullWithoutSpawning()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        using var handler = new CapturingHandler(_ => JsonOk("""{"results":[]}"""));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerRerankerClient(supervisor, http, NullLogger<LlamaServerRerankerClient>.Instance);

        var scores = await client.RerankAsync(ModelName, "the query", [], CancellationToken.None);

        AssertEx.Null(scores);
        await supervisor.DidNotReceive().EnsureRunningAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>());
    }

    // A rerank request held no lease at all, so ActiveLeases stayed 0, profiling's pre-spawn claim succeeded and the
    // removal tree-killed the live scoring round-trip.

    [Test]
    public async Task RerankAsync_HoldsAnInferenceLeaseForTheRerankerRole_ForTheWholeRoundTrip()
    {
        using var lease = new RecordingInferenceLease();
        var supervisor = LeasingSupervisor(LlamaServerLeaseAcquisition.Granted(lease));
        var heldDuringRequest = false;
        using var handler = new CapturingHandler(_ =>
        {
            heldDuringRequest = !lease.Disposed;
            return JsonOk("""{"results":[{"index":0,"relevance_score":0.5}]}""");
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerRerankerClient(supervisor, http, NullLogger<LlamaServerRerankerClient>.Instance);

        var scores = await client.RerankAsync(ModelName, "the query", ["a"], CancellationToken.None);

        AssertEx.NotNull(scores);
        AssertEx.Contains(supervisor.LeasedRoles, ModelRole.Reranker);
        AssertEx.True(heldDuringRequest, "The lease must still be held while the scoring request is in flight.");
        AssertEx.True(lease.Disposed, "The lease must be released once the round-trip ends.");
    }

    [Test]
    public async Task RerankAsync_WhenProfilingOwnsTheKey_DoesNotScoreAgainstTheMeasurement_ThenSucceedsAfterItEnds()
    {
        var supervisor = LeasingSupervisor(LlamaServerLeaseAcquisition.NotRunning);
        supervisor.LeaseSequence.Enqueue(LlamaServerLeaseAcquisition.ProfilingOwned);
        supervisor.LeaseSequence.Enqueue(LlamaServerLeaseAcquisition.NotRunning);
        using var handler = new CapturingHandler(_ => JsonOk("""{"results":[{"index":0,"relevance_score":0.5}]}"""));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerRerankerClient(supervisor, http, NullLogger<LlamaServerRerankerClient>.Instance);

        var scores = await client.RerankAsync(ModelName, "the query", ["a"], CancellationToken.None);

        AssertEx.NotNull(scores);
        AssertEx.Equal(expected: 2, supervisor.EnsureCalls, "The refusal must re-ensure rather than POST to the measurement process.");
    }

    [Test]
    public async Task RerankAsync_WhenProfilingNeverReleasesTheKey_DegradesToFusionOrder()
    {
        var supervisor = LeasingSupervisor(LlamaServerLeaseAcquisition.ProfilingOwned);
        using var handler = new CapturingHandler(_ => JsonOk("""{"results":[]}"""));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerRerankerClient(supervisor, http, NullLogger<LlamaServerRerankerClient>.Instance);

        var scores = await client.RerankAsync(ModelName, "the query", ["a", "b"], CancellationToken.None);

        AssertEx.Null(scores, "Reranking is best-effort: an unavailable model degrades, it does not throw.");
        AssertEx.Null(handler.LastRequestUri, "Nothing may be scored against the measurement process.");
    }

    /// <summary>A ready supervisor whose lease acquisition (and sequence) the test controls.</summary>
    private static FakeProcessSupervisor LeasingSupervisor(LlamaServerLeaseAcquisition acquisition)
    {
        return new FakeProcessSupervisor
        {
            EnsureEndpoint = Endpoint,
            LeaseAcquisition = acquisition
        };
    }

    private static ILlamaServerProcessSupervisor ReadySupervisor()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EnsureRunningAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(new LlamaServerEndpoint(ModelName, ModelRole.Reranker, Endpoint)));
        return supervisor;
    }

    private static HttpResponseMessage JsonOk(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder, TimeSpan? delay = null)
        : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            if (delay is { } pause)
            {
                // real-timer: a hang is the input. Honors the token so the client's own bounded timeout — real wall
                // clock inside the client, with no injected TimeProvider — is what cancels the wait.
                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            }

            return responder(request);
        }
    }
}
