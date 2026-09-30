namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <inheritdoc />
public sealed class BenchmarkPhaseLaunchResolver : IBenchmarkPhaseLaunchResolver
{
    /// <summary>Auto stayed on f16 because the node selected a CPU llama.cpp build.</summary>
    public const string AutoReasonCpuVariant = "cpu-variant";

    /// <summary>Auto stayed on f16 because the selected binary could not be interrogated.</summary>
    public const string AutoReasonProbeUnavailable = "probe-unavailable";

    /// <summary>Auto stayed on f16 because the selected binary does not advertise the quantized vector.</summary>
    public const string AutoReasonManifestUnsupported = "manifest-unsupported";

    /// <summary>Auto stayed on f16 because the optimized config was previously recorded as unable to start here.</summary>
    public const string AutoReasonFallbackDisabled = "fallback-disabled";

    private readonly IInferenceProfileResolver _inferenceProfiles;
    private readonly IGpuVariantSelector _variantSelector;
    private readonly ILlamaServerLaunchCapabilityInspector _launchCapabilities;
    private readonly ILlamaServerLaunchFallbackStore _launchFallbackStore;
    private readonly ILlamaServerLaunchPolicy _launchPolicy;
    private readonly LlamaServerLaunchPolicyOptions _launchPolicyOptions;

    public BenchmarkPhaseLaunchResolver(IInferenceProfileResolver inferenceProfiles,
        IGpuVariantSelector variantSelector,
        ILlamaServerLaunchCapabilityInspector launchCapabilities,
        ILlamaServerLaunchFallbackStore launchFallbackStore,
        ILlamaServerLaunchPolicy launchPolicy,
        LlamaServerLaunchPolicyOptions launchPolicyOptions)
    {
        ArgumentNullException.ThrowIfNull(inferenceProfiles);
        ArgumentNullException.ThrowIfNull(variantSelector);
        ArgumentNullException.ThrowIfNull(launchCapabilities);
        ArgumentNullException.ThrowIfNull(launchFallbackStore);
        ArgumentNullException.ThrowIfNull(launchPolicy);
        ArgumentNullException.ThrowIfNull(launchPolicyOptions);
        _inferenceProfiles = inferenceProfiles;
        _variantSelector = variantSelector;
        _launchCapabilities = launchCapabilities;
        _launchFallbackStore = launchFallbackStore;
        _launchPolicy = launchPolicy;
        _launchPolicyOptions = launchPolicyOptions;
    }

    public async Task<LlamaServerLaunchCapabilities?> InspectAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _launchCapabilities.InspectAsync(cancellationToken);
        }
        catch (LlamaRuntimeException)
        {
            return null;
        }
    }

    public async Task<GpuVariant> SelectVariantAsync(LlamaServerLaunchCapabilities? capabilities, CancellationToken cancellationToken) =>
        capabilities?.Variant ?? await _variantSelector.SelectVariantAsync(cancellationToken);

    public async Task<BenchmarkFrozenLaunch> ResolveAsync(string modelName,
        int requiredContextTokens,
        string? requestedKvCacheType,
        LlamaServerLaunchCapabilities? capabilities,
        GpuVariant variant,
        CancellationToken cancellationToken)
    {
        var resolved = await _inferenceProfiles.ResolveAsync(modelName, ModelRole.Chat, variant, cancellationToken);
        if (resolved.ExploreMode)
        {
            resolved = ResolvedLaunchArguments.Replay(requiredContextTokens);
        }

        if (resolved.CtxSize < requiredContextTokens)
        {
            throw new BenchmarkEligibilityException("The resolved llama.cpp runtime context is smaller than the benchmark requirement.");
        }

        // Auto is the only decision the fallback store takes part in: an explicit pick is answered from the manifest alone, so an operator can retry a config a previous host state disabled. Keyed
        // per (backend, KV type), which this class has none of, it asks about the node's SELECTED type — the one Auto would pick — so Auto avoids a config this host proved cannot reach readiness.
        var optimizedDisabled = requestedKvCacheType is null
                                && variant != GpuVariant.Cpu
                                && await _launchFallbackStore.IsOptimizedConfigDisabledAsync(variant, _launchPolicyOptions.KvCacheType, cancellationToken);
        var (effective, source, reason) = ResolveKvCacheType(requestedKvCacheType, variant, capabilities, optimizedDisabled);
        var applied = BenchmarkKvCacheType.Apply(resolved, effective);

        // The plan the supervisor will build for this spawn: a benchmark launch applies no launch policy, so a GPU
        // replay gets a null plan and a CPU replay the two args a CPU build can honour.
        var plan = variant == GpuVariant.Cpu ? _launchPolicy.ResolveCpuReplayPlan(applied) : (LlamaServerLaunchPlan?)null;
        var policy = LlamaServerBenchmarkLaunchPolicy.DeterministicV1;
        var intendedIdentity = LlamaServerLaunchProjection.From(variant, applied, plan, ModelRole.Chat, policy.ChatCacheReuse, policy.ChatCacheRamMiB)
                                                          .ComputeIdentity();
        return new BenchmarkFrozenLaunch
        {
            Runtime = new BenchmarkLlamaRuntimeSnapshotV1(variant,
                applied.CtxSize,
                applied.NGpuLayers,
                applied.TensorSplit,
                applied.OverrideTensor,
                applied.KvTypeK,
                applied.KvTypeV,
                applied.FlashAttn,
                policy),
            Intent = new BenchmarkRunLaunchIntent
            {
                Variant = BenchmarkLaunchBackend.VariantName(variant),
                KvCacheType = effective,
                KvCacheTypeSource = source,
                KvAutoReason = reason,
                FlashAttentionMode = BenchmarkKvCacheType.IsQuantized(effective) ? LlamaServerLaunchProjection.FlashAttentionOn : LlamaServerLaunchProjection.FlashAttentionAuto,
                IntendedLaunchIdentity = intendedIdentity,
                IntendedExecutableSha256 = capabilities?.ManifestSha256,
                // Stamped once, here, at freeze. Never recomputed at execution: the snapshot carries no CPU thread
                // inputs, so re-projecting would adopt the executing host's conditions as historical intent.
                LaunchIdentityScheme = LlamaServerLaunchProjection.IdentitySchemeVersion
            }
        };
    }

    /// <summary>
    ///     The KV-cache type this launch will actually use. Auto degrades to <c>f16</c> with a recorded reason; an
    ///     explicit quantized pick the selected binary cannot be shown to accept is refused (422) rather than
    ///     discovered as a failed spawn.
    /// </summary>
    private static KvCacheResolution ResolveKvCacheType(string? requested,
        GpuVariant variant,
        LlamaServerLaunchCapabilities? capabilities,
        bool optimizedDisabled)
    {
        var probed = capabilities is { ProbeSucceeded: true };
        var isGpu = variant != GpuVariant.Cpu;
        if (requested is null)
        {
            if (!isGpu)
            {
                return new KvCacheResolution(BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, AutoReasonCpuVariant);
            }

            if (!probed)
            {
                return new KvCacheResolution(BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, AutoReasonProbeUnavailable);
            }

            if (optimizedDisabled)
            {
                return new KvCacheResolution(BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, AutoReasonFallbackDisabled);
            }

            return Accepts(capabilities!, BenchmarkKvCacheType.Q8_0)
                ? new KvCacheResolution(BenchmarkKvCacheType.Q8_0, BenchmarkKvCacheType.SourceAuto, null)
                : new KvCacheResolution(BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, AutoReasonManifestUnsupported);
        }

        if (!BenchmarkKvCacheType.IsQuantized(requested))
        {
            return new KvCacheResolution(requested, BenchmarkKvCacheType.SourceExplicit, null);
        }

        if (!isGpu)
        {
            throw new BenchmarkUnsupportedKvCacheTypeException($"A {requested} KV cache needs a GPU llama.cpp build, and this node selected the CPU build. Pick f16.");
        }

        if (!probed)
        {
            throw new BenchmarkUnsupportedKvCacheTypeException(
                $"The selected llama.cpp binary could not be inspected, so a {requested} KV cache cannot be confirmed. Pick f16 or repair the llama.cpp runtime.");
        }

        if (!Accepts(capabilities!, requested))
        {
            throw new BenchmarkUnsupportedKvCacheTypeException($"The selected llama.cpp binary does not accept a {requested} KV cache with flash attention. Pick f16.");
        }

        return new KvCacheResolution(requested, BenchmarkKvCacheType.SourceExplicit, null);
    }

    private static bool Accepts(LlamaServerLaunchCapabilities capabilities, string cacheType) =>
        capabilities.SupportsCacheTypeK(cacheType)
        && capabilities.SupportsCacheTypeV(cacheType)
        && capabilities.SupportsFlashAttentionMode(LlamaServerLaunchProjection.FlashAttentionOn);
}
