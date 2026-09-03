namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A single measured benchmark row projected from a benchmark snapshot. The structural metrics are plaintext; the
///     raw benchmark output and detailed diagnostics are sensitive and stored as encrypted UTF-8 byte columns —
///     encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
///     <see cref="NodeEncryptionMaterializationInterceptor" />. The AAD column names are deliberately distinct from the
///     snapshot's (<c>bench_raw_json</c>/<c>bench_diagnostics_json</c>) to avoid cross-entity AAD collision. Cascades
///     when its parent snapshot is deleted.
/// </summary>
internal sealed record class ModelFitBenchmark
{
    public Guid Id { get; set; }

    /// <summary>Parent snapshot; real FK with cascade delete, indexed. Plaintext (structural).</summary>
    public Guid SnapshotId { get; set; }

    /// <summary>Benchmarked model name. Plaintext.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Provider name (e.g. <c>ollama</c>). Plaintext.</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Measured tokens per second, or null. Plaintext.</summary>
    public double? TokensPerSecond { get; set; }

    /// <summary>Time to first token in milliseconds, or null. Plaintext.</summary>
    public double? TtftMs { get; set; }

    /// <summary>Total latency in milliseconds, or null. Plaintext.</summary>
    public double? TotalLatencyMs { get; set; }

    /// <summary>Number of measured runs, or null. Plaintext.</summary>
    public int? Runs { get; set; }

    /// <summary>
    ///     Raw benchmark JSON output as UTF-8 bytes. Plaintext while tracked in memory; encrypted at rest using AAD column
    ///     name <c>bench_raw_json</c>. Optional.
    /// </summary>
    public byte[]? RawJson { get; set; }

    /// <summary>
    ///     Detailed benchmark diagnostics as UTF-8 bytes (JSON). Plaintext while tracked in memory; encrypted at rest using
    ///     AAD column name <c>bench_diagnostics_json</c>. Optional.
    /// </summary>
    public byte[]? DiagnosticsJson { get; set; }

    // Agent-loop benchmark metrics (additive, all nullable — legacy rows pre-date the agent-loop bench). Plaintext
    // numerics, same posture as tokens_per_second (no secrets).

    /// <summary>Prompt-processing throughput (tokens/s), or null. Plaintext.</summary>
    public double? PpTokensPerSecond { get; set; }

    /// <summary>Prompt-cache hit rate derived from <c>/metrics</c> prompt-token reuse, or null. Plaintext.</summary>
    public double? CacheHitRate { get; set; }

    /// <summary>Agent tool-call round latency in milliseconds, or null. Plaintext.</summary>
    public double? ToolLoopMs { get; set; }

    /// <summary>Host-observed free-VRAM delta at load (bytes); a delta estimate, not exact resident bytes. Optional. Plaintext.</summary>
    public long? VramLoadBytes { get; set; }

    /// <summary>Host-observed free-VRAM delta after the loop (bytes); a delta estimate. Optional. Plaintext.</summary>
    public long? VramAfterBytes { get; set; }

    public long? GlobalFreeVramLoadBytes { get; set; }

    public long? GlobalFreeVramAfterBytes { get; set; }

    public long? ProcessBudgetVramLoadBytes { get; set; }

    public long? ProcessBudgetVramAfterBytes { get; set; }

    public long? MinimumGlobalFreeVramBytes { get; set; }

    public long? MinimumProcessBudgetVramBytes { get; set; }

    public long? PeakProcessRamBytes { get; set; }

    public bool ExternalPressureDetected { get; set; }

    // Reproducibility key (today only in ephemeral job params).

    /// <summary>llama.cpp binary tag/commit the bench ran on, or null. Plaintext.</summary>
    public string? LlamacppBuild { get; set; }

    /// <summary>Quantization (e.g. <c>Q4_K_M</c>) the bench ran at, or null. Plaintext.</summary>
    public string? Quant { get; set; }

    /// <summary>Context size (<c>-c</c>) the bench ran at, or null. Plaintext.</summary>
    public int? CtxSize { get; set; }

    /// <summary>KV cache type the bench ran with (e.g. <c>f16</c>/<c>q8_0</c>), or null. Plaintext.</summary>
    public string? KvType { get; set; }

    /// <summary>Backend the bench ran on (<c>cuda</c>/<c>vulkan</c>/<c>cpu</c>), or null. Plaintext.</summary>
    public string? Backend { get; set; }

    /// <summary>Local-only machine key the bench ran on, or null. Never emitted in telemetry/aggregates. Plaintext.</summary>
    public string? MachineKey { get; set; }

    // Placement args that dominate MoE tok/s — persisted on the row so a measurement reproduces itself without
    // joining back through inference_profiles.

    /// <summary>GPU layer count (<c>-ngl</c>) the bench ran with, or null. Plaintext.</summary>
    public int? NGpuLayers { get; set; }

    /// <summary>Tensor split (<c>-ts</c>) the bench ran with, or null. Plaintext.</summary>
    public string? TensorSplit { get; set; }

    /// <summary>Expert/tensor placement (<c>-ot</c>) the bench ran with, or null. Plaintext.</summary>
    public string? OverrideTensor { get; set; }

    /// <summary>KV cache value type (<c>-ctv</c>) the bench ran with, or null. Pairs with <c>KvType</c> (=<c>-ctk</c>). Plaintext.</summary>
    public string? KvTypeV { get; set; }

    /// <summary>Whether flash-attention (<c>-fa</c>) was enabled for the bench, or null for legacy rows. Plaintext.</summary>
    public bool? FlashAttn { get; set; }

    // Profile revision binding (additive, nullable — legacy rows predate it). The freeze gate qualifies a benchmark
    // only when this matches the profile being frozen AND the row's launch args still match the profile's current
    // args, so a benchmark taken before a re-explore can never freeze the changed configuration.

    /// <summary>The inference profile revision this benchmark measured, or null for legacy rows. Plaintext (structural).</summary>
    public Guid? ProfileId { get; set; }

    /// <summary>Launch-policy fingerprint schema version measured by this benchmark, or null for legacy rows.</summary>
    public int? LaunchPolicyFingerprintVersion { get; set; }

    /// <summary>Launch-policy fingerprint measured by this benchmark, or null for legacy rows.</summary>
    public string? LaunchPolicyFingerprint { get; set; }
}
