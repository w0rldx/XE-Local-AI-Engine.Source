namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Reranks candidate documents against a query by leasing an already-running rerank-role <c>llama-server</c> for the
///     resolved reranker model and POSTing <c>/v1/rerank</c>. It never spawns one: a cold reranker degrades.
/// </summary>
/// <remarks>
///     <c>/v1/rerank</c> has no OpenAI SDK method, so it is POSTed with an injected <see cref="HttpClient" />. A cold,
///     busy, ejecting or profiling reranker, a transport error or a malformed response returns <see langword="null" />,
///     so the caller keeps its fusion order. The caller's deadline abandons the scoring call, never cancels it: it
///     settles in the background under the client's own budget. Query and document text are never logged.
/// </remarks>
public sealed class LlamaServerRerankerClient : IRerankerClient
{
    /// <summary>
    ///     Named <see cref="HttpClient" /> for the non-idempotent <c>/v1/rerank</c> POST. Its own client rather than the
    ///     shared default one so the Aspire-installed resilience pipeline can be stripped from it alone — see the
    ///     registration in <c>LlamaServerServiceCollectionExtensions</c>.
    /// </summary>
    public const string HttpClientName = "llamaserver-reranker";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Floor of the scoring budget, and the whole budget for a degenerate empty pool. Covers the fixed cost of the
    ///     round-trip plus the first document.
    /// </summary>
    private static readonly TimeSpan RerankTimeoutFloor = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Per-document allowance: a cross-encoder scores the pool SEQUENTIALLY on a <c>--parallel 1</c> server, so the
    ///     budget has to grow with the pool.
    /// </summary>
    /// <remarks>
    ///     The caller sends <c>max(20, 4 x limit)</c> chunks of about 500 tokens, which a CPU-only machine cannot finish
    ///     inside a flat 5 s — the reranker would then degrade to fusion order on every search while looking
    ///     configured. 500 ms per document is roughly double a measured CPU pass on a chunk that size: generous enough
    ///     to stop punishing slow hardware without being a licence to hang.
    /// </remarks>
    private static readonly TimeSpan RerankTimeoutPerDocument = TimeSpan.FromMilliseconds(500);

    /// <summary>
    ///     Hard ceiling regardless of pool size: past this the search has stalled long enough that fusion order now beats
    ///     waiting, whatever the reranker is doing.
    /// </summary>
    private static readonly TimeSpan RerankTimeoutCeiling = TimeSpan.FromSeconds(30);

    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LlamaServerRerankerClient> _logger;
    private readonly TimeSpan? _requestTimeoutOverride;

    // Models with a scoring call still settling, keyed like the supervisor's process keys; the value is unused.
    private readonly ConcurrentDictionary<string, byte> _outstanding = new(StringComparer.OrdinalIgnoreCase);

    public LlamaServerRerankerClient(ILlamaServerProcessSupervisor supervisor,
        HttpClient httpClient,
        ILogger<LlamaServerRerankerClient> logger,
        TimeSpan? requestTimeout = null)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _requestTimeoutOverride = requestTimeout;
    }

    /// <summary>
    ///     Scoring budget for a pool of <paramref name="documentCount" /> documents: a floor plus a per-document
    ///     allowance, capped.
    /// </summary>
    /// <remarks>
    ///     It bounds the detached scoring call — and so how long it holds the lease and blocks the next rerank — for a
    ///     reranker that accepts the request then hangs mid-scoring. It is the call's only token, never the caller's, so
    ///     a fired timeout degrades to null like the other failure modes.
    /// </remarks>
    internal static TimeSpan ResolveRequestTimeout(int documentCount)
    {
        if (documentCount <= 0)
        {
            return RerankTimeoutFloor;
        }

        var scaled = RerankTimeoutFloor + (RerankTimeoutPerDocument * documentCount);
        return scaled > RerankTimeoutCeiling ? RerankTimeoutCeiling : scaled;
    }

    public async Task<IReadOnlyList<double>?> RerankAsync(string modelName,
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(query) || documents is null || documents.Count == 0)
        {
            return null;
        }

        // Scale the scoring budget with the pool the caller actually sent; an explicit override (tests, callers with
        // their own budget) wins outright.
        var requestTimeout = _requestTimeoutOverride ?? ResolveRequestTimeout(documents.Count);

        // One scoring call per model at a time: the server is --parallel 1 and keeps scoring an abandoned call, so queueing behind it only grows a backlog.
        if (!_outstanding.TryAdd(modelName, value: 0))
        {
            LogDegrade("busy", documents.Count, requestTimeout, "OutstandingRerank");
            return null;
        }

        ILlamaServerInferenceLease? lease = null;
        var detached = false;
        try
        {
            // Lease FIRST, and never ensure-spawn: a cold reranker is warmed outside the search, so its load time is never charged to a search budget. The lease
            // keeps a profiling pre-spawn eviction or an operator eject from killing the scoring round-trip mid-flight.
            var acquisition = _supervisor.TryAcquireInferenceLease(modelName, ModelRole.Reranker);
            if (acquisition.ProcessEvicting)
            {
                LogDegrade("ejecting", documents.Count, requestTimeout, nameof(LlamaServerLeaseAcquisition.Evicting));
                return null;
            }

            if (acquisition.ProcessProfiling)
            {
                LogDegrade("profiling", documents.Count, requestTimeout, nameof(LlamaServerLeaseAcquisition.ProfilingOwned));
                return null;
            }

            if (acquisition.Lease is null)
            {
                LogDegrade("cold", documents.Count, requestTimeout, nameof(LlamaServerLeaseAcquisition.NotRunning));
                return null;
            }

            lease = acquisition.Lease;

            // The lease pins the live process, so an ensure here only takes the reuse path; it runs only for a supervisor that grants no endpoint.
            var endpoint = acquisition.Endpoint
                           ?? await _supervisor.EnsureRunningAsync(modelName, ModelRole.Reranker, cancellationToken).ConfigureAwait(false);

            // BaseAddress is the OpenAI-compatible ".../v1" base (no trailing slash); the raw rerank route is ".../v1/rerank".
            var requestUri = new Uri($"{endpoint.BaseAddress.AbsoluteUri}/rerank");
            var scoring = ScoreDetachedAsync(modelName, lease, requestUri, query, documents, requestTimeout);
            detached = true;

            // Abandon, don't cancel: the caller's deadline ends only its WAIT. The server scores the pool either way, so the call keeps its lease and the model's
            // outstanding slot until it settles, and a real caller cancellation still propagates as OperationCanceledException.
            return await scoring.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!detached && exception is LlamaRuntimeException or HttpRequestException or IOException or OperationCanceledException)
        {
            // Only the no-endpoint fallback ensure reaches here: the reranker is unavailable, so degrade to the existing fusion order.
            LogDegrade("unavailable", documents.Count, requestTimeout, exception.GetType().Name);
            return null;
        }
        finally
        {
            if (!detached)
            {
                lease?.Dispose();
                _outstanding.TryRemove(modelName, out _);
            }
        }
    }

    /// <summary>
    ///     The scoring round-trip, detached from every caller token: bounded only by <paramref name="requestTimeout" />,
    ///     it owns the lease and the model's outstanding slot and releases both when it settles.
    /// </summary>
    /// <remarks>
    ///     Never faults: every failure is logged here once and settles as <see langword="null" />, so an abandoned call can
    ///     never surface as an unobserved task exception. Shutdown is bounded by the same 30 s ceiling.
    /// </remarks>
    private async Task<IReadOnlyList<double>?> ScoreDetachedAsync(string modelName,
        ILlamaServerInferenceLease lease,
        Uri requestUri,
        string query,
        IReadOnlyList<string> documents,
        TimeSpan requestTimeout)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(requestTimeout);
            using var response = await _httpClient
                                       .PostAsJsonAsync(requestUri, new RerankRequest
                                       {
                                           Query = query,
                                           Documents = documents
                                       }, SerializerOptions, timeoutCts.Token)
                                       .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogDegrade("status", documents.Count, requestTimeout, $"HTTP {(int)response.StatusCode}");
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<RerankResponse>(SerializerOptions, timeoutCts.Token).ConfigureAwait(false);
            var scores = ProjectScores(payload, documents.Count);
            if (scores is null)
            {
                LogDegrade("malformed", documents.Count, requestTimeout, nameof(RerankResponse));
            }

            return scores;
        }
        catch (Exception exception)
        {
            // Server down, transport error, scoring timeout or malformed body. The reason separates "ran out of time on this pool" — raise the budget,
            // shrink the pool, or the host is too slow — from "there is no reranker"; both look identical otherwise.
            LogDegrade(exception is OperationCanceledException ? "timeout" : "unavailable",
                documents.Count,
                requestTimeout,
                exception.GetType().Name);
            return null;
        }
        finally
        {
            lease.Dispose();
            _outstanding.TryRemove(modelName, out _);
        }
    }

    // The single degrade-logging site. It carries the reason as its own structured field, so a log query can separate a budget-exhausted
    // rerank from an absent one; the detail is an exception or status NAME only, never the query or document text.
    private void LogDegrade(string reason, int documentCount, TimeSpan requestTimeout, string detail)
    {
        _logger.LogWarning("Knowledge reranking degraded to fusion order. Reason: {Reason}. Documents: {DocumentCount}. Budget: {RerankTimeoutMs}ms. Detail: {Detail}.",
            reason,
            documentCount,
            (long)requestTimeout.TotalMilliseconds,
            detail);
    }

    // Reprojects the server's (index, score) results back into an input-aligned score array: the server may return them score-sorted, so the `index` field is what maps
    // each score to its input document. Any gap, duplicate or count mismatch counts as a malformed response and degrades to null rather than a silently wrong ranking.
    private static IReadOnlyList<double>? ProjectScores(RerankResponse? payload, int documentCount)
    {
        if (payload?.Results is null || payload.Results.Count != documentCount)
        {
            return null;
        }

        var scores = new double[documentCount];
        var assigned = new bool[documentCount];
        foreach (var result in payload.Results)
        {
            if (result.Index < 0 || result.Index >= documentCount || assigned[result.Index])
            {
                return null;
            }

            scores[result.Index] = result.RelevanceScore;
            assigned[result.Index] = true;
        }

        return scores;
    }

    private sealed record RerankRequest
    {
        [JsonPropertyName("query")]
        public required string Query { get; init; }

        [JsonPropertyName("documents")]
        public required IReadOnlyList<string> Documents { get; init; }
    }

    private sealed record RerankResponse(
        [property: JsonPropertyName("results")]
        IReadOnlyList<RerankResult>? Results);

    private sealed record RerankResult(
        [property: JsonPropertyName("index")]
        int Index,
        [property: JsonPropertyName("relevance_score")]
        double RelevanceScore);
}
