namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Asks a native decision model one question by leasing its decision-role <c>llama-server</c> (starting it when cold)
///     and POSTing <c>/v1/systemone</c>. Shaped from <see cref="LlamaServerRerankerClient" />.
/// </summary>
/// <remarks>
///     Unlike the reranker it DOES ensure-spawn: a graph waiting on a decision has no fallback order to degrade to. Every
///     server-side failure comes back as a typed <see cref="LlamaServerSystemOneResult" />, because the graph routes 501
///     differently from a timeout. The call is detached from the caller's token like the reranker's: the server finishes
///     the pass anyway, so the lease stays held until it settles. Request text is never logged.
/// </remarks>
public sealed class LlamaServerSystemOneClient : ILlamaServerSystemOneClient
{
    /// <summary>Named, resilience-free <see cref="HttpClient" /> for the non-idempotent POST (see the registration).</summary>
    public const string HttpClientName = "llamaserver-systemone";

    /// <summary>The one question every request carries; the response is read back under the same name.</summary>
    public const string QuestionName = "q";

    /// <summary>The longest server error text kept for the node log.</summary>
    private const int MaxDetailLength = 300;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan TimeoutFloor = TimeSpan.FromSeconds(5);

    /// <summary>A decision pass is one forward pass over the prompt, so the budget grows with the prompt, not with output.</summary>
    private static readonly TimeSpan TimeoutPerInputToken = TimeSpan.FromMilliseconds(10);

    private static readonly TimeSpan TimeoutCeiling = TimeSpan.FromSeconds(30);

    // One call per model at a time: the process is --parallel 1, so a second request would spend its budget queued on the server.
    // One gate per model name for the process lifetime: a handful of decision models never makes this grow.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelGates = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly ILogger<LlamaServerSystemOneClient> _logger;
    private readonly TimeSpan? _requestTimeoutOverride;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    public LlamaServerSystemOneClient(ILlamaServerProcessSupervisor supervisor,
        HttpClient httpClient,
        ILogger<LlamaServerSystemOneClient> logger,
        TimeSpan? requestTimeout = null)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _requestTimeoutOverride = requestTimeout;
    }

    /// <summary>The call's own budget: 5 s plus 10 ms per estimated input token, capped at 30 s.</summary>
    /// <remarks>The estimate is characters / 4 over everything the server tokenizes; it bounds a hang, it does not predict latency.</remarks>
    internal static TimeSpan ResolveRequestTimeout(LlamaServerSystemOneRequest request)
    {
        long characters = request.State.Length + request.Instructions.Length;
        characters += request.Labels.Sum(static label => (long)label.Length);
        characters += request.Criteria.Values.Sum(static description => (long)description.Length);
        var scaled = TimeoutFloor + (TimeoutPerInputToken * (characters / 4));
        return scaled > TimeoutCeiling ? TimeoutCeiling : scaled;
    }

    public async Task<LlamaServerSystemOneResult> DecideAsync(LlamaServerSystemOneRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ModelName))
        {
            throw new ArgumentException("A decision request must name its model.", nameof(request));
        }

        var requestTimeout = _requestTimeoutOverride ?? ResolveRequestTimeout(request);

        // Waiting here is bounded only by the caller (the node's own timeout); the call's budget starts once it owns the model.
        var gate = _modelGates.GetOrAdd(request.ModelName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ILlamaServerInferenceLease? lease = null;
        var detached = false;
        try
        {
            var acquisition = _supervisor.TryAcquireInferenceLease(request.ModelName, ModelRole.Decision);
            LlamaServerEndpoint? endpoint = null;
            if (acquisition is { Lease: null, ProcessEvicting: false, ProcessProfiling: false })
            {
                // Cold: start it, then lease what the ensure returned. A second "not running" after a real ensure proceeds leaseless, as the embedder does.
                endpoint = await _supervisor.EnsureRunningAsync(request.ModelName, ModelRole.Decision, cancellationToken).ConfigureAwait(false);
                acquisition = _supervisor.TryAcquireInferenceLease(request.ModelName, ModelRole.Decision);
            }

            if (acquisition.ProcessEvicting)
            {
                return Fail(LlamaServerSystemOneOutcome.Evicting, "An operator eject is draining the decision model.");
            }

            if (acquisition.ProcessProfiling)
            {
                return Fail(LlamaServerSystemOneOutcome.Busy, "A profiling or benchmark run owns the decision model right now.");
            }

            lease = acquisition.Lease;
            endpoint = acquisition.Endpoint
                       ?? endpoint
                       ?? await _supervisor.EnsureRunningAsync(request.ModelName, ModelRole.Decision, cancellationToken).ConfigureAwait(false);

            // BaseAddress is the OpenAI-compatible ".../v1" base; the systemone route is ".../v1/systemone".
            var requestUri = new Uri($"{endpoint.BaseAddress.AbsoluteUri}/systemone");
            var call = PostDetachedAsync(lease, gate, requestUri, request, requestTimeout);
            detached = true;
            return await call.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LlamaRuntimeException exception) when (!detached)
        {
            _logger.LogWarning("The decision model {ModelName} could not be started: {Reason}", request.ModelName, exception.Message);
            return Fail(LlamaServerSystemOneOutcome.Unavailable, exception.Message);
        }
        finally
        {
            if (!detached)
            {
                lease?.Dispose();
                gate.Release();
            }
        }
    }

    /// <summary>The round-trip, bounded only by its own budget; owns the lease and the model's gate, and never faults.</summary>
    private async Task<LlamaServerSystemOneResult> PostDetachedAsync(ILlamaServerInferenceLease? lease,
        SemaphoreSlim gate,
        Uri requestUri,
        LlamaServerSystemOneRequest request,
        TimeSpan requestTimeout)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = new CancellationTokenSource(requestTimeout);
            using var response = await _httpClient.PostAsJsonAsync(requestUri, BuildBody(request), SerializerOptions, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadDetailAsync(response, timeoutCts.Token).ConfigureAwait(false);
                // b11669 answers a prompt longer than one physical batch with HTTP 500 "input (N tokens) is too large to process", so that text is a refused request, not a transport fault.
                var outcome = response.StatusCode switch
                {
                    HttpStatusCode.NotImplemented => LlamaServerSystemOneOutcome.NotDecisionModel,
                    HttpStatusCode.BadRequest => LlamaServerSystemOneOutcome.InvalidRequest,
                    _ when detail.Contains("too large to process", StringComparison.OrdinalIgnoreCase) => LlamaServerSystemOneOutcome.InvalidRequest,
                    _ => LlamaServerSystemOneOutcome.Transport
                };
                _logger.LogWarning("Decision model {ModelName} answered HTTP {Status}: {Detail}", request.ModelName, (int)response.StatusCode, detail);
                return Fail(outcome, detail, stopwatch.ElapsedMilliseconds);
            }

            var payload = await response.Content.ReadFromJsonAsync<SystemOneResponse>(SerializerOptions, timeoutCts.Token).ConfigureAwait(false);
            if (payload?.Answers is not { } answers
                || !answers.TryGetValue(QuestionName, out var wire)
                || string.IsNullOrWhiteSpace(wire.Type))
            {
                _logger.LogWarning("Decision model {ModelName} answered without an answer for question '{Question}'.", request.ModelName, QuestionName);
                return Fail(LlamaServerSystemOneOutcome.Malformed, "The response carried no answer for the question.", stopwatch.ElapsedMilliseconds);
            }

            return new LlamaServerSystemOneResult
            {
                Outcome = LlamaServerSystemOneOutcome.Answered,
                Answer = new LlamaServerSystemOneAnswer
                {
                    Type = wire.Type,
                    Choice = wire.Choice,
                    Noul = wire.Noul,
                    Score = wire.Score,
                    Legend = wire.Legend,
                    Probabilities = wire.Probabilities,
                    Confidence = wire.Confidence
                },
                InputTokens = payload.Usage?.InputTokens,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (Exception exception)
        {
            // Timeout, transport error or an unparseable body; the exception NAME is the whole detail, the request text never is.
            var outcome = exception switch
            {
                OperationCanceledException => LlamaServerSystemOneOutcome.Timeout,
                JsonException => LlamaServerSystemOneOutcome.Malformed,
                _ => LlamaServerSystemOneOutcome.Transport
            };
            _logger.LogWarning("Decision model {ModelName} call failed after {ElapsedMs} ms (budget {BudgetMs} ms): {Error}",
                request.ModelName,
                stopwatch.ElapsedMilliseconds,
                (long)requestTimeout.TotalMilliseconds,
                exception.GetType().Name);
            return Fail(outcome, exception.GetType().Name, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            lease?.Dispose();
            gate.Release();
        }
    }

    /// <summary>The wire body: one question named <see cref="QuestionName" />, with criteria shaped per question type.</summary>
    internal static JsonObject BuildBody(LlamaServerSystemOneRequest request)
    {
        var question = new JsonObject
        {
            ["type"] = request.QuestionType switch
            {
                LlamaServerSystemOneQuestionType.Noul => "noul",
                LlamaServerSystemOneQuestionType.Score => "score",
                _ => "choice"
            },
            ["instructions"] = request.Instructions
        };

        if (request.QuestionType == LlamaServerSystemOneQuestionType.Choice)
        {
            var criteria = new JsonObject();
            foreach (var label in request.Labels)
            {
                criteria[label] = request.Criteria.TryGetValue(label, out var description) ? description : null;
            }

            question["criteria"] = criteria;
        }
        else if (request.QuestionType == LlamaServerSystemOneQuestionType.Score)
        {
            question["criteria"] = new JsonArray([.. request.Labels.Select(static level => JsonValue.Create(level))]);
        }

        return new JsonObject
        {
            ["state"] = request.State,
            ["questions"] = new JsonObject
            {
                [QuestionName] = question
            }
        };
    }

    private static async Task<string> ReadDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        var status = $"HTTP {(int)response.StatusCode}";
        if (body.Length == 0)
        {
            return status;
        }

        return body.Length > MaxDetailLength ? $"{status}: {body[..MaxDetailLength]}…" : $"{status}: {body}";
    }

    private static LlamaServerSystemOneResult Fail(LlamaServerSystemOneOutcome outcome, string detail, long? durationMs = null) =>
        new()
        {
            Outcome = outcome,
            Detail = detail,
            DurationMs = durationMs
        };

    private sealed class SystemOneResponse
    {
        [JsonPropertyName("answers")]
        public Dictionary<string, SystemOneWireAnswer>? Answers { get; init; }

        [JsonPropertyName("usage")]
        public SystemOneUsage? Usage { get; init; }
    }

    private sealed class SystemOneWireAnswer
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("choice")]
        public string? Choice { get; init; }

        [JsonPropertyName("noul")]
        public double? Noul { get; init; }

        [JsonPropertyName("score")]
        public double? Score { get; init; }

        [JsonPropertyName("legend")]
        public Dictionary<string, string>? Legend { get; init; }

        [JsonPropertyName("probabilities")]
        public Dictionary<string, double>? Probabilities { get; init; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; init; }
    }

    private sealed class SystemOneUsage
    {
        [JsonPropertyName("input_tokens")]
        public int? InputTokens { get; init; }
    }
}
