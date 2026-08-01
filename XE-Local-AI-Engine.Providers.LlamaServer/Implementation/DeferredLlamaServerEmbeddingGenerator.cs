namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.ClientModel;
using System.Globalization;
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
        var inner = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await inner.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
        }
        catch (ClientResultException exception)
        {
            // The MEAI OpenAI adapter reports a non-2xx from llama-server as System.ClientModel's ClientResultException,
            // which is in NOBODY's catch set upstream: it escaped the ranker's lexical-fallback set AND the knowledge
            // ingestion pipeline's, surfacing as a bare "Ingestion failed unexpectedly" whose log line recorded only the
            // type name. That made a deterministic, fully-reproducible server rejection undiagnosable from logs.
            // Translate it at the provider boundary — the same job the LlamaRuntimeException wrap below does — so the
            // SDK type never reaches the application layer and callers keep one transport-failure catch set.
            throw new HttpRequestException(DescribeFailure(exception), exception, exception.Status switch
            {
                > 0 and var status => (System.Net.HttpStatusCode)status,
                _ => null
            });
        }
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

    private async Task<IEmbeddingGenerator<string, Embedding<float>>> EnsureInnerAsync(CancellationToken ct)
    {
        var existing = Volatile.Read(ref _inner);
        if (existing is not null)
        {
            return existing;
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inner is not null)
            {
                return _inner;
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
            return built;
        }
        finally
        {
            _initGate.Release();
        }
    }
}
