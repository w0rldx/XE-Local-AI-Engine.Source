namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

public class BenchmarkProjectMutationRequest
{
    public string Name { get; init; } = string.Empty;
    public string CoreTask { get; init; } = string.Empty;
    public int ContextTokens { get; init; }

    /// <summary>Per-run output-token budget; omitted leaves generation context-limited. Must be &lt; ContextTokens.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    ///     Per-run thinking budget (<c>reasoning_budget_tokens</c>); omitted leaves the reasoning bounded only by the
    ///     agent's reasoning effort and the window. Must be &lt; ContextTokens, and together with MaxOutputTokens must
    ///     leave a prompt reserve inside it.
    /// </summary>
    public int? ReasoningBudgetTokens { get; init; }

    /// <summary>Seconds one run's generation may take; omitted takes the node default (900). Range 60..7200.</summary>
    public int? InvocationTimeoutSeconds { get; init; }

    public Guid AgentDefinitionId { get; init; }
    public bool JudgeEnabled { get; init; }
    public string? JudgeModelName { get; init; }
    public int? JudgeContextTokens { get; init; }

    /// <summary>Omitted takes the default rubric.</summary>
    public BenchmarkRubricDto? Rubric { get; init; }

    public string? ReferenceAnswer { get; init; }

    /// <summary>Whether freeze enqueues a perplexity measurement beside each measured run. Display-only axis.</summary>
    public bool FidelityEnabled { get; init; }

    /// <summary>
    ///     Whether the fidelity pass also measures KL divergence. Opt-in separately: the base-logit cache it needs is
    ///     tens of gigabytes per base model. Requires <see cref="FidelityKldBaseModelName" />.
    /// </summary>
    public bool FidelityKldEnabled { get; init; }

    /// <summary>Chunks scored at the pinned 512-token window; omitted takes the default. Range 50..655.</summary>
    public int? FidelityChunks { get; init; }

    /// <summary>
    ///     The eligible local model KL divergence is measured against. Its fingerprint is resolved and persisted
    ///     server-side and is never accepted from a client — it is an input to the comparability digest.
    /// </summary>
    public string? FidelityKldBaseModelName { get; init; }
}

public sealed class UpdateBenchmarkProjectRequest : BenchmarkProjectMutationRequest
{
    public Guid ProjectId { get; init; }
    public long ExpectedVersion { get; init; }
}

public sealed class BenchmarkProjectRouteRequest
{
    public Guid ProjectId { get; init; }
}

public sealed class DeleteBenchmarkProjectRequest
{
    public Guid ProjectId { get; init; }
    public long ExpectedVersion { get; init; }
}

public class BenchmarkProjectSummaryResponse
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public int ContextTokens { get; init; }

    /// <summary>Per-run output-token budget, or null when generation is context-limited.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Per-run thinking budget, or null when the reasoning is bounded only by the effort and the window.</summary>
    public int? ReasoningBudgetTokens { get; init; }

    /// <summary>Seconds one run's generation may take, or null for the node default.</summary>
    public int? InvocationTimeoutSeconds { get; init; }

    public Guid AgentDefinitionId { get; init; }
    public bool JudgeEnabled { get; init; }
    public int RunCount { get; init; }
    public bool IsFrozen { get; init; }
    public long Version { get; init; }
    public long CreatedAtUtc { get; init; }
    public long UpdatedAtUtc { get; init; }
}

/// <summary>
///     The quant-fidelity settings, changeable on a frozen project. Deliberately NOT part of the project PUT: those
///     fields describe what the existing runs were measured against and are frozen with them, while these decide what
///     gets measured next.
/// </summary>
public sealed class UpdateBenchmarkProjectFidelityRequest
{
    public Guid ProjectId { get; init; }
    public long ExpectedVersion { get; init; }
    public bool FidelityEnabled { get; init; }

    /// <summary>Requires <see cref="FidelityKldBaseModelName" />. Turning it off keeps every stored attempt.</summary>
    public bool FidelityKldEnabled { get; init; }

    /// <summary>Chunks scored at the pinned 512-token window; omitted takes the default. Range 50..655.</summary>
    public int? FidelityChunks { get; init; }

    /// <summary>An eligible local model. Its fingerprint is resolved server-side and never accepted from a client.</summary>
    public string? FidelityKldBaseModelName { get; init; }

    /// <summary>
    ///     Also queue a measurement for every succeeded, non-warm-up, first-of-its-repeat-group run that has none.
    ///     Off by default: enabling fidelity should not silently spend GPU on a project's whole history.
    /// </summary>
    public bool MeasureExisting { get; init; }
}

/// <summary>The result of a fidelity change: the updated project plus the runs a measurement was queued for.</summary>
public sealed class BenchmarkProjectFidelityChangeResponse
{
    public required BenchmarkProjectDetailResponse Project { get; init; }
    public IReadOnlyList<Guid> EnqueuedRunIds { get; init; } = [];
    public int EnqueuedCount { get; init; }
}

public sealed class BenchmarkProjectDetailResponse : BenchmarkProjectSummaryResponse
{
    public IReadOnlyList<BenchmarkTaskItemResponse> TaskItems { get; init; } = [];
    public string? TaskItemSetHash { get; init; }

    public required string CoreTask { get; init; }

    /// <summary>The judge configuration this project judges under. <c>Enabled: false</c> when judging is off.</summary>
    public required BenchmarkJudgePolicyResponse Judge { get; init; }

    public bool FidelityEnabled { get; init; }
    public bool FidelityKldEnabled { get; init; }

    /// <summary>The operator's chunk count, or null for the default. <see cref="FidelityChunksEffective" /> is what runs.</summary>
    public int? FidelityChunks { get; init; }

    public int FidelityChunksEffective { get; init; }
    public string? FidelityKldBaseModelName { get; init; }

    /// <summary>The base model's content fingerprint as the node resolved it. Read-only; never accepted on a write.</summary>
    public string? FidelityKldBaseFingerprint { get; init; }

    /// <summary>
    ///     The base-logit digest the project's CURRENT settings recompute, or null when it does not measure KL
    ///     divergence. A stored KLD figure is served only while a run's digest equals this one — the whole cache key
    ///     is the comparability gate, not the base model's fingerprint, because the corpus, the chunk count and the
    ///     format version all move without the fingerprint moving.
    /// </summary>
    public string? FidelityKldExpectedDigest { get; init; }
}

public sealed class ListBenchmarkProjectsResponse
{
    public IReadOnlyList<BenchmarkProjectSummaryResponse> Items { get; init; } = [];
}

public sealed class EligibleBenchmarkAgentsRequest
{
    public string ModelName { get; init; } = string.Empty;
}

public sealed class EligibleBenchmarkAgentResponse
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public int Version { get; init; }
}

public sealed class ListEligibleBenchmarkAgentsResponse
{
    public IReadOnlyList<EligibleBenchmarkAgentResponse> Items { get; init; } = [];
}

public sealed class EligibleBenchmarkModelsRequest
{
    public int? ContextTokens { get; init; }
}

public sealed class EligibleBenchmarkModelResponse
{
    public required string ModelName { get; init; }
    public int? MaxContextTokens { get; init; }
    public int? EffectiveContextTokens { get; init; }
    public LocalModelOrigin? Origin { get; init; }
    public required string ModelContentFingerprint { get; init; }
    public bool SupportsTools { get; init; }
}

public sealed class ListEligibleBenchmarkModelsResponse
{
    public IReadOnlyList<EligibleBenchmarkModelResponse> Items { get; init; } = [];
}
