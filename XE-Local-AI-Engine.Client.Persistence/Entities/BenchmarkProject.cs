namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class BenchmarkProject
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_core_task_json</c>.
    /// </summary>
    public byte[] CoreTaskJson { get; set; } = [];

    public int ContextTokens { get; set; }

    /// <summary>
    ///     The per-run output-token budget frozen into every run's sampling (<c>n_predict</c>), or
    ///     <see langword="null" /> to leave generation context-limited. Plaintext, not sensitive.
    /// </summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    ///     The per-request thinking budget (<c>reasoning_budget_tokens</c>) frozen into every run's sampling, or
    ///     <see langword="null" /> to leave the reasoning bounded only by the reasoning-effort ladder and the window.
    ///     Plaintext, not sensitive.
    /// </summary>
    public int? ReasoningBudgetTokens { get; set; }

    /// <summary>
    ///     How long one run's generation may take before the node cancels it, or <see langword="null" /> for the
    ///     frozen default (<see cref="Services.Benchmarks.BenchmarkFrozenPolicies" />). Plaintext, not sensitive.
    /// </summary>
    public int? InvocationTimeoutSeconds { get; set; }

    public Guid AgentDefinitionId { get; set; }

    /// <summary>The judge policy revision this project judges under, or <see langword="null" /> when judging is off.</summary>
    public Guid? CurrentJudgePolicyRevisionId { get; set; }

    /// <summary>Whether freeze enqueues a quant-fidelity (perplexity) measurement beside each measured run.</summary>
    public bool FidelityEnabled { get; set; }

    /// <summary>
    ///     Whether the fidelity pass also measures KL-divergence against a base model. Opt-in and separate because the
    ///     base-logit cache it needs is tens of gigabytes per base model.
    /// </summary>
    public bool FidelityKldEnabled { get; set; }

    /// <summary>Chunks to score at the pinned 512-token window, or <see langword="null" /> for the frozen default.</summary>
    public int? FidelityChunks { get; set; }

    /// <summary>The base model KL-divergence is measured against, and its content fingerprint. Persisted rather than
    ///     remembered, so "which base was this KLD against" is answerable on read.</summary>
    public string? FidelityKldBaseModelName { get; set; }

    public string? FidelityKldBaseFingerprint { get; set; }

    /// <summary>
    ///     <c>v1:</c> + SHA-256 over this project's LEAF task items, ordered by their immutable
    ///     <see cref="BenchmarkTaskItem.Id" /> rather than by index — so adding or deleting an item moves the hash and
    ///     reordering does not. <see langword="null" /> until the project's first item write. Plaintext, and the value
    ///     every run copies at freeze so a cell can say which question set it was measured against.
    /// </summary>
    public string? TaskItemSetHash { get; set; }

    public long Version { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
