namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
// Aliased rather than a blanket `using OpenAI.Chat` so the wire type never collides with the MEAI ChatResponseFormat every IChatClient
// signature in this file speaks — the same discipline OpenAICompatibleRequestBody applies to ChatCompletionOptions.
using OpenAIChatResponseFormat = OpenAI.Chat.ChatResponseFormat;

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
///     inner adapter it constructs and disposes it on <see cref="Dispose" />. A request whose endpoint is gone
///     self-heals ONCE before any output has streamed. Self-heal, leases, refusals and the outbound body patches:
///     docs/wiki/03-local-runtime-and-providers.md, "Deferred chat / embedding clients".
/// </remarks>
internal sealed class DeferredLlamaServerChatClient : IChatClient
{
    // User-safe terminal message when an operator force-ejected this model mid-request. Carries no paths/ports/internals.
    private const string ModelEjectedMessage = "The model was ejected by the operator while this request was running.";

    // User-safe terminal message when a request arrives while a graceful operator eject is draining this model: the
    // request is refused up front (never started) instead of running untracked under the drain.
    private const string ModelEjectingMessage = "The model is being ejected by the operator; this request was not started.";

    // User-safe terminal message when back-to-back measurement spawns keep owning this model's key past the bounded
    // re-ensure. Retryable by nature — the measurement ends on its own.
    private const string ModelProfilingMessage = "The model is being profiled by a benchmark right now; this request was not started. Try again shortly.";

    // How many times a request re-ensures around a profiling spawn before giving up. ONE normally suffices; the extra rounds cover back-to-back measurements, and
    // the bound is what stops an unbroken benchmark queue from parking an interactive request indefinitely.
    private const int MaxProfilingReEnsures = 3;

    // In-process marker, duplicated from InvocationAgentFactory.LlamaDisableThinkingMarkerKey because AI.Agent does not reference this assembly. Present and true means
    // reasoning is OFF on a thinking-capable model, so the outbound request must carry the enable-thinking switch. See wiki 03, section Deferred chat / embedding clients.
    internal const string DisableThinkingMarkerKey = "xe.llama.disable_thinking";

    // In-process marker, duplicated from ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey because AI.Agent does not reference this assembly. It carries the
    // turn's thinking budget in tokens, which must ride the outbound body: llama-server otherwise thinks until the window is exhausted and the turn has no answer.
    internal const string ReasoningBudgetMarkerKey = "xe.llama.reasoning_budget_tokens";

    // The OpenAI schema wrapper requires a name and llama-server ignores it, so an unnamed MEAI response format needs
    // any valid one rather than a meaningful one.
    private const string DefaultResponseSchemaName = "response";

    // The gen_ai provider name the inner adapter reports once built: MEAI's OpenAI chat-completions adapter hard-codes a ChatClientMetadata of provider name openai in
    // Microsoft.Extensions.AI.OpenAI 10.9.0. Pinned so pre-init and post-init metadata match; drift shows up as a failing test that compares the two directly.
    private const string InnerProviderName = "openai";

    // The raw utf8 JSON object written at the chat-template-kwargs path. The OpenAI chat body has no typed field for it, so it rides the wire through
    // OpenAICompatibleRequestBody: MEAI's adapter uses the body returned by ChatOptions.RawRepresentationFactory as its serialization base, patch included, at MEAI 10.7.
    private static ReadOnlySpan<byte> DisableThinkingKwargs => "{\"enable_thinking\":false}"u8;

    private readonly SemaphoreSlim _initGate = new(initialCount: 1, maxCount: 1);
    private readonly string _modelName;
    private readonly TimeSpan _networkTimeout;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly ITokenEstimatorCalibrationScheduler _calibrationScheduler;
    private readonly ILlamaServerEndpointBinding? _endpointBinding;

    private IChatClient? _inner;
    private Uri? _innerEndpoint;

    public DeferredLlamaServerChatClient(ILlamaServerProcessSupervisor supervisor,
        string modelName,
        TimeSpan networkTimeout,
        ITokenEstimatorCalibrationScheduler? calibrationScheduler = null,
        ILlamaServerEndpointBinding? endpointBinding = null)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _modelName = modelName;
        _networkTimeout = networkTimeout;
        _calibrationScheduler = calibrationScheduler ?? new NullTokenEstimatorCalibrationScheduler();
        _endpointBinding = endpointBinding;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options = ApplyToolSchemaCompatibility(ApplyResponseSchemaPassthrough(ApplySamplingPassthrough(ApplyReasoningBudget(ApplyThinkingSwitch(options)))));
        var healed = false;
        var profilingReEnsures = 0;
        while (true)
        {
            var resolved = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);

            // Hold an inference lease for the duration of the request so a graceful operator eject waits for it to finish before teardown. A refused lease is
            // CLASSIFIED, never ignored; only a genuinely absent or exited process proceeds leaseless. See wiki 03, "Deferred chat / embedding clients".
            ILlamaServerInferenceLease? lease = null;
            if (!resolved.Bound)
            {
                var acquisition = _supervisor.TryAcquireInferenceLease(_modelName, ModelRole.Chat);
                if (acquisition.ProcessEvicting)
                {
                    _calibrationScheduler.Invalidate(_modelName);
                    throw new LlamaServerModelEjectedException(ModelEjectingMessage);
                }

                if (acquisition.ProcessProfiling)
                {
                    if (!TryBeginProfilingReEnsure(ref profilingReEnsures))
                    {
                        throw new LlamaRuntimeException(ModelProfilingMessage);
                    }

                    continue;
                }

                lease = acquisition.Lease;
            }

            // Never from a bound endpoint: that is the benchmark's transient profiling port, and seeding the
            // calibration target with it queues a probe against a process that is about to be torn down.
            if (!resolved.Bound)
            {
                _calibrationScheduler.Schedule(_modelName, resolved.BaseAddress);
            }

            try
            {
                return await resolved.Client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsServerGone(ex))
            {
                // Operator FORCE-eject takes priority: fail as operator-ejected (never retried) so the terminal state is
                // truthful, not a generic provider drop.
                if (lease is { WasEjected: true })
                {
                    _calibrationScheduler.Invalidate(_modelName);
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
        options = ApplyToolSchemaCompatibility(ApplyResponseSchemaPassthrough(ApplySamplingPassthrough(ApplyReasoningBudget(ApplyThinkingSwitch(options)))));
        var healed = false;
        var profilingReEnsures = 0;
        while (true)
        {
            var resolved = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);

            // Same refusal classification as the non-streaming path: an eject-in-progress fails the request before the
            // stream opens; only an absent/exited process streams leaseless and relies on the pre-first-chunk self-heal.
            ILlamaServerInferenceLease? lease = null;
            if (!resolved.Bound)
            {
                var acquisition = _supervisor.TryAcquireInferenceLease(_modelName, ModelRole.Chat);
                if (acquisition.ProcessEvicting)
                {
                    _calibrationScheduler.Invalidate(_modelName);
                    throw new LlamaServerModelEjectedException(ModelEjectingMessage);
                }

                if (acquisition.ProcessProfiling)
                {
                    if (!TryBeginProfilingReEnsure(ref profilingReEnsures))
                    {
                        throw new LlamaRuntimeException(ModelProfilingMessage);
                    }

                    continue;
                }

                lease = acquisition.Lease;
            }

            // Never from a bound endpoint: that is the benchmark's transient profiling port, and seeding the
            // calibration target with it queues a probe against a process that is about to be torn down.
            if (!resolved.Bound)
            {
                _calibrationScheduler.Schedule(_modelName, resolved.BaseAddress);
            }

            var enumerator =
                resolved.Client.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
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
                            _calibrationScheduler.Invalidate(_modelName);
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

        var inner = Volatile.Read(ref _inner);
        if (inner is not null)
        {
            // Once built, the adapter's own metadata wins: it carries the endpoint actually resolved.
            return inner.GetService(serviceType, serviceKey);
        }

        // Before first use there is no inner adapter, but MEAI's OpenTelemetryChatClient snapshots ChatClientMetadata in ITS constructor and never refreshes it, so a
        // null here permanently costs every span over this client its model and provider name. Answer from construction-time knowledge — wiki 03, pre-init metadata.
        if (serviceType == typeof(ChatClientMetadata) && serviceKey is null)
        {
            return new ChatClientMetadata(InnerProviderName,
                _endpointBinding?.GetBoundEndpoint(_modelName, ModelRole.Chat)?.BaseAddress,
                _modelName);
        }

        return null;
    }

    public void Dispose()
    {
        _inner?.Dispose();
        _initGate.Dispose();
    }

    /// <summary>
    ///     When the turn carries the <see cref="DisableThinkingMarkerKey" /> marker, returns a clone of
    ///     <paramref name="options" /> whose request body carries the enable-thinking switch off.
    /// </summary>
    /// <remarks>
    ///     Without the marker the options are returned unchanged, so every other request is byte-identical, and a
    ///     pre-existing <see cref="ChatOptions.RawRepresentationFactory" /> is composed rather than dropped (see
    ///     <see cref="OpenAICompatibleRequestBody.Chain" />). Why the switch must ride the body, and why the marker's
    ///     gate is a deliberate superset: wiki 03, "Deferred chat / embedding clients".
    /// </remarks>
    internal static ChatOptions? ApplyThinkingSwitch(ChatOptions? options)
    {
        // Gating: the marker is set upstream by InvocationAgentFactory and by ConversationSummarizer.FoldAsync, both resting on the same safety argument — a
        // deliberate SUPERSET gate that stays inert on a template ignoring the kwarg. Tighten BOTH producers, never this site; the argument is in wiki 03.
        if (options?.AdditionalProperties is not { } properties
            || !properties.TryGetValue(DisableThinkingMarkerKey, out var raw)
            || raw is not true)
        {
            return options;
        }

        return OpenAICompatibleRequestBody.Chain(options,
            static body => OpenAICompatibleRequestBody.SetRawField(body, "$.chat_template_kwargs", DisableThinkingKwargs));
    }

    /// <summary>
    ///     When the turn carries the <see cref="ReasoningBudgetMarkerKey" /> marker, returns a clone of
    ///     <paramref name="options" /> whose request body carries the reasoning budget, so llama-server caps the
    ///     reasoning phase instead of letting it run until the context window is exhausted.
    /// </summary>
    /// <remarks>
    ///     Without the marker the options are returned unchanged, so every other request is byte-identical, and a
    ///     pre-existing <see cref="ChatOptions.RawRepresentationFactory" /> (the thinking switch above sets one) is
    ///     composed rather than dropped. Semantics verified against the pinned build b10201, and the templates that
    ///     read the field at all: wiki 03, "Deferred chat / embedding clients".
    /// </remarks>
    internal static ChatOptions? ApplyReasoningBudget(ChatOptions? options)
    {
        if (TryReadInt32(options?.AdditionalProperties, ReasoningBudgetMarkerKey) is not { } markerTokens || markerTokens <= 0)
        {
            return options;
        }

        var budgetTokens = ClampToGenerationRoom(markerTokens, options!);
        return OpenAICompatibleRequestBody.Chain(options!,
            body => OpenAICompatibleRequestBody.SetField(body, "$.reasoning_budget_tokens", budgetTokens));
    }

    /// <summary>
    ///     Caps the marker's budget at HALF the room this turn can actually generate into, so the reasoning phase can
    ///     never consume everything the model had to answer with.
    /// </summary>
    /// <remarks>
    ///     Room is the launched window carried onto <c>num_ctx</c> (llama-server's own context flag, never sent on the
    ///     wire — see <see cref="ApplySamplingPassthrough" />), narrowed by <see cref="ChatOptions.MaxOutputTokens" />
    ///     when the turn sets one; with neither figure known the marker's budget stands. KNOWN GAP, deliberately not
    ///     closed here: this bounds against the WINDOW, not the room left after the prompt, so
    ///     <c>ProviderCallBudgetChatClient.NarrowReasoningBudget</c> narrows first and this is the backstop. See wiki 03.
    /// </remarks>
    private static int ClampToGenerationRoom(int budgetTokens, ChatOptions options)
    {
        int? room = null;
        if (TryReadInt32(options.AdditionalProperties, SamplingOptionKeys.NumCtx) is { } window && window > 0)
        {
            room = window;
        }

        if (options.MaxOutputTokens is { } maxOutput && maxOutput > 0)
        {
            room = room is { } known ? Math.Min(known, maxOutput) : maxOutput;
        }

        return room is { } resolved ? Math.Min(budgetTokens, Math.Max(resolved / 2, 1)) : budgetTokens;
    }

    /// <summary>
    ///     When the turn sets any sampling knob the MEAI OpenAI adapter does not map — <see cref="ChatOptions.TopK" />,
    ///     or <see cref="SamplingOptionKeys.MinP" />, <see cref="SamplingOptionKeys.RepeatPenalty" /> and
    ///     <see cref="SamplingOptionKeys.RepeatLastN" /> — returns a clone carrying them as top-level body fields.
    /// </summary>
    /// <remarks>
    ///     With none of them set the options are returned unchanged, so every other request is byte-identical, and a
    ///     pre-existing <see cref="ChatOptions.RawRepresentationFactory" /> is composed rather than dropped.
    ///     <c>num_ctx</c> is deliberately EXCLUDED: llama-server's window is fixed by the context size it launched with,
    ///     so a per-request value is not honoured and that knob stays a client-side history budget. Why the passthrough
    ///     must exist and why the four names are safe (verified on the pinned build b10201): wiki 03.
    /// </remarks>
    internal static ChatOptions? ApplySamplingPassthrough(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        var topK = options.TopK;
        var properties = options.AdditionalProperties;
        var minP = TryReadSingle(properties, SamplingOptionKeys.MinP);
        var repeatPenalty = TryReadSingle(properties, SamplingOptionKeys.RepeatPenalty);
        var repeatLastN = TryReadInt32(properties, SamplingOptionKeys.RepeatLastN);

        if (topK is null && minP is null && repeatPenalty is null && repeatLastN is null)
        {
            return options;
        }

        return OpenAICompatibleRequestBody.Chain(options,
            body =>
            {
                if (topK is { } resolvedTopK)
                {
                    OpenAICompatibleRequestBody.SetField(body, "$.top_k", resolvedTopK);
                }

                if (minP is { } resolvedMinP)
                {
                    OpenAICompatibleRequestBody.SetField(body, "$.min_p", resolvedMinP);
                }

                if (repeatPenalty is { } resolvedRepeatPenalty)
                {
                    OpenAICompatibleRequestBody.SetField(body, "$.repeat_penalty", resolvedRepeatPenalty);
                }

                if (repeatLastN is { } resolvedRepeatLastN)
                {
                    OpenAICompatibleRequestBody.SetField(body, "$.repeat_last_n", resolvedRepeatLastN);
                }
            });
    }

    /// <summary>
    ///     When the turn asks for a JSON-schema response format, returns a clone of <paramref name="options" /> whose
    ///     request body carries the AUTHOR'S schema at the json-schema path llama-server reads, sanitised only where
    ///     llama.cpp's GBNF converter could not compile it.
    /// </summary>
    /// <remarks>
    ///     Without a schema the options are returned unchanged, so every other request is byte-identical, and a
    ///     pre-existing <see cref="ChatOptions.RawRepresentationFactory" /> is composed rather than dropped.
    ///     <see cref="ChatOptions.ResponseFormat" /> is deliberately LEFT in place — the MEAI-level consumers above
    ///     this client read it — and no longer decides what goes on the wire. Why MEAI's strict-schema transform must
    ///     be bypassed, why a body patch suffices, why non-strict, and the bound that still needs sanitising: wiki 03.
    /// </remarks>
    internal static ChatOptions? ApplyResponseSchemaPassthrough(ChatOptions? options)
    {
        if (options?.ResponseFormat is not ChatResponseFormatJson { Schema: { } schema } jsonFormat)
        {
            return options;
        }

        // Serialised once, here, rather than inside the factory: the factory runs per request, and the author's
        // JsonElement must not be captured past the lifetime of whatever document it came from.
        var payload = BinaryData.FromString(LlamaGrammarToolSchemaCompatibility.Sanitize(schema).GetRawText());
        var name = jsonFormat.SchemaName ?? DefaultResponseSchemaName;
        var description = jsonFormat.SchemaDescription;

        return OpenAICompatibleRequestBody.Chain(options,
            body => body.ResponseFormat = OpenAIChatResponseFormat.CreateJsonSchemaFormat(name, payload, description, jsonSchemaIsStrict: false));
    }

    private static float? TryReadSingle(AdditionalPropertiesDictionary? properties, string key)
    {
        return properties is not null && properties.TryGetValue<float>(key, out var value) && !float.IsNaN(value) ? value : null;
    }

    private static int? TryReadInt32(AdditionalPropertiesDictionary? properties, string key)
    {
        return properties is not null && properties.TryGetValue<int>(key, out var value) ? value : null;
    }

    /// <summary>
    ///     When at least one offered tool carries a JSON-schema bound llama.cpp's GBNF converter cannot compile, returns
    ///     a clone of <paramref name="options" /> in which ONLY those tools are replaced by a
    ///     <see cref="GrammarSafeSchemaAIFunction" /> advertising the sanitised schema.
    /// </summary>
    /// <remarks>
    ///     When every tool is already compilable the options are returned unchanged, so every other request stays
    ///     byte-identical. An oversized bound does not degrade tool calling, it breaks the turn outright — see
    ///     <see cref="LlamaGrammarToolSchemaCompatibility" /> for the measured limit. The caller's
    ///     <see cref="ChatOptions" /> and its tool list are never mutated; only the llama.cpp path is affected. Why the
    ///     swap is safe for approval gating and argument validation: wiki 03, "Deferred chat / embedding clients".
    /// </remarks>
    internal static ChatOptions? ApplyToolSchemaCompatibility(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } tools)
        {
            return options;
        }

        List<AITool>? sanitizedTools = null;
        for (var index = 0; index < tools.Count; index++)
        {
            if (tools[index] is not AIFunction function
                || !LlamaGrammarToolSchemaCompatibility.RequiresSanitizing(function.JsonSchema))
            {
                continue;
            }

            sanitizedTools ??= [.. tools];
            sanitizedTools[index] = new GrammarSafeSchemaAIFunction(function, LlamaGrammarToolSchemaCompatibility.Sanitize(function.JsonSchema));
        }

        if (sanitizedTools is null)
        {
            return options;
        }

        var patched = options.Clone();
        patched.Tools = sanitizedTools;
        return patched;
    }

    private async Task<ResolvedChatClient> EnsureInnerAsync(CancellationToken ct)
    {
        // Resolve the CURRENT endpoint for every real request. Besides allowing the supervisor to cheaply confirm the
        // process is live, this is the request-triggered due check for calibration; no timer ever polls a cached target.
        var (endpoint, bound) = await ResolveEndpointCoreAsync(ct).ConfigureAwait(false);

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _inner);
            if (current is not null && _innerEndpoint == endpoint.BaseAddress)
            {
                return new ResolvedChatClient(current, endpoint.BaseAddress, bound);
            }

            if (current is not null)
            {
                _calibrationScheduler.Invalidate(_modelName);
                current.Dispose();
            }

            // The created adapter is transferred into _inner immediately and remains owned by this wrapper; both
            // InvalidateInner and Dispose release it. CA2000 cannot follow that ownership through ResolvedChatClient.
#pragma warning disable CA2000
            var built = LlamaServerOpenAIAdapterFactory.CreateChatClient(endpoint.BaseAddress, _modelName, _networkTimeout);
#pragma warning restore CA2000
            _innerEndpoint = endpoint.BaseAddress;
            Volatile.Write(ref _inner, built);
            return new ResolvedChatClient(built, endpoint.BaseAddress, bound);
        }
        finally
        {
            _initGate.Release();
        }
    }

    internal async Task<LlamaServerEndpoint> ResolveEndpointAsync(CancellationToken ct)
    {
        return (await ResolveEndpointCoreAsync(ct).ConfigureAwait(false)).Endpoint;
    }

    /// <summary>
    ///     The endpoint to talk to, and whether it came from the endpoint BINDING rather than from the supervisor.
    /// </summary>
    /// <remarks>
    ///     The distinction decides whether this request takes an inference lease at all: a bound endpoint is a
    ///     benchmark's own profiling process, handed to it by <c>RunExclusiveBenchmarkAsync</c> for the measurement, so
    ///     it takes none. See wiki 03, "Deferred chat / embedding clients".
    /// </remarks>
    private async Task<(LlamaServerEndpoint Endpoint, bool Bound)> ResolveEndpointCoreAsync(CancellationToken ct)
    {
        if (_endpointBinding?.GetBoundEndpoint(_modelName, ModelRole.Chat) is { } bound)
        {
            return (bound, true);
        }

        return (await _supervisor.EnsureRunningAsync(_modelName, ModelRole.Chat, ct).ConfigureAwait(false), false);
    }

    /// <summary>
    ///     Prepares one more attempt around a profiling spawn that owns this model's key, or reports that the bounded
    ///     budget is spent.
    /// </summary>
    /// <remarks>
    ///     The cached adapter is dropped either way, because reusing it would send this request INTO the measurement,
    ///     contaminating it and dying to its teardown. The next <c>EnsureInnerAsync</c> parks on the per-key gate
    ///     profiling holds through teardown and comes back with a process of our own. See wiki 03.
    /// </remarks>
    private bool TryBeginProfilingReEnsure(ref int attempts)
    {
        // InvalidateInner already invalidates the calibration target.
        InvalidateInner();
        return ++attempts <= MaxProfilingReEnsures;
    }

    // Drops the cached adapter so the next EnsureInnerAsync re-resolves the endpoint and re-spawns the server via the
    // supervisor. Idempotent and safe under concurrency (the loser of the swap simply disposes nothing).
    private void InvalidateInner()
    {
        var stale = Interlocked.Exchange(ref _inner, null);
        _innerEndpoint = null;
        _calibrationScheduler.Invalidate(_modelName);
        stale?.Dispose();
    }

    /// <summary><see cref="Bound" /> marks an endpoint that came from the endpoint binding — see ResolveEndpointCoreAsync.</summary>
    private readonly record struct ResolvedChatClient(IChatClient Client, Uri BaseAddress, bool Bound);

    // True when the exception chain says the target llama-server is unreachable — the process is gone — rather than reporting a model or runtime error. Walks the
    // FULL chain, including the AggregateException fan-out from the OpenAI SDK retry policy: ClientResultException, HttpRequestException, a refused SocketException.
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
                // A process killed MID-RESPONSE does not surface as a connect-time failure: the open body stream terminates as HttpIOException(ResponseEnded),
                // live-observed during a force-eject. Without this arm the ejected-lease translation never fires and the user sees a generic provider failure.
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
