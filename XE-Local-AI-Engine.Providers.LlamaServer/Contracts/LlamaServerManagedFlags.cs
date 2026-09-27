namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The <c>llama-server</c> flags the node manages: the launch-argument composer emits them, and an operator's
///     extra-argument override may neither set nor replace them.
/// </summary>
/// <remarks>
///     One list for both sides, so a flag the composer starts emitting is protected by the same edit. Reachability
///     (the node binds these to reach its process), memory-fit placement (decided before admission; an override would
///     invalidate the memory ledger) and adapter identity. Ordinal: llama.cpp flags are ASCII. Rationale per flag:
///     docs/wiki/03-local-runtime-and-providers.md ("2.7 Per-model extra launch arguments (operator override)").
/// </remarks>
public static class LlamaServerManagedFlags
{
    public const string Model = "-m";
    public const string Host = "--host";
    public const string Port = "--port";
    public const string ContextSize = "-c";
    public const string GpuLayers = "--n-gpu-layers";
    public const string TensorSplit = "-ts";
    public const string OverrideTensor = "-ot";
    public const string CpuMoe = "--cpu-moe";
    public const string CacheTypeK = "-ctk";
    public const string CacheTypeV = "-ctv";
    public const string FlashAttentionShort = "-fa";
    public const string FlashAttention = "--flash-attn";
    public const string Parallel = "--parallel";
    public const string BatchSize = "-b";
    public const string UbatchSize = "-ub";
    public const string Lora = "--lora";

    /// <summary>Every managed spelling, aliases included, in match order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        // Reachability.
        Model, "--model", Host, Port,

        // Memory-fit placement.
        ContextSize, "--ctx-size",
        "-ngl", "--gpu-layers", GpuLayers,
        TensorSplit, "--tensor-split",
        OverrideTensor, "--override-tensor",
        // --cpu-moe/-cmoe and --n-cpu-moe/-ncmoe are -ot by another name: upstream pushes them into the SAME
        // tensor_buft_overrides list -ot writes (llama.cpp common/arg.cpp), so an override could re-place every expert after admission.
        "-cmoe", CpuMoe,
        "-ncmoe", "--n-cpu-moe",
        CacheTypeK, "--cache-type-k",
        CacheTypeV, "--cache-type-v",
        FlashAttentionShort, FlashAttention,
        "-np", Parallel,
        BatchSize, "--batch-size",
        UbatchSize, "--ubatch-size",

        // Adapter identity: the registry decides whether a model launches with an adapter and which one, and the launch-policy
        // fingerprint commits to that choice; an operator --lora would load weights the fingerprint, ledger and registry know nothing about.
        Lora, "--lora-scaled"
    ];
}
