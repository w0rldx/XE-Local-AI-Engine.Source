namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.ClientModel;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     An <see cref="IEmbeddingGenerator{TInput,TEmbedding}" /> that defers the embedding process start to first use:
///     the supervisor ensure-runs a non-<c>none</c>-pooling embedding process on the first
///     <see cref="GenerateAsync" /> call, then delegates to the MEAI OpenAI embedding adapter over its endpoint.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Lexical-fallback contract:</strong>
///         <see cref="EmbeddingPlaybookRetrievalRanker" /> degrades to its lexical ranker only when this generator
///         throws <see cref="HttpRequestException" /> / <see cref="IOException" />. Process-unavailable failures
///         surface as <see cref="LlamaRuntimeException" />, so they are wrapped to <see cref="IOException" /> here to
///         keep that fallback intact; the inner adapter's own transport <see cref="HttpRequestException" />s already
///         match the ranker's catch set and flow through unwrapped. A non-2xx HTTP RESPONSE is a third case: the MEAI
///         OpenAI adapter raises <see cref="ClientResultException" /> for it, which matched no caller's catch set and so
///         escaped as an unclassified failure — it is translated to a status-carrying
///         <see cref="HttpRequestException" /> in <see cref="GenerateAsync" />. Every failure this generator can produce
///         therefore lands in the single <c>HttpRequestException</c>/<c>IOException</c> set callers already handle.
///     </para>
///     <para>
///         The returned generator is <see cref="IDisposable" /> and <strong>caller-owned</strong>
///         (<see cref="ILocalModelProvider.CreateEmbeddingGenerator" />); disposing it disposes the inner adapter this
///         wrapper built. The supervisor still owns the underlying process.
///     </para>
/// </remarks>
internal sealed class DeferredLlamaServerEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>Cap on how much of a llama-server error body is carried into the failure message (and therefore the log).</summary>
    private const int MaxDetailLength = 512;

    /// <summary>
    ///     How many times a request re-ensures around a profiling spawn before degrading. Profiling holds the per-key
    ///     single-flight gate through its own teardown, so one re-ensure normally suffices.
    /// </summary>
    private const int MaxProfilingReEnsures = 3;

    private readonly SemaphoreSlim _initGate = new(initialCount: 1, maxCount: 1);
    private readonly string _modelName;
    private readonly TimeSpan _networkTimeout;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    private IEmbeddingGenerator<string, Embedding<float>>? _inner;

    public DeferredLlamaServerEmbeddingGenerator(ILlamaServerProcessSupervisor supervisor, string modelName, TimeSpan networkTimeout)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _modelName = modelName;
        _networkTimeout = networkTimeout;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Hold an inference lease for the request's lifetime, exactly as the chat path does. Without it this role's
        // ActiveLeases stayed 0, so a profiling pre-spawn eviction claimed the process and tree-killed the embedding
        // mid-flight — and an operator eject drained past it for the same reason.
        var (inner, lease) = await EnsureLeasedInnerAsync(cancellationToken).ConfigureAwait(false);
        using var held = lease;
        try
        {
            return await inner.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ClientResultException or HttpRequestException or IOException)
        {
            // Self-heal seam, mirroring DeferredLlamaServerChatClient.InvalidateInner: the cached adapter is bound to
            // ONE endpoint for this generator's whole lifetime, so once the embedding process behind it is gone
            // (ejected, respawned on a new port, crashed) every later call would retry a dead address forever. Drop the
            // adapter and the next call re-ensures the process through the supervisor and re-resolves its endpoint.
            // No caller can reach that state today — all four scope this generator to a single document/search — so
            // this is latent-only; it exists so a future long-lived caller degrades instead of failing permanently.
            if (DeferredLlamaServerChatClient.IsServerGone(exception))
            {
                InvalidateInner();
            }

            // The MEAI OpenAI adapter reports a non-2xx from llama-server as System.ClientModel's ClientResultException,
            // which is in NOBODY's catch set upstream: it escaped the ranker's lexical-fallback set AND the knowledge
            // ingestion pipeline's, surfacing as a bare "Ingestion failed unexpectedly" whose log line recorded only the
            // type name. That made a deterministic, fully-reproducible server rejection undiagnosable from logs.
            // Translate it at the provider boundary — the same job the LlamaRuntimeException wrap below does — so the
            // SDK type never reaches the application layer and callers keep one transport-failure catch set. The
            // already-conforming HttpRequestException/IOException shapes rethrow unchanged.
            if (exception is not ClientResultException clientResult)
            {
                throw;
            }

            throw new HttpRequestException(DescribeFailure(clientResult), clientResult, clientResult.Status switch
            {
                > 0 and var status => (HttpStatusCode)status,
                _ => null
            });
        }
    }

    /// <summary>
    ///     The adapter to embed through, together with the request-lifetime lease over its process.
    ///     <para>
    ///         A profiling/benchmark spawn owning this key is refused and RETRIED rather than embedded around: the
    ///         cached adapter is bound to an endpoint the measurement process may now answer on (the port allocator
    ///         commonly re-uses the freed one), so proceeding would contaminate the measurement and then die to its
    ///         teardown. A draining operator eject is refused outright. Both give up as <see cref="IOException" /> —
    ///         the transport-failure set every caller of this generator already handles — once the bounded retry is
    ///         spent, so retrieval degrades to its lexical fallback instead of failing unclassified.
    ///     </para>
    /// </summary>
    private async Task<(IEmbeddingGenerator<string, Embedding<float>> Inner, ILlamaServerInferenceLease? Lease)> EnsureLeasedInnerAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            var (inner, fromCache) = await EnsureInnerAsync(ct).ConfigureAwait(false);
            var acquisition = _supervisor.TryAcquireInferenceLease(_modelName, ModelRole.Embedding);
            if (acquisition.ProcessEvicting)
            {
                throw new IOException("The embedding model is being ejected by the operator; this request was not started.");
            }

            // "Not running" read against a CACHED adapter is ambiguous: unlike the chat client, this one can serve a
            // request without ensuring anything, and "not running" is exactly what profiling's remove-then-register
            // window looks like from here — with the freed port commonly re-handed to the measurement spawn. Drop the
            // adapter and take the answer from a process this call actually resolved. A second "not running", now
            // after a real ensure, means genuinely absent and proceeds leaseless as before.
            var unresolved = acquisition.Lease is null && !acquisition.ProcessProfiling && fromCache;
            if (!acquisition.ProcessProfiling && !unresolved)
            {
                return (inner, acquisition.Lease);
            }

            InvalidateInner();
            if (attempt++ >= MaxProfilingReEnsures)
            {
                throw new IOException(acquisition.ProcessProfiling
                    ? "The embedding model is being profiled by a benchmark right now; this request was not started."
                    : "The embedding model could not be resolved to a running process; this request was not started.");
            }
        }
    }

    /// <summary>
    ///     Drops the cached adapter so the next <see cref="GenerateAsync" /> re-resolves the endpoint and re-ensures the
    ///     embedding process via the supervisor. Idempotent and safe under concurrency (the loser of the swap disposes
    ///     nothing). Mirrors <c>DeferredLlamaServerChatClient.InvalidateInner</c>.
    /// </summary>
    private void InvalidateInner()
    {
        Interlocked.Exchange(ref _inner, null)?.Dispose();
    }

    /// <summary>
    ///     Builds the sanitized transport-failure message for a llama-server non-2xx.
    /// </summary>
    /// <remarks>
    ///     Includes the HTTP status and the server's own error text. That text is llama-server's diagnostic (e.g.
    ///     <c>"input (678 tokens) is too large to process. increase the physical batch size (current batch size: 512)"</c>),
    ///     NOT the caller's input — the endpoint is loopback-bound and never echoes the request body — so surfacing it
    ///     carries no document/chunk content and does not weaken the repo's no-content-in-logs rule. Omitting it is what
    ///     turned a one-line fix into an investigation.
    ///     <para>
    ///         The body is capped at <see cref="MaxDetailLength" /> characters anyway: this string reaches a log, the
    ///         response is not under our control, and one unbounded error body should not be able to flood the node log.
    ///         Every llama-server diagnostic worth reading is far shorter than the cap.
    ///     </para>
    /// </remarks>
    private static string DescribeFailure(ClientResultException exception)
    {
        var detail = exception.GetRawResponse()?.Content?.ToString();
        var status = exception.Status > 0
            ? exception.Status.ToString(CultureInfo.InvariantCulture)
            : "unknown";

        if (string.IsNullOrWhiteSpace(detail))
        {
            return $"The llama-server embedding endpoint returned HTTP {status}.";
        }

        var trimmed = detail.Trim();
        if (trimmed.Length > MaxDetailLength)
        {
            trimmed = string.Concat(trimmed.AsSpan(0, MaxDetailLength), "…");
        }

        return $"The llama-server embedding endpoint returned HTTP {status}: {trimmed}";
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return _inner?.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        _inner?.Dispose();
        _initGate.Dispose();
    }

    private async Task<(IEmbeddingGenerator<string, Embedding<float>> Inner, bool FromCache)> EnsureInnerAsync(CancellationToken ct)
    {
        var existing = Volatile.Read(ref _inner);
        if (existing is not null)
        {
            return (existing, true);
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inner is not null)
            {
                return (_inner, true);
            }

            LlamaServerEndpoint endpoint;
            try
            {
                endpoint = await _supervisor.EnsureRunningAsync(_modelName, ModelRole.Embedding, ct).ConfigureAwait(false);
            }
            catch (LlamaRuntimeException exception)
            {
                // Re-shape to the ranker's caught transport set so an unavailable embedding process degrades to the
                // lexical fallback instead of hard-failing retrieval. Message is already sanitized.
                throw new IOException(exception.Message, exception);
            }

            var built = LlamaServerOpenAIAdapterFactory.CreateEmbeddingGenerator(endpoint.BaseAddress, _modelName, _networkTimeout);
            Volatile.Write(ref _inner, built);
            return (built, false);
        }
        finally
        {
            _initGate.Release();
        }
    }
}
