namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <param name="MaxOutputTokens">
///     The per-run output-token budget frozen into every run's sampling, or <see langword="null" /> to leave generation
///     context-limited. Must be <c>1 &lt;= MaxOutputTokens &lt; ContextTokens</c>.
/// </param>
/// <param name="ReasoningBudgetTokens">
///     The per-request thinking budget frozen into every run's sampling, or <see langword="null" /> to leave the
///     reasoning bounded only by the effort ladder and the window. Must be <c>1 &lt;= ReasoningBudgetTokens &lt;
///     ContextTokens</c>.
/// </param>
public sealed record BenchmarkProjectInput(
    Guid Id,
    string Name,
    ReadOnlyMemory<byte> CoreTaskJson,
    int ContextTokens,
    Guid AgentDefinitionId,
    int? MaxOutputTokens = null,
    int? InvocationTimeoutSeconds = null,
    int? ReasoningBudgetTokens = null,
    bool FidelityEnabled = false,
    bool FidelityKldEnabled = false,
    int? FidelityChunks = null,
    string? FidelityKldBaseModelName = null,
    string? FidelityKldBaseFingerprint = null);

/// <summary>
///     The judge half of a project write, applied in the project's own transaction. A <see langword="null" /> instance
///     leaves the judge alone; an instance with a <see langword="null" /> <paramref name="PolicyJson" /> disables it.
/// </summary>
public sealed record BenchmarkJudgePolicyChangeInput(ReadOnlyMemory<byte>? PolicyJson, string? PolicyHash)
{
    /// <summary>Turns judging off as part of the project write.</summary>
    public static BenchmarkJudgePolicyChangeInput Disabled { get; } = new(null, null);
}

/// <param name="JudgeEnabled">Derived: the project judges exactly while it points at a policy revision.</param>
public sealed record BenchmarkProjectRecord(
    Guid Id,
    string Name,
    ReadOnlyMemory<byte> CoreTaskJson,
    int ContextTokens,
    Guid AgentDefinitionId,
    bool JudgeEnabled,
    Guid? CurrentJudgePolicyRevisionId,
    bool IsFrozen,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    int? MaxOutputTokens = null,
    int? InvocationTimeoutSeconds = null,
    int? ReasoningBudgetTokens = null,
    bool FidelityEnabled = false,
    bool FidelityKldEnabled = false,
    int? FidelityChunks = null,
    string? FidelityKldBaseModelName = null,
    string? FidelityKldBaseFingerprint = null,
    string? TaskItemSetHash = null);
