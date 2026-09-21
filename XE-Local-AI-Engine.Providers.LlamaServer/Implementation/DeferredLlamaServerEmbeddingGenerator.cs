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
///     <strong>Lexical-fallback contract:</strong> every failure this generator can produce lands in the single
///     <see cref="HttpRequestException" /> / <see cref="IOException" /> set callers already handle, which keeps
///     <see cref="EmbeddingPlaybookRetrievalRanker" />'s degrade-to-lexical path intact (wiki 03). The generator is
///     <see cref="IDisposable" /> and <strong>caller-owned</strong>
///     (<see cref="ILocalModelProvider.CreateEmbeddingGenerator" />): disposing it disposes the inner adapter, while the supervisor still owns the process.
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
        // Hold an inference lease for the request's lifetime, exactly as the chat path does: with this role's ActiveLeases at 0, a profiling pre-spawn eviction
        // claims the process and tree-kills the embedding mid-flight, and an operator eject drains straight past it.
        var (inner, lease) = await EnsureLeasedInnerAsync(cancellationToken).ConfigureAwait(false);
        using var held = lease;
        try
        {
            return await inner.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ClientResultException or HttpRequestException or IOException)
        {
            // Self-heal seam, mirroring DeferredLlamaServerChatClient.InvalidateInner: the cached adapter is bound to ONE endpoint for this generator's whole
            // lifetime, and the cancellation guard is the chat client's, in the same operand order. Why both, and why the drop is latent-only today: wiki 03.
            if (!cancellationToken.IsCancellationRequested && DeferredLlamaServerChatClient.IsServerGone(exception))
            {
                InvalidateInner();
            }

            // The MEAI OpenAI adapter reports a non-2xx as System.ClientModel's ClientResultException, which is in NOBODY's catch set upstream, so translate it at
            // the provider boundary and keep one transport-failure catch set. Already-conforming shapes rethrow unchanged. What it cost before: wiki 03.
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
    /// </summary>
    /// <remarks>
    ///     A profiling or benchmark spawn owning this key is refused and RETRIED rather than embedded around; a draining
    ///     operator eject is refused outright. Both give up as <see cref="IOException" />, the transport-failure set
    ///     every caller already handles, once the bounded retry is spent, so retrieval degrades to its lexical fallback
    ///     instead of failing unclassified. Why a retry rather than proceeding: wiki 03.
    /// </remarks>
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

            // "Not running" read against a CACHED adapter is ambiguous (wiki 03), so drop the adapter and take the answer from a process this call actually
            // resolved. A second "not running", now after a real ensure, means genuinely absent and proceeds leaseless.
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
    ///     embedding process via the supervisor.
    /// </summary>
    /// <remarks>
    ///     Idempotent and safe under concurrency, the loser of the swap disposing nothing. Mirrors
    ///     <c>DeferredLlamaServerChatClient.InvalidateInner</c>.
    /// </remarks>
    private void InvalidateInner()
    {
        Interlocked.Exchange(ref _inner, null)?.Dispose();
    }

    /// <summary>
    ///     Builds the sanitized transport-failure message for a llama-server non-2xx.
    /// </summary>
    /// <remarks>
    ///     Includes the HTTP status and the server's own error text, which is llama-server's diagnostic and NOT the
    ///     caller's input — the endpoint is loopback-bound and never echoes the request body — so surfacing it carries
    ///     no document or chunk content and does not weaken the repo's no-content-in-logs rule. The body is capped at
    ///     <see cref="MaxDetailLength" /> characters anyway, because this string reaches a log and one unbounded error
    ///     body must not flood the node log; every diagnostic worth reading is far shorter. See wiki 03.
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
