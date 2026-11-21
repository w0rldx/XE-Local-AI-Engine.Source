namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The <c>llama-server</c> flags the node manages: the launch-argument composer emits them, and an operator's
///     extra-argument override may neither set nor replace them.
/// </summary>
/// <remarks>
///     One list for both sides, so a flag the composer starts emitting is protected by the same edit. Reachability
///     (the node binds these to reach its process), memory-fit placement (decided before admission; an override would
///     invalidate the memory ledger), adapter identity and steering. Ordinal: llama.cpp flags are ASCII. Rationale per flag:
///     docs/wiki/03-local-runtime-and-providers.md ("2.7 Per-model extra launch arguments (operator override)").
/// </remarks>
public static class LlamaServerManagedFlags
{
    public const string Model = "-m";
    public const string Host = "--host";
    public const string Port = "--port";
    public const string Alias = "--alias";
    public const string ContextSize = "-c";
    public const string GpuLayers = "--n-gpu-layers";
    public const string TensorSplit = "-ts";
    public const string OverrideTensor = "-ot";
    public const string CpuMoe = "--cpu-moe";
    public const string Fit = "--fit";
    public const string FitTarget = "--fit-target";
    public const string CacheTypeK = "-ctk";
    public const string CacheTypeV = "-ctv";
    public const string FlashAttentionShort = "-fa";
    public const string FlashAttention = "--flash-attn";
    public const string Parallel = "--parallel";
    public const string BatchSize = "-b";
    public const string UbatchSize = "-ub";
    public const string Lora = "--lora";
    public const string ControlVector = "--control-vector";
    public const string ControlVectorScaled = "--control-vector-scaled";
    public const string ControlVectorLayerRange = "--control-vector-layer-range";
    public const string Embeddings = "--embeddings";
    public const string Pooling = "--pooling";
    public const string Rerank = "--rerank";

    /// <summary>Every managed spelling, aliases included, in match order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        // Reachability. The alias is the model id the process answers to, so it stays the id the node routes by.
        Model, "--model", Host, Port, "-a", Alias,

        // Memory-fit placement.
        ContextSize, "--ctx-size",
        "-ngl", "--gpu-layers", GpuLayers,
        TensorSplit, "--tensor-split",
        OverrideTensor, "--override-tensor",
        // --cpu-moe/-cmoe and --n-cpu-moe/-ncmoe are -ot by another name: upstream pushes them into the SAME
        // tensor_buft_overrides list -ot writes (llama.cpp common/arg.cpp), so an override could re-place every expert after admission.
        "-cmoe", CpuMoe,
        "-ncmoe", "--n-cpu-moe",
        // Auto-fit and its margin carry a live-share booking into the child; an override could turn the fit off or retarget it.
        "-fit", Fit, "-fitt", FitTarget, "-fitc", "--fit-ctx",
        CacheTypeK, "--cache-type-k",
        CacheTypeV, "--cache-type-v",
        FlashAttentionShort, FlashAttention,
        "-np", Parallel,
        // The chat role's two-slot pool (LlamaServerChatSlots): an override could split, uncap or re-clear it.
        "-kvu", "--kv-unified", "-no-kvu", "--no-kv-unified", "--kv-unified-per-slot",
        "--cache-idle-slots", "--no-cache-idle-slots",
        BatchSize, "--batch-size",
        UbatchSize, "--ubatch-size",

        // Adapter identity: the registry decides whether a model launches with an adapter and which one, and the launch-policy
        // fingerprint commits to that choice; an operator --lora would load weights the fingerprint, ledger and registry know nothing about.
        Lora, "--lora-scaled",

        // Steering: a vector changes every output of its process, so it stays on the private harness launch that records it.
        ControlVector, ControlVectorScaled, ControlVectorLayerRange,

        // Serving mode: the embedding profile's pooling is part of the stored vector identity, and --rerank flips pooling to rank wherever it lands.
        "--embedding", Embeddings, Pooling, Rerank, "--reranking"
    ];

    // llama.cpp's LLM_FFN_EXPS_REGEX with the CPU buffer: what --cpu-moe installs and llama-fit-params echoes back as -ot.
    private const string AllExpertsToCpu = @"\.ffn_(up|down|gate|gate_up)_(ch|)exps=CPU";

    /// <summary>Whether <paramref name="arguments" /> keep expert weights in system RAM: the admitted <c>--cpu-moe</c>, or a replayed expert <c>-ot</c>.</summary>
    public static bool PinsExpertsToCpu(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(static argument => string.Equals(argument, CpuMoe, StringComparison.Ordinal) || ClassifyExpertPin(argument) != ExpertPinScope.None);
    }

    /// <summary>
    ///     Classifies an <c>-ot</c> value: every entry the all-expert pattern, some entry an <c>exps=CPU</c> pin, or none. A mix counts as partial,
    ///     which prices a frozen replay at no less than today's booking.
    /// </summary>
    public static ExpertPinScope ClassifyExpertPin(string? overrideTensor)
    {
        if (string.IsNullOrWhiteSpace(overrideTensor) || !overrideTensor.Contains("exps=CPU", StringComparison.OrdinalIgnoreCase))
        {
            return ExpertPinScope.None;
        }

        var entries = overrideTensor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.All(static entry => string.Equals(entry, AllExpertsToCpu, StringComparison.Ordinal))
            ? ExpertPinScope.AllExperts
            : ExpertPinScope.Partial;
    }
}
