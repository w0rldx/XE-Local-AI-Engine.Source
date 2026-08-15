namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>
///     Sizes one QLoRA run against the box. Mirrors <c>MemoryFitEstimator</c>'s shape — constants up front, one pure
///     computation — but sizes against parameter count and the activation levers rather than GGUF quant bytes, because
///     nothing here is a GGUF.
/// </summary>
/// <remarks>
///     <para>
///         The breakdown, per the pinned-stack research: the 4-bit frozen base dominates at ≈0.6 bytes/param (4 bits
///         packed plus NF4 double-quant scale overhead); the bf16 LoRA weights and their two 8-bit Adam moment buffers
///         cost ≈4 bytes per TRAINABLE param, which is small because only the adapter trains; and activations are the
///         only term that scales with batch and sequence length. Gradient checkpointing keeps activations proportional
///         to a small constant number of layers' worth rather than all of them, so they are budgeted as a headroom term
///         with the batch/sequence lever kept linear rather than modelled precisely.
///     </para>
///     <para>
///         Two deliberate fail-safes: a headroom fraction on the fixed cost, and a floor at the frozen 4-bit weights
///         plus a CUDA context, so a checkpoint whose shape could not be read still cannot produce a tiny answer.
///     </para>
///     <para>
///         The floor is deliberately NOT the fp16 weight size the research brief proposed. That floor would refuse
///         exactly the runs this feature exists for: the published reference points the same brief cites put an 8B
///         QLoRA run near 6 GB, well under the 16 GB its own fp16 weights would need, so an fp16 floor makes every
///         estimate the floor and hides the batch and sequence levers entirely.
///     </para>
/// </remarks>
public static class TrainingFootprintEstimator
{
    /// <summary>Bytes per parameter of the frozen 4-bit base, including per-block quantization scales.</summary>
    public const double QuantizedBytesPerParameter = 0.6;

    /// <summary>bf16 adapter weights (2) plus the two 8-bit Adam moment buffers (1 each), per trainable parameter.</summary>
    public const double TrainableBytesPerParameter = 4.0;

    /// <summary>CUDA context, cuBLAS handles and the driver's own allocations.</summary>
    public const long CudaContextOverheadBytes = 1L * 1024 * 1024 * 1024;

    public const double ActivationHeadroomFraction = 0.18;

    /// <summary>How many layers' worth of activations survive gradient checkpointing, as a budgeting constant.</summary>
    private const int CheckpointedLayerEquivalent = 8;

    /// <summary>bf16 activations.</summary>
    private const int ActivationBytesPerElement = 2;

    /// <summary>Above this the run is marked experimental — it is beyond what this feature has been exercised on.</summary>
    public const long ExperimentalParameterThreshold = 27_000_000_000L;

    /// <summary>LoRA touches the four attention projections plus the three MLP projections.</summary>
    private const int AttentionProjectionCount = 4;

    private const int MlpProjectionCount = 3;

    /// <summary>Host RAM the trainer needs beyond the GPU: the process, the tokenized dataset and the dataloader.</summary>
    private const long HostRamBytes = 6L * 1024 * 1024 * 1024;

    public static TrainingFootprintEstimate Estimate(long parameterCount, BaseCheckpointConfigV1 config, TrainingRunOptionsV1 options)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);

        var trainable = EstimateTrainableParameters(config, options.LoraR);
        var fixedCost = (long)((parameterCount * QuantizedBytesPerParameter) + (trainable * TrainableBytesPerParameter));

        var hiddenSize = config.HiddenSize ?? 0;
        var layers = Math.Min(config.NumHiddenLayers ?? 0, CheckpointedLayerEquivalent);
        var activations = (long)options.PerDeviceTrainBatchSize
                          * options.MaxSeqLength
                          * hiddenSize
                          * Math.Max(layers, val2: 1)
                          * ActivationBytesPerElement;

        var gpuBytes = fixedCost
                       + activations
                       + (long)(fixedCost * ActivationHeadroomFraction)
                       + CudaContextOverheadBytes;

        // The fail-safe floor: the frozen 4-bit weights plus a CUDA context have to be resident no matter what the
        // rest of the formula says, so a shape this estimator could not read still cannot produce a tiny answer.
        gpuBytes = Math.Max(gpuBytes, (long)(parameterCount * QuantizedBytesPerParameter) + CudaContextOverheadBytes);
        return new TrainingFootprintEstimate(gpuBytes,
            HostRamBytes,
            parameterCount,
            trainable,
            parameterCount >= ExperimentalParameterThreshold);
    }

    /// <summary>
    ///     LoRA parameter count: two <c>r</c>-rank factors per adapted projection. The four attention projections are
    ///     square in the hidden size; the three MLP projections span hidden↔intermediate.
    /// </summary>
    public static long EstimateTrainableParameters(BaseCheckpointConfigV1 config, int loraRank)
    {
        ArgumentNullException.ThrowIfNull(config);
        var hiddenSize = (long)(config.HiddenSize ?? 0);
        if (hiddenSize <= 0 || loraRank <= 0)
        {
            return 0;
        }

        var layers = (long)Math.Max(config.NumHiddenLayers ?? 0, val2: 1);
        // Most Llama-architecture repos declare intermediate_size; 4x hidden is the family's usual ratio when absent.
        var intermediate = (long)(config.IntermediateSize ?? (config.HiddenSize!.Value * 4));
        var attention = AttentionProjectionCount * 2L * loraRank * hiddenSize;
        var mlp = MlpProjectionCount * (long)loraRank * (hiddenSize + intermediate);
        return layers * (attention + mlp);
    }

    /// <summary>
    ///     Total parameter count derived from the checkpoint's own weight bytes. There is no safetensors header reader
    ///     in this repo and none is needed: the manifest already records every shard's size, and a checkpoint's storage
    ///     dtype is declared in its config, so bytes ÷ bytes-per-parameter is both simpler and accurate to within the
    ///     tied-embedding rounding the headroom term already absorbs.
    /// </summary>
    public static long EstimateParameterCount(IReadOnlyList<BaseArtifactFileView> files, BaseCheckpointConfigV1 config)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(config);

        var weightBytes = files.Where(static file => string.Equals(file.Role, "Weights", StringComparison.OrdinalIgnoreCase))
                               .Where(static file => file.FileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
                               .Sum(static file => file.SizeBytes);
        var bytesPerParameter = BytesPerParameter(config.TorchDtype);
        return bytesPerParameter <= 0 ? 0 : (long)(weightBytes / bytesPerParameter);
    }

    private static double BytesPerParameter(string? torchDtype) =>
        torchDtype?.ToUpperInvariant() switch
        {
            "FLOAT32" or "FLOAT" => 4,
            "FLOAT8_E4M3FN" or "FLOAT8_E5M2" or "INT8" or "UINT8" => 1,
            // bfloat16/float16, and the safe assumption for an undeclared dtype: a 2-byte guess on a 1-byte checkpoint
            // over-counts parameters, which over-counts the footprint, which refuses rather than admits.
            _ => 2
        };

    /// <summary>
    ///     Tolerant <c>config.json</c> read. A repository is free to omit any field, so a partial parse is the normal
    ///     outcome and only a document that will not parse at all returns null.
    /// </summary>
    public static BaseCheckpointConfigV1? TryReadConfig(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(configPath);
            return JsonSerializer.Deserialize<BaseCheckpointConfigV1>(stream, TrainingJson.Options);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
