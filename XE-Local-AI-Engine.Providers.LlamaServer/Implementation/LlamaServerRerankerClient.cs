namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Reranks candidate documents against a query by spawning/reusing a rerank-role <c>llama-server</c> for the resolved
///     reranker model and POSTing <c>/v1/rerank</c>. Unlike the chat/embedding adapters this does NOT go through the
///     OpenAI SDK — <c>/v1/rerank</c> is a raw llama-server route with no SDK method — so it calls the endpoint directly
///     with an injected <see cref="HttpClient" /> (the same plain-client pattern the health probe uses).
/// </summary>
/// <remarks>
///     Any failure to obtain scores — the reranker model not installed, the supervisor rejecting the spawn (loaded-cap),
///     the server being down, a transport error, or a malformed/mismatched response — returns <see langword="null" /> so
///     the caller keeps its existing fusion order (graceful degrade, mirroring the embedding degrade-to-lexical path).
///     The query and document text are never logged.
/// </remarks>
public sealed class LlamaServerRerankerClient : IRerankerClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Floor of the scoring budget, and the whole budget for a degenerate empty pool. Covers the fixed cost of the
    ///     round-trip plus the first document.
    /// </summary>
    private static readonly TimeSpan RerankTimeoutFloor = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Per-document allowance. A cross-encoder scores the pool SEQUENTIALLY on a <c>--parallel 1</c> server, so the
    ///     budget has to grow with the pool — the caller sends <c>max(20, 4 x limit)</c> ~500-token chunks, which a
    ///     CPU-only box cannot finish inside a flat 5s, and the reranker then degrades to fusion order on every search
    ///     while looking configured. 500ms/document is roughly double a measured CPU pass on a chunk that size, so the
    ///     budget is generous enough to stop punishing slow hardware without being a licence to hang.
    /// </summary>
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
    ///     allowance, capped. Bounds a reranker that accepts the request then hangs mid-scoring, without stalling
    ///     knowledge search for the shared <see cref="HttpClient.Timeout" />. A fired timeout is a linked-token
    ///     cancellation (not the caller's), so it degrades to null like the other failure modes.
    /// </summary>
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

        try
        {
            var endpoint = await _supervisor.EnsureRunningAsync(modelName, ModelRole.Reranker, cancellationToken).ConfigureAwait(false);

            // BaseAddress is the OpenAI-compatible ".../v1" base (no trailing slash); the raw rerank route is ".../v1/rerank".
            var requestUri = new Uri($"{endpoint.BaseAddress.AbsoluteUri}/rerank");

            // Bound the scoring round-trip on its own linked token so a reranker that accepts then hangs mid-scoring
            // degrades rather than stalling for the whole HttpClient.Timeout. A fired timeout cancels timeoutCts
            // (NOT the caller token), so it lands in the degrade catch below; a real caller cancellation still propagates.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(requestTimeout);

            using var response = await _httpClient
                                       .PostAsJsonAsync(requestUri, new RerankRequest(query, documents), SerializerOptions, timeoutCts.Token)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A caller cancellation is a real cancellation, not a degrade — propagate it.
            throw;
        }
        catch (Exception exception) when (exception is LlamaRuntimeException or HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            // Model not installed / cap reached / server down / transport error / scoring timeout / malformed body:
            // degrade to the existing fusion order. The reason separates "the reranker ran out of time on this pool"
            // (raise the budget, or shrink the pool, or the box is too slow for this model) from "there is no reranker"
            // — without it both present identically as fusion-ordered results.
            LogDegrade(exception is OperationCanceledException ? "timeout" : "unavailable",
                documents.Count,
                requestTimeout,
                exception.GetType().Name);
            return null;
        }
    }

    // The single degrade-logging site. Carries the reason as its own structured field so a log query can separate a
    // budget-exhausted rerank from an absent one; the detail is an exception/status NAME only — never the query or
    // document text.
    private void LogDegrade(string reason, int documentCount, TimeSpan requestTimeout, string detail)
    {
        _logger.LogWarning("Knowledge reranking degraded to fusion order. Reason: {Reason}. Documents: {DocumentCount}. Budget: {RerankTimeoutMs}ms. Detail: {Detail}.",
            reason,
            documentCount,
            (long)requestTimeout.TotalMilliseconds,
            detail);
    }

    // Reprojects the server's (index, score) results back into an input-aligned score array. The server may return the
    // results in score-sorted order, so the `index` field maps each score to its input document. Any gap, duplicate, or
    // count mismatch is treated as a malformed response → null (degrade) rather than a silently wrong ranking.
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

    private sealed record RerankRequest(
        [property: JsonPropertyName("query")]
        string Query,
        [property: JsonPropertyName("documents")]
        IReadOnlyList<string> Documents);

    private sealed record RerankResponse(
        [property: JsonPropertyName("results")]
        IReadOnlyList<RerankResult>? Results);

    private sealed record RerankResult(
        [property: JsonPropertyName("index")]
        int Index,
        [property: JsonPropertyName("relevance_score")]
        double RelevanceScore);
}
