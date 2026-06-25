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
    private readonly SemaphoreSlim _initGate = new(initialCount: 1, maxCount: 1);
    private readonly string _modelName;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    private IChatClient? _inner;

    public DeferredLlamaServerChatClient(ILlamaServerProcessSupervisor supervisor, string modelName)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _modelName = modelName;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var healed = false;
        while (true)
        {
            var inner = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!healed && !cancellationToken.IsCancellationRequested && IsServerGone(ex))
            {
                // The endpoint behind the cached adapter is dead (ejected / switched / crashed). Drop it and re-ensure a
                // running server, then retry once. No output was produced, so a retry cannot duplicate tokens.
                healed = true;
                InvalidateInner();
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
            await using var enumerator =
                inner.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);

            bool moved;
            try
            {
                // The connection to llama-server is established lazily on the first MoveNext; a refused socket surfaces
                // here, before any update is yielded — the only point a retry is safe.
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!healed && !cancellationToken.IsCancellationRequested && IsServerGone(ex))
            {
                healed = true;
                InvalidateInner();
                continue; // await using disposes the dead enumerator, then we re-ensure + retry from scratch.
            }

            while (moved)
            {
                yield return enumerator.Current;
                // Past the first update the stream is live; a mid-stream drop is NOT retried (it would duplicate output).
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }

            yield break;
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
            var built = LlamaServerOpenAIAdapterFactory.CreateChatClient(endpoint.BaseAddress, _modelName);
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
