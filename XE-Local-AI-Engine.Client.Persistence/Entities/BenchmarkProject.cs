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
    ///     How long one run's generation may take before the node cancels it, or <see langword="null" /> for the
    ///     frozen default (<see cref="Services.Benchmarks.BenchmarkFrozenPolicies" />). Plaintext, not sensitive.
    /// </summary>
    public int? InvocationTimeoutSeconds { get; set; }

    public Guid AgentDefinitionId { get; set; }

    /// <summary>The judge policy revision this project judges under, or <see langword="null" /> when judging is off.</summary>
    public Guid? CurrentJudgePolicyRevisionId { get; set; }

    public long Version { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
