namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The persisted llama-server launch profile for one <c>(machine_key, model_name, role, backend)</c> key: the exact
///     launch args plus the benchmark that justifies them. There is exactly one live config per key — re-exploring at a
///     different quant/ctx OVERWRITES the single config (latest explore wins); benchmark history lives on
///     <see cref="ModelFitBenchmark" /> rows, not here. All columns are plaintext structural data — no secrets, so this
///     entity is NOT on the node encryption-interceptor path. The machine key is a local-only random id (never hardware
///     derived, never emitted in telemetry/aggregates).
/// </summary>
internal sealed record class InferenceProfile
{
    public Guid Id { get; set; }

    /// <summary>Local-only stable machine id; never leaves the device. Part of the natural key. Plaintext (structural).</summary>
    public string MachineKey { get; set; } = string.Empty;

    /// <summary>Canonical <c>repo:quant</c> model name. Part of the natural key. Plaintext (structural).</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    ///     The llama-server role this profile targets, stored as the integer value of <c>ModelRole</c> (Chat=0,
    ///     Embedding=1). Persisted as a plain <see cref="int" /> because Persistence does not reference
    ///     <c>Providers.LlamaServer</c>; the Application layer maps it to/from <c>ModelRole</c>. Part of the natural key.
    /// </summary>
    public int Role { get; set; }

    /// <summary>Resolved backend (<c>cuda</c> | <c>vulkan</c> | <c>cpu</c>). Part of the natural key. Plaintext.</summary>
    public string Backend { get; set; } = string.Empty;

    /// <summary>llama.cpp binary tag/commit recorded at freeze; drives the build-change invalidation trigger. Plaintext.</summary>
    public string LlamacppBuild { get; set; } = string.Empty;

    /// <summary>Quantization (e.g. <c>Q4_K_M</c>). Plaintext (structural).</summary>
    public string Quant { get; set; } = string.Empty;

    /// <summary>Frozen context size (<c>-c</c>). Plaintext (structural).</summary>
    public int CtxSize { get; set; }

    /// <summary>Frozen GPU layer count (<c>-ngl</c>), or null to leave unset. Plaintext (structural).</summary>
    public int? NGpuLayers { get; set; }

    /// <summary>Frozen tensor split (<c>-ts</c>), or null. Plaintext (structural).</summary>
    public string? TensorSplit { get; set; }

    /// <summary>Frozen expert/tensor placement (<c>-ot</c>, MoE), or null. Plaintext (structural).</summary>
    public string? OverrideTensor { get; set; }

    /// <summary>Frozen KV cache key type (<c>-ctk</c>, e.g. <c>f16</c>/<c>q8_0</c>); set together with <see cref="KvTypeV" />.</summary>
    public string? KvTypeK { get; set; }

    /// <summary>Frozen KV cache value type (<c>-ctv</c>); set together with <see cref="KvTypeK" /> (matching-type invariant).</summary>
    public string? KvTypeV { get; set; }

    /// <summary>Whether the fused flash-attention path is enabled; required true when the KV cache types are quantized.</summary>
    public bool FlashAttn { get; set; }

    /// <summary>Total model parameter count, or null when unknown. Plaintext (structural).</summary>
    public long? NParams { get; set; }

    /// <summary>Whether the model is a mixture-of-experts model (a model attribute, not a role). Plaintext (structural).</summary>
    public bool IsMoe { get; set; }

    /// <summary>Expert count for an MoE model (<c>n_expert</c>), or null. Plaintext (structural).</summary>
    public int? ExpertCount { get; set; }

    /// <summary>Free-VRAM baseline (bytes) captured at freeze; the live free-VRAM invalidation trigger compares against it.</summary>
    public long? FreeVramAtFreezeBytes { get; set; }

    /// <summary>Lifecycle status. Plaintext (structural).</summary>
    public InferenceProfileStatus Status { get; set; }

    /// <summary>The justifying benchmark snapshot (<c>model_fit_snapshots</c>), or null until frozen. Plaintext (structural).</summary>
    public Guid? BenchmarkSnapshotId { get; set; }

    /// <summary>Unix-ms instant the row was created. Plaintext (structural).</summary>
    public long CreatedAtUtc { get; set; }

    /// <summary>Unix-ms instant the row was last written (re-explore or status transition). Plaintext (structural).</summary>
    public long UpdatedAtUtc { get; set; }
}
