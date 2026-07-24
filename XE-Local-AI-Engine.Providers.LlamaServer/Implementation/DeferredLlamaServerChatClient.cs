namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
// Aliased (not a blanket `using OpenAI.Chat`) so OpenAI.Chat.ChatMessage never collides with the MEAI ChatMessage this
// client's IChatClient signatures use — a blanket import makes ChatMessage ambiguous and breaks the interface impl.
using ChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;

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

    // User-safe terminal message when a request arrives while a graceful operator eject is draining this model: the
    // request is refused up front (never started) instead of running untracked under the drain.
    private const string ModelEjectingMessage = "The model is being ejected by the operator; this request was not started.";

    // In-process marker (duplicated from InvocationAgentFactory.LlamaDisableThinkingMarkerKey — AI.Agent does not
    // reference this assembly). When present+true, reasoning is OFF on a thinking-capable model and the outbound
    // llama-server request must carry chat_template_kwargs.enable_thinking=false so a Qwen3-class chat template stops
    // emitting a reasoning block. The Ollama `think:false` set alongside it never reaches llama.cpp — the
    // OpenAI adapter drops unmapped AdditionalProperties — so the switch is injected here instead.
    internal const string DisableThinkingMarkerKey = "xe.llama.disable_thinking";

    // The raw utf8 JSON object written at $.chat_template_kwargs. The OpenAI chat body has no typed field for it, so it
    // rides the wire via ChatCompletionOptions.Patch — MEAI's OpenAI adapter uses the ChatCompletionOptions returned by
    // ChatOptions.RawRepresentationFactory as its serialization base, Patch included (verified against MEAI 10.7).
    private static ReadOnlySpan<byte> DisableThinkingKwargs => "{\"enable_thinking\":false}"u8;

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
        options = ApplyThinkingSwitch(options);
        var healed = false;
        while (true)
        {
            var inner = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);

            // Hold an inference lease for the duration of the request so a graceful operator eject waits for it to
            // finish before teardown. A refused lease is classified: an EVICTING process fails the request up front as
            // operator-ejected (running it leaseless would slip under the eject drain, be killed mid-flight by the
            // teardown, and then self-heal-respawn the just-ejected model — so eject would never stick); only a
            // genuinely absent/exited process proceeds leaseless, relying on the self-heal below.
            var acquisition = _supervisor.TryAcquireInferenceLease(_modelName, ModelRole.Chat);
            if (acquisition.ProcessEvicting)
            {
                throw new LlamaServerModelEjectedException(ModelEjectingMessage);
            }

            var lease = acquisition.Lease;
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
        options = ApplyThinkingSwitch(options);
        var healed = false;
        while (true)
        {
            var inner = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);

            // Same refusal classification as the non-streaming path: an eject-in-progress fails the request before the
            // stream opens; only an absent/exited process streams leaseless and relies on the pre-first-chunk self-heal.
            var acquisition = _supervisor.TryAcquireInferenceLease(_modelName, ModelRole.Chat);
            if (acquisition.ProcessEvicting)
            {
                throw new LlamaServerModelEjectedException(ModelEjectingMessage);
            }

            var lease = acquisition.Lease;
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

    /// <summary>
    ///     When the turn carries the <see cref="DisableThinkingMarkerKey" /> marker (reasoning OFF on a thinking-capable
    ///     model), returns a clone of <paramref name="options" /> whose <see cref="ChatOptions.RawRepresentationFactory" />
    ///     yields a <see cref="ChatCompletionOptions" /> with <c>chat_template_kwargs.enable_thinking=false</c> patched in,
    ///     so the switch reaches llama-server on the wire. Without the marker the options are returned
    ///     unchanged, so every other request is byte-identical. A pre-existing <see cref="ChatOptions.RawRepresentationFactory" />
    ///     (none is set on the llama.cpp path today) is composed rather than dropped.
    /// </summary>
    internal static ChatOptions? ApplyThinkingSwitch(ChatOptions? options)
    {
        // Gating note: the marker is set upstream (InvocationAgentFactory) whenever reasoning is OFF on a
        // thinking-capable model — i.e. gated on the model's thinking capability, NOT on the finer "template advertises
        // the enable_thinking switch" signal. That is a deliberate, safe SUPERSET: injecting
        // chat_template_kwargs.enable_thinking=false is a no-op for any chat template that does not read that variable
        // (an unknown kwarg is simply ignored by the jinja renderer), and only reasoning models are thinking-capable, so
        // at worst the field is inert. The finer gate would require a new capability threaded through the (cross-lane)
        // classification/resolver chain; if that lands, tighten the factory's marker condition — this site needs no change.
        if (options?.AdditionalProperties is not { } properties
            || !properties.TryGetValue(DisableThinkingMarkerKey, out var raw)
            || raw is not true)
        {
            return options;
        }

        var priorFactory = options.RawRepresentationFactory;
        var patched = options.Clone();
        patched.RawRepresentationFactory = client =>
        {
            var baseOptions = priorFactory?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions();
            // SCME0001: ChatCompletionOptions.Patch (System.ClientModel JsonPatch) is [Experimental] in the pinned OpenAI
            // 2.11 SDK. It is the ONLY seam that serializes an arbitrary top-level body field (the OpenAI chat schema has
            // no typed chat_template_kwargs), and MEAI's OpenAI adapter serializes the ChatCompletionOptions this factory
            // returns, Patch included — so the switch reaches llama-server. Suppress is scoped to this one call, mirroring
            // the MAAI001 pattern (docs/agent-knowledge.md §4).
#pragma warning disable SCME0001
            baseOptions.Patch.Set("$.chat_template_kwargs"u8, DisableThinkingKwargs);
#pragma warning restore SCME0001
            return baseOptions;
        };
        return patched;
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
    internal static bool IsServerGone(Exception exception)
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
                // A process killed MID-RESPONSE (force-eject, crash while streaming) does not surface as a connect-time
                // failure: the open body stream terminates as HttpIOException(ResponseEnded) — live-observed as
                // "The response ended prematurely." during a force-eject. Without this arm the ejected-lease translation
                // above never fires and the user sees a generic provider failure instead of the operator-eject terminal.
                case HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded or HttpRequestError.ConnectionError }:
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
