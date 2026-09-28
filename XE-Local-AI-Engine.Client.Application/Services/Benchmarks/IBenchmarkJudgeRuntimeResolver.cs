namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

public interface IBenchmarkJudgeRuntimeResolver
{
    /// <summary>
    ///     Resolves the judge launch vector for <paramref name="policy" />. Throws when the policy's model is gone, is
    ///     no longer eligible, or its content fingerprint has moved — the caller records that as a failed attempt.
    /// </summary>
    Task<BenchmarkJudgeRuntimeResolution> ResolveAsync(BenchmarkJudgePolicyV1 policy, CancellationToken cancellationToken);
}
