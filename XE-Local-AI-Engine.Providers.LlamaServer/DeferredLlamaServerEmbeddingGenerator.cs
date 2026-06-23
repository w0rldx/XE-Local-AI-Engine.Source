namespace XE_Local_AI_Engine.Providers.LlamaServer;

using Microsoft.Extensions.AI;

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
///         match the ranker's catch set and flow through unwrapped.
///     </para>
///     <para>
///         The returned generator is <see cref="IDisposable" /> and <strong>caller-owned</strong>
///         (<see cref="ILocalModelProvider.CreateEmbeddingGenerator" />); disposing it disposes the inner adapter this
///         wrapper built. The supervisor still owns the underlying process.
///     </para>
/// </remarks>
internal sealed class DeferredLlamaServerEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly SemaphoreSlim _initGate = new(initialCount: 1, maxCount: 1);
    private readonly string _modelName;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    private IEmbeddingGenerator<string, Embedding<float>>? _inner;

    public DeferredLlamaServerEmbeddingGenerator(ILlamaServerProcessSupervisor supervisor, string modelName)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _modelName = modelName;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inner = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);
        return await inner.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
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

            var built = LlamaServerOpenAIAdapterFactory.CreateEmbeddingGenerator(endpoint.BaseAddress, _modelName);
            Volatile.Write(ref _inner, built);
            return built;
        }
        finally
        {
            _initGate.Release();
        }
    }
}
