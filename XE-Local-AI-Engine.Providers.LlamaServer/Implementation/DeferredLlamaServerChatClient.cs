namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

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
///     <para>
///         Self-heal: the cached adapter is bound to a specific llama-server endpoint (host:port). If that process is
///         gone when a request is sent — the operator ejected the model, the runtime variant was switched, or the
///         server crashed — the socket is refused. On that connection failure (before any output has streamed) the
///         cached adapter is dropped and the supervisor is re-asked to ensure a running server (which re-spawns it),
///         then the request is retried ONCE. Without this a single eject permanently bricked chat for that model until
///         a full app restart (the adapter never re-resolved its endpoint).
///     </para>
/// </remarks>
internal sealed class DeferredLlamaServerChatClient : IChatClient
{
    // User-safe terminal message when an operator force-ejected this model mid-request. Carries no paths/ports/internals.
    private const string ModelEjectedMessage = "The model was ejected by the operator while this request was running.";

    private readonly SemaphoreSlim _initGate = new(initialCount: 1, maxCount: 1);
    private readonly string _modelName;
    private readonly TimeSpan _networkTimeout;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    private IChatClient? _inner;

    public DeferredLlamaServerChatClient(ILlamaServerProcessSupervisor supervisor, string modelName, TimeSpan networkTimeout)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _modelName = modelName;
        _networkTimeout = networkTimeout;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var healed = false;
        while (true)
        {
            var inner = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);

            // Hold an inference lease for the duration of the request so a graceful operator eject waits for it to
            // finish before teardown. A null lease (process evicting/gone) means we proceed without one and rely on the
            // self-heal below.
            var lease = _supervisor.TryAcquireInferenceLease(_modelName, ModelRole.Chat);
            try
            {
                return await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsServerGone(ex))
            {
                // Operator FORCE-eject takes priority: fail as operator-ejected (never retried) so the terminal state is
                // truthful, not a generic provider drop.
                if (lease is { WasEjected: true })
                {
                    throw new LlamaServerModelEjectedException(ModelEjectedMessage, ex);
                }

                // Otherwise (crash / runtime switch) self-heal ONCE: drop the dead adapter, re-ensure, retry. No output
                // was produced, so a retry cannot duplicate tokens.
                if (healed)
                {
                    throw;
                }

                healed = true;
                InvalidateInner();
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var healed = false;
        while (true)
        {
            var inner = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);
            var lease = _supervisor.TryAcquireInferenceLease(_modelName, ModelRole.Chat);
            var enumerator =
                inner.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            var retry = false;
            try
            {
                var first = true;
                while (true)
                {
                    bool moved;
                    try
                    {
                        // The connection to llama-server is established lazily on the first MoveNext; a refused/reset
                        // socket surfaces here (before the first update) or on a later pull (mid-stream).
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsServerGone(ex))
                    {
                        // A force-eject killed the process under us — fail as operator-ejected (never retried), whether
                        // the drop happened before OR mid-stream.
                        if (lease is { WasEjected: true })
                        {
                            throw new LlamaServerModelEjectedException(ModelEjectedMessage, ex);
                        }

                        // Pre-first-chunk drop (crash / switch): self-heal ONCE. A mid-stream drop cannot be retried (it
                        // would replay already-yielded chunks), so it rethrows.
                        if (!first || healed)
                        {
                            throw;
                        }

                        healed = true;
                        retry = true;
                        break;
                    }

                    if (!moved)
                    {
                        yield break;
                    }

                    yield return enumerator.Current;
                    first = false;
                }
            }
            finally
            {
                lease?.Dispose();
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (!retry)
            {
                yield break;
            }

            // Reached only via the self-heal break: drop the dead adapter, then the outer loop re-ensures + retries.
            InvalidateInner();
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
        return Volatile.Read(ref _inner)?.GetService(serviceType, serviceKey);
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
            var current = Volatile.Read(ref _inner);
            if (current is not null)
            {
                return current;
            }

            var endpoint = await _supervisor.EnsureRunningAsync(_modelName, ModelRole.Chat, ct).ConfigureAwait(false);
            var built = LlamaServerOpenAIAdapterFactory.CreateChatClient(endpoint.BaseAddress, _modelName, _networkTimeout);
            Volatile.Write(ref _inner, built);
            return built;
        }
        finally
        {
            _initGate.Release();
        }
    }

    // Drops the cached adapter so the next EnsureInnerAsync re-resolves the endpoint and re-spawns the server via the
    // supervisor. Idempotent and safe under concurrency (the loser of the swap simply disposes nothing).
    private void InvalidateInner()
    {
        var stale = Interlocked.Exchange(ref _inner, null);
        stale?.Dispose();
    }

    // True when the exception chain indicates the target llama-server is unreachable (refused / connection error) — i.e.
    // the process is gone — rather than a model/runtime error. Walks the full chain (including AggregateException fan-out
    // from the OpenAI SDK retry policy, which surfaces the refusal as ClientResultException -> HttpRequestException ->
    // SocketException ConnectionRefused).
    private static bool IsServerGone(Exception exception)
    {
        var queue = new Queue<Exception>();
        queue.Enqueue(exception);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            switch (current)
            {
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.HostUnreachable or SocketError.TimedOut }:
                    return true;
                case HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError }:
                    return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var nested in aggregate.InnerExceptions)
                {
                    queue.Enqueue(nested);
                }
            }
            else if (current.InnerException is not null)
            {
                queue.Enqueue(current.InnerException);
            }
        }

        return false;
    }
}
