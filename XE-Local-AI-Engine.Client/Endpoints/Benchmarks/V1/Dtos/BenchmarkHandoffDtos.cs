namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

/// <summary>
///     Turns one training comparison into a benchmark project with its paired base/tuned runs.
///     <para>
///         The id is route-bound and everything else comes from the body, so <c>ComparisonId</c> cannot be
///         <c>required</c> — the body is deserialized before the route value is applied and the generated client sends
///         only the body members (same rule as <c>DeleteComparisonRequest</c>).
///     </para>
/// </summary>
public sealed class CreateBenchmarkFromComparisonRequest
{
    public Guid ComparisonId { get; init; }

    /// <summary>Blank reuses the comparison's own name, which is also what an existing project is matched on.</summary>
    public string? Name { get; init; }

    /// <summary>
    ///     REQUIRED, and deliberately the operator's: a comparison's evaluation prompt scores hold-out samples, so
    ///     reusing it as the benchmark task would measure the wrong thing.
    /// </summary>
    public required string CoreTask { get; init; }

    public required int ContextTokens { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    /// <summary>Null (or blank) is Auto. Both sides are frozen with the same value — that is what makes them paired.</summary>
    public string? KvCacheType { get; init; }

    public int RepeatCount { get; init; } = 1;

    public bool Warmup { get; init; }
}

/// <param name="RunIds">The base runs first, then the tuned ones, in the order they were enqueued.</param>
public sealed class CreateBenchmarkFromComparisonResponse
{
    public required Guid ProjectId { get; init; }
    public required string BaseModelName { get; init; }
    public required string TunedModelName { get; init; }
    public required IReadOnlyList<Guid> RunIds { get; init; }
}
