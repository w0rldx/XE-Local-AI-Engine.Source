namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     What the judge will actually launch with, resolved once per attempt at enqueue and frozen onto that attempt.
/// </summary>
/// <remarks>
///     Deliberately NOT part of the policy hash: a runtime update changes this underneath the operator, and it must
///     make attempts <em>unranked together</em> (a new cohort key) rather than invalidate the policy.
/// </remarks>
public sealed record BenchmarkJudgeRuntimeV1(
    int SchemaVersion,
    BenchmarkInstalledModelSnapshotV1 Model,
    int RequestedContextTokens,
    BenchmarkLlamaRuntimeSnapshotV1 Runtime,
    BenchmarkSamplingSnapshotV1 Sampling)
{
    public const int CurrentSchemaVersion = 1;
}
