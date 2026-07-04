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
    ///     Bounds a single <c>/v1/rerank</c> scoring round-trip so a reranker that accepts the request then hangs
    ///     mid-scoring degrades fast instead of stalling knowledge search for the shared <see cref="HttpClient.Timeout" />.
    ///     5s comfortably fits a real rerank of the ~20-document candidate pool of short chunks the KB search sends, while
    ///     capping a hang. A timeout is a linked-token cancellation (not the caller's), so it degrades to null like the
    ///     other failure modes rather than surfacing to the caller.
    /// </summary>
    private static readonly TimeSpan DefaultRerankRequestTimeout = TimeSpan.FromSeconds(5);

    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LlamaServerRerankerClient> _logger;
    private readonly TimeSpan _requestTimeout;

    public LlamaServerRerankerClient(ILlamaServerProcessSupervisor supervisor,
        HttpClient httpClient,
        ILogger<LlamaServerRerankerClient> logger,
        TimeSpan? requestTimeout = null)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _requestTimeout = requestTimeout ?? DefaultRerankRequestTimeout;
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

        try
        {
            var endpoint = await _supervisor.EnsureRunningAsync(modelName, ModelRole.Reranker, cancellationToken).ConfigureAwait(false);

            // BaseAddress is the OpenAI-compatible ".../v1" base (no trailing slash); the raw rerank route is ".../v1/rerank".
            var requestUri = new Uri($"{endpoint.BaseAddress.AbsoluteUri}/rerank");

            // Bound the scoring round-trip on its own linked token so a reranker that accepts then hangs mid-scoring
            // degrades fast rather than stalling for the whole HttpClient.Timeout. A fired timeout cancels timeoutCts
            // (NOT the caller token), so it lands in the degrade catch below; a real caller cancellation still propagates.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_requestTimeout);

            using var response = await _httpClient
                .PostAsJsonAsync(requestUri, new RerankRequest(query, documents), SerializerOptions, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Knowledge reranking returned a non-success status; keeping fusion order. Status: {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<RerankResponse>(SerializerOptions, timeoutCts.Token).ConfigureAwait(false);
            return ProjectScores(payload, documents.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A caller cancellation is a real cancellation, not a degrade — propagate it.
            throw;
        }
        catch (Exception exception) when (exception is LlamaRuntimeException or HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            // Model not installed / cap reached / server down / transport error / server timeout / malformed body:
            // degrade to the existing fusion order. Log the exception TYPE only — never the query or document text.
            _logger.LogWarning("Knowledge reranking unavailable; keeping fusion order. Exception type: {ExceptionType}.",
                exception.GetType().Name);
            return null;
        }
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
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents);

    private sealed record RerankResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<RerankResult>? Results);

    private sealed record RerankResult(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("relevance_score")] double RelevanceScore);
}
