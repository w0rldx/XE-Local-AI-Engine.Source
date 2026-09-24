namespace XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed record BenchmarkProjectInput
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required ReadOnlyMemory<byte> CoreTaskJson { get; init; }

    public required int ContextTokens { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    /// <summary>
    ///     The per-run output-token budget frozen into every run's sampling, or <see langword="null" /> to leave generation
    ///     context-limited. Must be <c>1 &lt;= MaxOutputTokens &lt; ContextTokens</c>.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    public int? InvocationTimeoutSeconds { get; init; }

    /// <summary>
    ///     The per-request thinking budget frozen into every run's sampling, or <see langword="null" /> to leave the
    ///     reasoning bounded only by the effort ladder and the window. Must be <c>1 &lt;= ReasoningBudgetTokens &lt;
    ///     ContextTokens</c>.
    /// </summary>
    public int? ReasoningBudgetTokens { get; init; }

    public bool FidelityEnabled { get; init; }

    public bool FidelityKldEnabled { get; init; }

    public int? FidelityChunks { get; init; }

    public string? FidelityKldBaseModelName { get; init; }

    public string? FidelityKldBaseFingerprint { get; init; }
}

/// <summary>
///     The judge half of a project write, applied in the project's own transaction. A <see langword="null" /> instance
///     leaves the judge alone; an instance with a <see langword="null" /> <see cref="PolicyJson" /> disables it.
/// </summary>
public sealed record BenchmarkJudgePolicyChangeInput
{
    public required ReadOnlyMemory<byte>? PolicyJson { get; init; }

    public required string? PolicyHash { get; init; }

    /// <summary>Turns judging off as part of the project write.</summary>
    public static BenchmarkJudgePolicyChangeInput Disabled { get; } = new()
    {
        PolicyJson = null,
        PolicyHash = null
    };
}

public sealed record BenchmarkProjectRecord
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required ReadOnlyMemory<byte> CoreTaskJson { get; init; }

    public required int ContextTokens { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    /// <summary>Derived: the project judges exactly while it points at a policy revision.</summary>
    public required bool JudgeEnabled { get; init; }

    public required Guid? CurrentJudgePolicyRevisionId { get; init; }

    public required bool IsFrozen { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public int? MaxOutputTokens { get; init; }

    public int? InvocationTimeoutSeconds { get; init; }

    public int? ReasoningBudgetTokens { get; init; }

    public bool FidelityEnabled { get; init; }

    public bool FidelityKldEnabled { get; init; }

    public int? FidelityChunks { get; init; }

    public string? FidelityKldBaseModelName { get; init; }

    public string? FidelityKldBaseFingerprint { get; init; }

    public string? TaskItemSetHash { get; init; }
}
