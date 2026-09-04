namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.OpenAICompat;

/// <summary>
///     The turn's model-readiness step: resolves whether the turn pays a real local (llama.cpp) cold-load, warms that
///     model to readiness before the stream-idle watchdog is armed, and reads back the window it actually launched with.
///     Owned by <see cref="InvocationRunner" />, which calls it once per turn and (on the orchestration path) once per
///     participant model; it holds no per-turn state of its own, so a single instance serves every invocation.
/// </summary>
public sealed class LocalRuntimeWarmer(
    ILocalModelProviderResolver providerResolver,
    IActiveCloudChatClientFactory activeCloudFactory,
    IModelTrustResolver modelTrustResolver,
    ILogger<LocalRuntimeWarmer> logger)
{
    private readonly IActiveCloudChatClientFactory _activeCloudFactory = activeCloudFactory ?? throw new ArgumentNullException(nameof(activeCloudFactory));
    private readonly ILogger<LocalRuntimeWarmer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IModelTrustResolver _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));
    private readonly ILocalModelProviderResolver _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));

    /// <summary>
    ///     Model-readiness phase: warms a LOCAL (llama.cpp) model to readiness BEFORE the stream-idle watchdog
    ///     is armed, so a cold big-model load runs in its own size-aware window (owned by the supervisor) instead of
    ///     being killed by the shorter no-first-chunk watchdog. Cloud (Codex/Azure) and Ollama models route elsewhere or
    ///     warm cheaply on first send, so they are a no-op here. Surfaces the phase into the invocation state (so the UI
    ///     can render "loading model…") and records readiness timing/outcome on <see cref="NodeMetrics" />.
    ///     <para>
    ///         INVARIANT: the warm await is decoupled from the model load's lifetime in the supervisor — a caller
    ///         cancellation abandons THIS wait (rethrown so the turn terminates as cancelled) while the load continues in
    ///         the background and the model becomes warm for the next send. A warm FAILURE is swallowed here so the
    ///         streaming send surfaces the real, classified error through its normal path rather than a duplicate here.
    ///         Admission-gated callers receive that captured failure before policy evaluation, because otherwise an
    ///         unknown-context rejection would mask the authoritative provider failure.
    ///     </para>
    /// </summary>
    internal async Task<LocalRuntimePreparationResult> PrepareLocalRuntimeAsync(string resolvedModel,
        IWorkerEventDispatcher dispatcher,
        Guid invocationId,
        InvocationRunner.StreamState stream,
        long turnStartedTimestamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(stream);

        // An external OpenAI-compatible model is never warmed — the node does not own the process — but it still has a
        // context window, and this branch has to run BEFORE the skip-return below or that window can never reach the
        // turn budgeter. The skip path returns EffectiveContextTokens: null before GetRuntimeInfoAsync is ever called,
        // and a null there means TurnPolicy falls back to its conservative 8192-token default. Implementing only the
        // provider's runtime-info method would therefore have been cosmetic: nothing would have called it.
        if (ExternalModelId.HasExternalScheme(resolvedModel))
        {
            stream.ProviderTag = "remote";
            stream.ModelReadyTimestamp = turnStartedTimestamp;
            var declaredContextTokens = await ResolveDeclaredExternalContextAsync(resolvedModel, invocationId, cancellationToken).ConfigureAwait(false);
            return new LocalRuntimePreparationResult(declaredContextTokens, ExternalProviderConstants.ProviderName, WarmFailure: null);
        }

        var provider = await ResolveWarmableProviderAsync(resolvedModel, invocationId, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            // No local cold-load for this turn (cloud, Ollama, or an unresolved provider): the TTFT baseline is turn
            // start and the provider dimension is "remote" — the first-token latency is the provider's own, not a
            // measurable local warm. The send-to-load-start histogram is deliberately local-only, so it is not recorded.
            stream.ProviderTag = "remote";
            stream.ModelReadyTimestamp = turnStartedTimestamp;
            return new LocalRuntimePreparationResult(EffectiveContextTokens: null, ProviderName: null, WarmFailure: null);
        }

        // This turn pays a real local cold-load. Record how long the turn waited before the load even began
        // (the audited silent pre-spawn gap), tag the provider dimension "local", and measure TTFT from readiness.
        stream.ProviderTag = "local";
        NodeMetrics.TurnToModelLoadStartMs.Record(Stopwatch.GetElapsedTime(turnStartedTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("provider", "local"));

        // Phase: preparing runtime → loading model. Both fire BEFORE the stream-idle watchdog is armed.
        await dispatcher.ReportInvocationPhaseAsync(invocationId, InvocationRuntimePhase.PreparingRuntime).ConfigureAwait(false);
        await dispatcher.ReportInvocationPhaseAsync(invocationId, InvocationRuntimePhase.LoadingModel).ConfigureAwait(false);
        _logger.LogInformation("Warming local model for invocation {InvocationId} before streaming (readiness decoupled from the stream-idle watchdog).", invocationId);

        using var readinessActivity = NodeActivitySource.Source.StartActivity("chat.invocation.model_readiness");
        var startedUtc = DateTimeOffset.UtcNow;
        try
        {
            await provider.WarmModelAsync(resolvedModel, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled its WAIT (the detached load continues in the supervisor and warms the model for the
            // next send). Record the abandoned readiness and rethrow so the turn terminates as cancelled.
            stream.AddModelReadiness(RecordReadiness(startedUtc, "cancelled"));
            readinessActivity?.SetTag("outcome", "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            // Readiness failed (e.g. model incompatible / OOM). Record and capture it. The normal path still lets the
            // streaming send surface the real classified failure through its provider boundary; an admission-gated path
            // rethrows this captured failure before policy evaluation so a null-context refusal cannot mask it.
            stream.AddModelReadiness(RecordReadiness(startedUtc, "failed"));
            readinessActivity?.SetTag("outcome", "failed");
            _logger.LogWarning(exception, "Model warm failed for invocation {InvocationId}; the streaming send will surface the classified failure.", invocationId);
            return new LocalRuntimePreparationResult(EffectiveContextTokens: null,
                provider.ProviderName,
                ExceptionDispatchInfo.Capture(exception));
        }

        var durationMs = RecordReadiness(startedUtc, "ready");
        stream.AddModelReadiness(durationMs);
        readinessActivity?.SetTag("outcome", "ready");

        // The model is ready: measure TTFT from HERE (the first emitted chunk records against this baseline).
        stream.ModelReadyTimestamp = Stopwatch.GetTimestamp();
        _logger.LogInformation("Local model ready for invocation {InvocationId} after {ElapsedMs:F0} ms; arming the stream-idle watchdog for generation.", invocationId, durationMs);

        // Phase: generating (the model is ready; streaming begins under the stream-idle watchdog).
        await dispatcher.ReportInvocationPhaseAsync(invocationId, InvocationRuntimePhase.Generating).ConfigureAwait(false);

        // With the model now ready, read the effective per-slot context window it actually loaded so the turn's
        // budgeters + the num_ctx side channel size against the REAL window (llama.cpp's -c) rather than the app default.
        // Best-effort — a null here just keeps the configured default. A cancellation propagates (the turn is terminating).
        var effectiveContextTokens = await ResolveEffectiveContextTokensAsync(provider, resolvedModel, invocationId, cancellationToken).ConfigureAwait(false);
        return new LocalRuntimePreparationResult(effectiveContextTokens, provider.ProviderName, WarmFailure: null);
    }

    public static string BuildGenerationAdmissionRejectionMessage(string? reasonCode,
        InvocationGenerationAdmissionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return reasonCode switch
        {
            InvocationGenerationAdmissionReasonCodes.EffectiveContextUnavailable => "Effective context unavailable.",
            InvocationGenerationAdmissionReasonCodes.EffectiveContextInsufficient when context.EffectiveContextTokens is { } effectiveContext =>
                $"Requested context {context.RequestedContextTokens} tokens exceeds effective context {effectiveContext} tokens.",
            _ => "Invocation generation was rejected by policy."
        };
    }

    /// <summary>
    ///     Reads the launched effective context window for <paramref name="resolvedModel" /> from the warm local provider
    ///     (llama.cpp). Returns <see langword="null" /> when unknown (the runtime does not report one, or the read failed)
    ///     so the caller keeps the configured default. A cancellation propagates.
    /// </summary>
    public async Task<int?> ResolveEffectiveContextTokensAsync(ILocalModelProvider provider,
        string resolvedModel,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        try
        {
            var runtimeInfo = await provider.GetRuntimeInfoAsync(resolvedModel, cancellationToken).ConfigureAwait(false);
            return runtimeInfo is { EffectiveContextTokens: > 0 } info ? info.EffectiveContextTokens : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Reading the effective context window for invocation {InvocationId} failed; the configured default window is used.", invocationId);
            return null;
        }
    }

    /// <summary>
    ///     The context window an external model's operator DECLARED, or <see langword="null" /> when they declared none
    ///     (the budgeter then keeps its conservative fallback rather than assuming a window the server may not have).
    ///     A cancellation propagates; every other failure degrades to the fallback.
    /// </summary>
    private async Task<int?> ResolveDeclaredExternalContextAsync(string resolvedModel, Guid invocationId, CancellationToken cancellationToken)
    {
        try
        {
            var registration = await _modelTrustResolver.TryResolveExternalAsync(resolvedModel, cancellationToken).ConfigureAwait(false);
            return registration?.Model.ContextLength is > 0 ? registration.Model.ContextLength : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Reading the declared context window for invocation {InvocationId} failed; the configured default window is used.", invocationId);
            return null;
        }
    }

    /// <summary>
    ///     Resolves the local provider that serves <paramref name="resolvedModel" />, returning it only when it is the
    ///     llama.cpp runtime (the one that pays a real cold-load before a watched stream). Any other provider (Ollama /
    ///     cloud / external) or a resolution failure returns <see langword="null" /> so the warm phase is skipped; a
    ///     genuine cancellation propagates.
    /// </summary>
    public async Task<ILocalModelProvider?> ResolveWarmableProviderAsync(string resolvedModel, Guid invocationId, CancellationToken cancellationToken)
    {
        // A cloud-routed model (Codex/Azure) must never trigger a local warm. The provider resolver maps any UNMAPPED
        // model name to the default local provider (llamacpp), so a cloud model id like "gpt-5.6-terra" would otherwise
        // resolve to llama-server and fail its cold-load with "model not installed". This is the SAME per-request routing
        // decision RuntimeChatClient makes for the send, so warm and send stay consistent (see IsCloudProviderSelected).
        if (_activeCloudFactory.IsCloudProviderSelected(resolvedModel))
        {
            return null;
        }

        // An external model is served by a process the node does not own, so there is nothing to warm. Checked here as
        // well as at the caller so any other entry point into this method inherits the skip.
        if (ExternalModelId.HasExternalScheme(resolvedModel))
        {
            return null;
        }

        try
        {
            var provider = await _providerResolver.ResolveProviderForModelAsync(resolvedModel, cancellationToken).ConfigureAwait(false);
            return provider is not null && string.Equals(provider.ProviderName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase)
                ? provider
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Skipping runtime warm for invocation {InvocationId}: could not resolve a local provider for the model.", invocationId);
            return null;
        }
    }

    /// <summary>Records the model-readiness duration + outcome on <see cref="NodeMetrics" /> and returns the elapsed milliseconds.</summary>
    private static double RecordReadiness(DateTimeOffset startedUtc, string outcome)
    {
        var durationMs = (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds;
        NodeMetrics.ModelReadinessDurationMs.Record(durationMs, new KeyValuePair<string, object?>("outcome", outcome));
        NodeMetrics.ModelReadinessTotal.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        return durationMs;
    }
}

/// <summary>
///     The outcome of <see cref="LocalRuntimeWarmer.PrepareLocalRuntimeAsync" />: the effective context window the model
///     actually launched with (null when there was no local warm or the window is unknown), the warmed provider's name,
///     and — when readiness FAILED — the captured provider failure an admission-gated caller must rethrow before its
///     policy runs.
/// </summary>
internal readonly record struct LocalRuntimePreparationResult(
    int? EffectiveContextTokens,
    string? ProviderName,
    ExceptionDispatchInfo? WarmFailure);
