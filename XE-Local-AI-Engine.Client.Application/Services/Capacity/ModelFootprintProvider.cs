namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Default <see cref="IModelFootprintProvider" />. Resolves the installed model's footprint inputs through the GGUF
///     store seam (registry quant label + on-disk size + a cached tolerant header read) and scores them with the pure
///     <see cref="MemoryFitEstimator" />. Stateless → singleton; the only cache is the store's own header-facts cache.
/// </summary>
public sealed class ModelFootprintProvider : IModelFootprintProvider
{
    /// <summary>
    ///     Context window the KV-cache term is sized against (mirrors the advisor's <c>DefaultCtxTarget</c>). The
    ///     footprint is a budget pre-flight, not a per-request sizing, so a fixed conservative target keeps the estimate
    ///     stable across spawns and comparable to the advisor's recommendation math.
    /// </summary>
    private const int DefaultCtxTarget = 8192;

    private readonly MemoryFitEstimator _estimator;
    private readonly IGgufModelStore _modelStore;

    public ModelFootprintProvider(IGgufModelStore modelStore, MemoryFitEstimator estimator)
    {
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
    }

    /// <inheritdoc />
    public async Task<ModelFootprint> ResolveFootprintAsync(string modelName, HardwareProfile profile, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(profile);

        var facts = await _modelStore.ResolveModelFootprintFactsAsync(modelName, ct).ConfigureAwait(false);

        // Not installed (no registry entry / file gone) → Unknown → the gate rejects.
        if (facts is null)
        {
            return ModelFootprint.Unknown;
        }

        // No header param count AND no file size ⇒ nothing to estimate weights from → Unknown (invariant: conservative).
        if (facts.ParamCount is not > 0 && facts.FileSizeBytes <= 0)
        {
            return ModelFootprint.Unknown;
        }

        // Quant label is the registry's parsed value — strip the Unsloth Dynamic (UD-) marker before the density map so a
        // UD-Q4_K_XL prices off its base quant rather than defaulting to Q4_K_M. NEVER the header's general.file_type.
        var quant = GgufQuantParser.StripDynamicPrefix(facts.Quant);

        var estimate = _estimator.Estimate(quant,
            facts.ParamCount,
            facts.FileSizeBytes,
            facts.BlockCount ?? 0,
            facts.AttentionHeadCountKV ?? 0,
            facts.EmbeddingLength ?? 0,
            facts.AttentionHeadCount ?? 0,
            ResolveCtxTarget(facts.ContextLength),
            profile,
            kvCacheQuantized: false,
            // Explicit attention geometry (key/value lengths + SWA) corrects the KV term for Qwen3-family / Gemma models
            // whose derived head_dim is wrong; native-format detection keeps a native quant priced at its own density.
            attention: new GgufAttentionShape(facts.AttentionKeyLength, facts.AttentionValueLength, facts.SlidingWindow, facts.SlidingWindowPattern),
            nativeQuantFormat: QuantLadder.IsNativeFormat(quant));

        return ModelFootprint.Known(estimate.EstimatedBytes);
    }

    // Size the KV term against the model's own context window when the header reports one, capped at the default so a
    // model advertising a huge context cannot inflate the footprint past the conservative pre-flight target.
    private static long ResolveCtxTarget(long? contextLength)
    {
        return contextLength is > 0 and < DefaultCtxTarget ? contextLength.Value : DefaultCtxTarget;
    }
}
