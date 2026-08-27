namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

public sealed class UpdateBenchmarkJudgePolicyRequest
{
    public Guid ProjectId { get; init; }

    /// <summary><see langword="null" /> disables judging; existing revisions and attempts stay as history.</summary>
    public BenchmarkJudgePolicyDraftDto? Policy { get; init; }

    public long ExpectedVersion { get; init; }

    /// <summary>Required when the policy actually changes on a project that already has runs.</summary>
    public bool ConfirmRejudge { get; init; }
}

public sealed class BenchmarkJudgePolicyDraftDto
{
    public string ModelName { get; init; } = string.Empty;
    public int ContextTokens { get; init; }

    /// <summary>
    ///     <c>pointwise</c> (the default an omitted value takes) or <c>pairwise</c>. Switching modes changes the policy
    ///     hash, so it mints a revision and re-judges the project — the pre-flight estimate says what that costs.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>Omitted takes the default rubric.</summary>
    public BenchmarkRubricDto? Rubric { get; init; }

    public string? ReferenceAnswer { get; init; }
}

public sealed class BenchmarkRubricCriterionDto
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Weight { get; init; }

    /// <summary>
    ///     How the criterion is decided: <c>llm</c> (the default an omitted value takes), <c>exact</c>, <c>regex</c>,
    ///     <c>jsonSchema</c>, <c>mathAnswer</c>, <c>constraint</c> or <c>pythonTests</c>. Optional on the way in so a caller written
    ///     before verifiable criteria existed keeps working unchanged; always present on the way out.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>The kind's configuration as JSON, or null for <c>llm</c>. Validated when the judge policy is saved.</summary>
    public string? Config { get; init; }
}

public sealed class BenchmarkRubricDto
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<BenchmarkRubricCriterionDto> Criteria { get; init; } = [];
}

/// <summary>The judge configuration a project currently judges under, decrypted from its policy revision.</summary>
public sealed class BenchmarkJudgePolicyResponse
{
    public bool Enabled { get; init; }
    public Guid? PolicyRevisionId { get; init; }
    public int? PolicyRevision { get; init; }
    public string? PolicyHash { get; init; }
    public string? ModelName { get; init; }
    public int? RequestedContextTokens { get; init; }
    public BenchmarkRubricDto? Rubric { get; init; }
    public string? ReferenceAnswer { get; init; }

    /// <summary>The judging mode this revision was stored under: <c>pointwise</c> or <c>pairwise</c>.</summary>
    public string? Mode { get; init; }

    public int? CohortGeneration { get; init; }
    public string? ReferenceExecutionKey { get; init; }

    /// <summary>The judge prompt version this revision was stored under.</summary>
    public int? PromptVersion { get; init; }

    /// <summary>
    ///     True when <see cref="PromptVersion" /> is not the one this build judges under. The revision still READS —
    ///     the project opens, the export works, existing scores stay ranked — but no NEW judging will run against it
    ///     until the operator re-saves the judge, which mints a revision under the current version and re-judges.
    /// </summary>
    public bool PromptVersionOutdated { get; init; }
}

/// <summary>The result of a judge change: the updated project plus the runs a judging was queued for.</summary>
public sealed class BenchmarkJudgeChangeResponse
{
    public required BenchmarkProjectDetailResponse Project { get; init; }
    public IReadOnlyList<Guid> EnqueuedRunIds { get; init; } = [];
    public int? CohortGeneration { get; init; }
}

public sealed class BenchmarkRubricPresetsResponse
{
    public required BenchmarkRubricDto Default { get; init; }
    public required BenchmarkRubricDto Programming { get; init; }
    public required BenchmarkRubricDto Reasoning { get; init; }

    /// <summary>The all-verifiable preset: judged server-side, with no llama-server spawn at all.</summary>
    public required BenchmarkRubricDto Verifiable { get; init; }

    /// <summary>The compute-sandbox code-execution preset.</summary>
    public required BenchmarkRubricDto CodeExecution { get; init; }
}

/// <summary>One verifiable criterion's server-side evidence. Detail responses only.</summary>
public sealed class BenchmarkJudgeVerifierResponse
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public bool Passed { get; init; }
    public required string Detail { get; init; }
}

/// <summary>One rubric criterion as the judge scored it. Detail responses only.</summary>
public sealed class BenchmarkJudgeCriterionScoreResponse
{
    public required string Id { get; init; }
    public int Score { get; init; }
    public required string Rationale { get; init; }
}

/// <summary>
///     The run's judge state, derived from its current attempt. <see cref="Summary" /> and <see cref="Criteria" /> are
///     the decrypted verdict and appear on the detail response only — a list must not decrypt one blob per row.
/// </summary>
public sealed class BenchmarkRunJudgeResponse
{
    public required string State { get; init; }
    public int? Score { get; init; }
    public int? PolicyRevision { get; init; }
    public int? AttemptSequence { get; init; }
    public int? CohortGeneration { get; init; }
    public string? ExecutionKey { get; init; }
    public bool PolicyCurrent { get; init; }
    public bool ExecutionCurrent { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<BenchmarkJudgeCriterionScoreResponse>? Criteria { get; init; }

    /// <summary>
    ///     The evidence behind each server-side criterion, or null for a judging that had none. An attempt whose
    ///     rubric was entirely verifiable also carries <c>executionKey</c> <c>verified:v1</c> and no launch receipt.
    /// </summary>
    public IReadOnlyList<BenchmarkJudgeVerifierResponse>? Verifiers { get; init; }
}

/// <summary>
///     A run's quant-fidelity numbers. Display only — perplexity and KL divergence are never ranking inputs.
/// </summary>
