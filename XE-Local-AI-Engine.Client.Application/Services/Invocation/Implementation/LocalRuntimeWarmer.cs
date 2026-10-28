namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.OpenAICompat;

/// <summary>
///     The turn's model-readiness step: resolves whether the turn pays a real local (llama.cpp) cold-load, warms that
///     model before the stream-idle watchdog is armed, and reads back the window it actually launched with.
/// </summary>
/// <remarks>
///     Owned by <see cref="InvocationRunner" />, which calls it once per turn and, on the orchestration path, once per
///     participant model. It holds no per-turn state, so a single instance serves every invocation.
/// </remarks>
public sealed class LocalRuntimeWarmer
{
    private readonly IGpuWorkGate _gpuWorkGate;
    private readonly IActiveCloudChatClientFactory _activeCloudFactory;
    private readonly ICloudModelResolver _cloudModelResolver;
    private readonly ILogger<LocalRuntimeWarmer> _logger;
    private readonly IModelTrustResolver _modelTrustResolver;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly TimeProvider _timeProvider;
    private readonly LocalModelPerfLog? _perfLog;

    /// <param name="perfLog">Why a model is unloaded and how long its last load took, for the loading phase's detail; null reports none.</param>
    public LocalRuntimeWarmer(ILocalModelProviderResolver providerResolver,
        IActiveCloudChatClientFactory activeCloudFactory,
        IModelTrustResolver modelTrustResolver,
        ICloudModelResolver cloudModelResolver,
        ILogger<LocalRuntimeWarmer> logger,
        TimeProvider timeProvider,
        IGpuWorkGate gpuWorkGate,
        LocalModelPerfLog? perfLog = null)
    {
        _perfLog = perfLog;
        ArgumentNullException.ThrowIfNull(gpuWorkGate);
        _gpuWorkGate = gpuWorkGate;
        ArgumentNullException.ThrowIfNull(activeCloudFactory);
        ArgumentNullException.ThrowIfNull(cloudModelResolver);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(modelTrustResolver);
        ArgumentNullException.ThrowIfNull(providerResolver);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _activeCloudFactory = activeCloudFactory;
        _cloudModelResolver = cloudModelResolver;
        _logger = logger;
        _modelTrustResolver = modelTrustResolver;
        _providerResolver = providerResolver;
        _timeProvider = timeProvider;
    }

    /// <summary>
    ///     Warms a LOCAL (llama.cpp) model BEFORE the stream-idle watchdog is armed, so a cold big-model load runs in
    ///     the supervisor's own size-aware window instead of dying to the shorter no-first-chunk watchdog.
    /// </summary>
    /// <remarks>
    ///     Cloud and Ollama models route elsewhere or warm on first send, so they are a no-op. It surfaces the phase
    ///     into the invocation state and records readiness on <see cref="NodeMetrics" />. INVARIANT: this wait is
    ///     decoupled from the load's lifetime — a cancellation abandons the WAIT and rethrows while the load keeps
    ///     going, leaving the model warm for the next send. A warm FAILURE is swallowed so the streaming send surfaces
    ///     the classified error, but an admission-gated caller is handed it first, or a null-context refusal masks it.
    /// </remarks>
    internal async Task<LocalRuntimePreparationResult> PrepareLocalRuntimeAsync(string resolvedModel,
        IWorkerEventDispatcher dispatcher,
        Guid invocationId,
        InvocationRunner.StreamState stream,
        long turnStartedTimestamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(stream);

        // An external OpenAI-compatible model is never warmed (the node does not own the process) but still HAS a
        // window, so this branch must run BEFORE the skip-return or TurnPolicy falls back to its conservative default.
        if (ExternalModelId.HasExternalScheme(resolvedModel))
        {
            stream.ProviderTag = "remote";
            stream.ModelReadyTimestamp = turnStartedTimestamp;
            var declaredContextTokens = await ResolveDeclaredRemoteContextAsync(resolvedModel, invocationId, cancellationToken);
            return new LocalRuntimePreparationResult(declaredContextTokens, ExternalProviderConstants.ProviderName, WarmFailure: null);
        }

        var provider = await ResolveWarmableProviderAsync(resolvedModel, invocationId, cancellationToken);
        if (provider is null)
        {
            // No local cold-load (cloud, Ollama or an unresolved provider): the TTFT baseline is turn start and the
            // latency is the provider's own; the load-start histogram is local-only. A cloud model's declared window replaces the default.
            stream.ProviderTag = "remote";
            stream.ModelReadyTimestamp = turnStartedTimestamp;
            var cloudContextTokens = await ResolveDeclaredRemoteContextAsync(resolvedModel, invocationId, cancellationToken);
            return new LocalRuntimePreparationResult(cloudContextTokens, ProviderName: null, WarmFailure: null);
        }

        // This turn pays a real local cold-load. Record how long the turn waited before the load even began
        // (the audited silent pre-spawn gap), tag the provider dimension "local", and measure TTFT from readiness.
        stream.ProviderTag = "local";
        NodeMetrics.TurnToModelLoadStartMs.Record(Stopwatch.GetElapsedTime(turnStartedTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("provider", "local"));

        // Phase: preparing runtime → loading model. Both fire BEFORE the stream-idle watchdog is armed.
        await dispatcher.ReportInvocationPhaseAsync(invocationId, InvocationRuntimePhase.PreparingRuntime);
        await dispatcher.ReportInvocationPhaseAsync(invocationId, InvocationRuntimePhase.LoadingModel, LoadingDetail(resolvedModel));
        if (GpuWorkHolderNotice.ForModelLoad(_gpuWorkGate, invocationId) is { } gpuWorkNotice)
        {
            // The load waits for the exclusive job; without this the loading state carries no reason.
            await dispatcher.ReportTurnNoticeAsync(gpuWorkNotice);
        }

        _logger.LogInformation("Warming local model for invocation {InvocationId} before streaming (readiness decoupled from the stream-idle watchdog).", invocationId);

        using var readinessActivity = NodeActivitySource.Source.StartActivity("chat.invocation.model_readiness");
        var startedUtc = _timeProvider.GetUtcNow();
        try
        {
            await provider.WarmModelAsync(resolvedModel, cancellationToken);
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
            // Readiness failed (incompatible model, OOM): record and CAPTURE it. The streaming send still surfaces the
            // classified failure, while an admission-gated path rethrows this one before its null-context refusal.
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
        await dispatcher.ReportInvocationPhaseAsync(invocationId, InvocationRuntimePhase.Generating);

        // Read the effective per-slot window the model actually loaded, so the budgeters and the num_ctx side channel
        // size against llama.cpp's real -c. Best-effort: a null keeps the default, a cancellation propagates.
        var effectiveContextTokens = await ResolveEffectiveContextTokensAsync(provider, resolvedModel, invocationId, cancellationToken);
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
            var runtimeInfo = await provider.GetRuntimeInfoAsync(resolvedModel, cancellationToken);
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
    ///     The DECLARED context window of a model the node does not launch: an external (<c>ext:</c>) model's
    ///     registered length, else a selected cloud (Codex/Azure) model's catalog window, else <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     Makes no provider call, so it never triggers a load. A cancellation propagates; every other failure degrades
    ///     to <see langword="null" />, which keeps the caller's default window.
    /// </remarks>
    public async Task<int?> ResolveDeclaredRemoteContextAsync(string resolvedModel, Guid correlationId, CancellationToken cancellationToken)
    {
        if (ExternalModelId.HasExternalScheme(resolvedModel))
        {
            return await ResolveDeclaredExternalContextAsync(resolvedModel, correlationId, cancellationToken);
        }

        if (!_activeCloudFactory.IsCloudProviderSelected(resolvedModel))
        {
            return null;
        }

        try
        {
            return (await _cloudModelResolver.ResolveContextWindowAsync(resolvedModel, cancellationToken))?.ContextTokens;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Reading the declared cloud context window for {CorrelationId} failed; the configured default window is used.", correlationId);
            return null;
        }
    }

    /// <summary>
    ///     The context window an external model's operator DECLARED, or <see langword="null" /> when they declared
    ///     none.
    /// </summary>
    /// <remarks>
    ///     A null keeps the budgeter's conservative fallback rather than assuming a window the server may not have. A
    ///     cancellation propagates; every other failure degrades to the fallback.
    /// </remarks>
    private async Task<int?> ResolveDeclaredExternalContextAsync(string resolvedModel, Guid invocationId, CancellationToken cancellationToken)
    {
        try
        {
            var registration = await _modelTrustResolver.TryResolveExternalAsync(resolvedModel, cancellationToken);
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
    ///     Resolves the local provider serving <paramref name="resolvedModel" />, returning it only when it is the
    ///     llama.cpp runtime — the one that pays a real cold-load before a watched stream.
    /// </summary>
    /// <remarks>
    ///     Any other provider (Ollama, cloud, external) or a resolution failure answers <see langword="null" /> so the
    ///     warm phase is skipped; a genuine cancellation propagates.
    /// </remarks>
    public async Task<ILocalModelProvider?> ResolveWarmableProviderAsync(string resolvedModel, Guid invocationId, CancellationToken cancellationToken)
    {
        // A cloud-routed model must never warm locally: the resolver maps any UNMAPPED name to llamacpp, so a cloud id
        // would cold-load and fail. Same per-request decision RuntimeChatClient makes, so warm and send stay in step.
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
            var provider = await _providerResolver.ResolveProviderForModelAsync(resolvedModel, cancellationToken);
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

    /// <summary>
    ///     Why this load happens and how long the model's previous load took, from <see cref="LocalModelPerfLog" />; null when
    ///     no log is wired.
    /// </summary>
    /// <remarks>
    ///     No history means this process never loaded the model. A history whose model is still loaded, or was lost in a
    ///     way the supervisor did not cause, has no reason: the warm then returns at once or the cause is unknown.
    /// </remarks>
    internal InvocationRuntimePhaseDetail? LoadingDetail(string resolvedModel)
    {
        if (_perfLog is null)
        {
            return null;
        }

        if (_perfLog.TryGetLoadHistory(resolvedModel, ModelRole.Chat) is not { } history)
        {
            return new InvocationRuntimePhaseDetail
            {
                Reason = InvocationRuntimePhaseReasons.FirstLoad
            };
        }

        var reason = history.LastUnloadReason switch
        {
            LlamaServerUnloadReason.IdleTimeout => InvocationRuntimePhaseReasons.IdleUnloaded,
            LlamaServerUnloadReason.LoadedModelCap => InvocationRuntimePhaseReasons.EvictedForCap,
            LlamaServerUnloadReason.MemoryForAnotherModel => InvocationRuntimePhaseReasons.EvictedForMemory,
            _ => null
        };
        return new InvocationRuntimePhaseDetail
        {
            Reason = reason,
            LastLoadMs = history.LastReadyLoadMs
        };
    }

    /// <summary>Records the model-readiness duration + outcome on <see cref="NodeMetrics" /> and returns the elapsed milliseconds.</summary>
    private double RecordReadiness(DateTimeOffset startedUtc, string outcome)
    {
        var durationMs = (_timeProvider.GetUtcNow() - startedUtc).TotalMilliseconds;
        NodeMetrics.ModelReadinessDurationMs.Record(durationMs, new KeyValuePair<string, object?>("outcome", outcome));
        NodeMetrics.ModelReadinessTotal.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        return durationMs;
    }
}
