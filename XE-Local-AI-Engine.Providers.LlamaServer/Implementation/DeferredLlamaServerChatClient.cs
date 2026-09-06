namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

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

    // User-safe terminal message when back-to-back measurement spawns keep owning this model's key past the bounded
    // re-ensure. Retryable by nature — the measurement ends on its own.
    private const string ModelProfilingMessage = "The model is being profiled by a benchmark right now; this request was not started. Try again shortly.";

    // How many times a request re-ensures around a profiling spawn before giving up. Profiling holds the per-key
    // single-flight gate through its own teardown, so ONE re-ensure normally suffices: the re-ensure parks on that gate
    // and returns a process of our own. The extra rounds cover back-to-back measurements; the bound is what stops an
    // unbroken benchmark queue from parking an interactive request indefinitely.
    private const int MaxProfilingReEnsures = 3;

    // In-process marker (duplicated from InvocationAgentFactory.LlamaDisableThinkingMarkerKey — AI.Agent does not
    // reference this assembly). When present+true, reasoning is OFF on a thinking-capable model and the outbound
    // llama-server request must carry chat_template_kwargs.enable_thinking=false so a Qwen3-class chat template stops
    // emitting a reasoning block. The Ollama `think:false` set alongside it never reaches llama.cpp — the
    // OpenAI adapter drops unmapped AdditionalProperties — so the switch is injected here instead.
    internal const string DisableThinkingMarkerKey = "xe.llama.disable_thinking";

    // In-process marker (duplicated from ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey — AI.Agent does not
    // reference this assembly). When present, it carries the turn's thinking budget in tokens, which must ride the
    // outbound body as reasoning_budget_tokens: llama-server otherwise lets a reasoning model think until the context
    // window is exhausted, so the turn ends with no final answer at all.
    internal const string ReasoningBudgetMarkerKey = "xe.llama.reasoning_budget_tokens";

    // The raw utf8 JSON object written at $.chat_template_kwargs. The OpenAI chat body has no typed field for it, so it
    // rides the wire through OpenAICompatibleRequestBody — MEAI's OpenAI adapter uses the request body returned by
    // ChatOptions.RawRepresentationFactory as its serialization base, patch included (verified against MEAI 10.7).
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
        options = ApplyToolSchemaCompatibility(ApplySamplingPassthrough(ApplyReasoningBudget(ApplyThinkingSwitch(options))));
        var healed = false;
        var profilingReEnsures = 0;
        while (true)
        {
            var resolved = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);

            // Hold an inference lease for the duration of the request so a graceful operator eject waits for it to
            // finish before teardown. A refused lease is classified: an EVICTING process fails the request up front as
            // operator-ejected (running it leaseless would slip under the eject drain, be killed mid-flight by the
            // teardown, and then self-heal-respawn the just-ejected model — so eject would never stick); only a
            // genuinely absent/exited process proceeds leaseless, relying on the self-heal below.
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
        options = ApplyToolSchemaCompatibility(ApplySamplingPassthrough(ApplyReasoningBudget(ApplyThinkingSwitch(options))));
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
    ///     model), returns a clone of <paramref name="options" /> whose request body carries
    ///     <c>chat_template_kwargs.enable_thinking=false</c>, so the switch reaches llama-server on the wire. Without the
    ///     marker the options are returned unchanged, so every other request is byte-identical. A pre-existing
    ///     <see cref="ChatOptions.RawRepresentationFactory" /> (none is set on the llama.cpp path today) is composed
    ///     rather than dropped — see <see cref="OpenAICompatibleRequestBody.Chain" />.
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

        return OpenAICompatibleRequestBody.Chain(options,
            static body => OpenAICompatibleRequestBody.SetRawField(body, "$.chat_template_kwargs", DisableThinkingKwargs));
    }

    /// <summary>
    ///     When the turn carries the <see cref="ReasoningBudgetMarkerKey" /> marker (an explicit graded reasoning effort
    ///     on a thinking-capable model), returns a clone of <paramref name="options" /> whose request body carries
    ///     <c>reasoning_budget_tokens</c>, so llama-server caps the reasoning phase instead of letting it run until the
    ///     context window is exhausted. Without the marker the options are returned unchanged, so every other request is
    ///     byte-identical. A pre-existing <see cref="ChatOptions.RawRepresentationFactory" /> (the thinking switch above
    ///     sets one) is composed rather than dropped.
    ///     <para>
    ///         Semantics at the pinned build b10201 (<c>tools/server/server-common.cpp</c>
    ///         <c>oaicompat_chat_params_parse</c>): the per-request value overrides the launch-time
    ///         <c>--reasoning-budget</c> default, and on hitting the budget the server injects its budget message before
    ///         the end-of-thinking tag and forces the final-answer phase — a capped answer rather than a truncated one.
    ///         The field is only read for chat templates with explicit think-end tags (the Qwen3/DeepSeek-R1 family),
    ///         and is a silent no-op otherwise — the same acceptable caveat as the <c>enable_thinking</c> switch above.
    ///     </para>
    /// </summary>
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
    ///     <para>
    ///         The graded budgets are fixed token counts (<c>ReasoningOptionsResolver.ResolveReasoningBudgetTokens</c>)
    ///         sized for the 64k windows local runtimes are usually launched with, and neither call site that sets the
    ///         marker knows the window: the orchestration participant path has no context figure at all, and the
    ///         single-agent factory resolves its <c>num_ctx</c> after the marker is written. A model launched with a
    ///         16k window — or a turn carrying an explicit max-output cap — would otherwise be handed a 24576-token
    ///         budget it can never spend and still answer, reproducing the exact "thought until the window ran out,
    ///         returned nothing" failure the budget exists to prevent.
    ///     </para>
    ///     <para>
    ///         Room is the launched window the invocation factory carried onto <c>num_ctx</c> (llama-server's own
    ///         <c>-c</c>, never sent on the wire — see <see cref="ApplySamplingPassthrough" />), narrowed by
    ///         <see cref="ChatOptions.MaxOutputTokens" /> when the turn sets one, since llama-server counts reasoning
    ///         tokens against that cap too. Half leaves at least as many tokens for the final answer as the model may
    ///         spend thinking, and it leaves the common 64k case unchanged (32768 > every graded budget). When neither
    ///         figure is known the budget is left exactly as the marker carried it.
    ///     </para>
    ///     <para>
    ///         KNOWN GAP, deliberately not closed here: this bounds against the WINDOW, not against the room left after
    ///         the prompt. On a long conversation the true generation room is smaller than what this sees, so the cap
    ///         it computes can still exceed it. Closing it needs the round's input-token count, which does not exist at
    ///         this seam — the estimator lives in the AI.Agent assembly this provider does not reference.
    ///         <c>ProviderCallBudgetChatClient.NarrowReasoningBudget</c> therefore narrows the marker against the
    ///         MEASURED input before it reaches here; this clamp is the backstop for the paths that run without an
    ///         ambient budget scope (the eval and preview-workflow runners), where a coarse bound beats none.
    ///     </para>
    /// </summary>
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
    ///     or <see cref="SamplingOptionKeys.MinP" /> / <see cref="SamplingOptionKeys.RepeatPenalty" /> /
    ///     <see cref="SamplingOptionKeys.RepeatLastN" /> on <see cref="ChatOptions.AdditionalProperties" /> — returns a
    ///     clone of <paramref name="options" /> whose request body carries those knobs as top-level fields, so they reach
    ///     llama-server on the wire. With none of them set the options are returned unchanged, so every other request is
    ///     byte-identical. A pre-existing <see cref="ChatOptions.RawRepresentationFactory" /> (the thinking switch above
    ///     sets one) is composed rather than dropped.
    ///     <para>
    ///         Why this must exist: <c>Microsoft.Extensions.AI.OpenAI</c>'s <c>ToOpenAIOptions</c> maps only
    ///         Temperature/TopP/FrequencyPenalty/PresencePenalty/MaxOutputTokens/Seed/StopSequences — <c>TopK</c> has no
    ///         OpenAI counterpart and unrecognised <c>AdditionalProperties</c> are dropped — so without this the
    ///         developer-gated per-send sampling overrides silently did nothing on the default (llama.cpp) runtime while
    ///         appearing to apply.
    ///     </para>
    ///     <para>
    ///         Why the four names are safe: llama-server's OpenAI-compatible handler copies every unrecognised body
    ///         property straight onto its own request params (verified in the pinned build b10201,
    ///         <c>tools/server/server-common.cpp</c> <c>oaicompat_chat_params_parse</c> "Copy remaining properties to
    ///         llama_params"), where <c>tools/server/server-schema.cpp</c> declares <c>top_k</c>, <c>min_p</c>,
    ///         <c>repeat_penalty</c> and <c>repeat_last_n</c> as first-class sampling fields. <c>num_ctx</c> is
    ///         deliberately excluded: llama-server's context window is fixed by the <c>--ctx-size</c> it was launched
    ///         with, so a per-request value is not honoured — that knob stays a client-side history budget.
    ///     </para>
    /// </summary>
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
    ///     <see cref="GrammarSafeSchemaAIFunction" /> advertising the sanitised schema. When every tool is already
    ///     compilable the options are returned unchanged, so every other request stays byte-identical.
    ///     <para>
    ///         Why this must exist: llama-server compiles the whole <c>tools</c> array into one constrained grammar
    ///         before sampling, and an over-large repetition bound makes it reject the request with HTTP 400
    ///         <c>Failed to initialize samplers: failed to parse grammar</c> — so an oversized bound does not degrade
    ///         tool calling, it breaks the turn outright. See <see cref="LlamaGrammarToolSchemaCompatibility" /> for the
    ///         measured limit.
    ///     </para>
    ///     <para>
    ///         Why it is safe: the caller's <see cref="ChatOptions" /> and its tool list are never mutated — the swap
    ///         happens on a clone whose only consumer is the inner OpenAI adapter's <c>tools</c> serialization. The
    ///         function-invocation middleware above this client resolves, approval-gates and executes tools from its own
    ///         (untouched) list, so <c>ApprovalRequiredAIFunction</c> stays the outermost type there, and argument
    ///         validation still runs against the unsanitised schema. Only the llama.cpp path is affected; cloud
    ///         providers never reach this client.
    ///     </para>
    /// </summary>
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
    ///     <para>
    ///         The distinction decides whether this request takes an inference lease at all. A bound endpoint is a
    ///         benchmark's own profiling process, handed to it by <c>RunExclusiveBenchmarkAsync</c> for the duration of
    ///         the measurement: that caller owns the process, so there is nothing to drain and nothing to protect the
    ///         request from — and asking for a lease would be answered <c>ProfilingOwned</c> and refuse the
    ///         measurement's own requests. Re-ensuring instead would be worse still: it parks on the per-key gate the
    ///         benchmark itself is holding.
    ///     </para>
    /// </summary>
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
    ///     budget is spent. The cached adapter is dropped either way: it is bound to the endpoint the measurement
    ///     process now answers on — the port allocator commonly re-uses the one the replaced process just freed — so
    ///     reusing it would send this request INTO the measurement, contaminating it and dying to its teardown. The
    ///     next EnsureInnerAsync parks on the per-key gate profiling holds through teardown and comes back with a
    ///     process of our own.
    /// </summary>
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
