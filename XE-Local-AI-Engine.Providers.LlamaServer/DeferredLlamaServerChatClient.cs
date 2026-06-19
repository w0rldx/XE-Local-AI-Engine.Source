namespace XE_Local_AI_Engine.Providers.LlamaServer;

using Microsoft.Extensions.AI;

/// <summary>
///     An <see cref="IChatClient" /> that defers process start to first use: the supervisor's
///     <see cref="ILlamaServerProcessSupervisor.EnsureRunningAsync" /> is async while
///     <see cref="ILocalModelProvider.CreateChatClient" /> is sync, so the cold-start cost is paid on the first
///     <see cref="GetResponseAsync" /> / <see cref="GetStreamingResponseAsync" /> call (a normal first-token delay)
///     rather than blocking the sync factory.
/// </summary>
/// <remarks>
///     The inner MEAI OpenAI adapter is built once, keyed by the resolved endpoint, behind a single-flight gate so
///     concurrent first calls ensure-run once. The supervisor owns the underlying process; this wrapper owns only the
///     inner adapter it constructs and disposes it on <see cref="Dispose" />.
/// </remarks>
internal sealed class DeferredLlamaServerChatClient : IChatClient
{
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly string _modelName;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    private IChatClient? _inner;

    public DeferredLlamaServerChatClient(ILlamaServerProcessSupervisor supervisor, string modelName)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _modelName = modelName;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inner = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);
        return await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var inner = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        // Defer to the inner adapter only once it has been constructed; before first use there is nothing to forward.
        return _inner?.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        _inner?.Dispose();
        _initGate.Dispose();
    }

    private async Task<IChatClient> EnsureInnerAsync(CancellationToken ct)
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

            var endpoint = await _supervisor.EnsureRunningAsync(_modelName, ModelRole.Chat, ct).ConfigureAwait(false);
            var built = LlamaServerOpenAIAdapterFactory.CreateChatClient(endpoint.BaseAddress, _modelName);
            Volatile.Write(ref _inner, built);
            return built;
        }
        finally
        {
            _initGate.Release();
        }
    }
}
