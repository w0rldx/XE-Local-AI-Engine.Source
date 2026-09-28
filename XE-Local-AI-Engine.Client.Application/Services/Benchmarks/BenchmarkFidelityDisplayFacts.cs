namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class BenchmarkFidelityDisplayFacts
{
    public required string? ExpectedKldDigest { get; init; }

    public static BenchmarkFidelityDisplayFacts FromProject(BenchmarkProjectRecord? project) =>
        new()
        {
            ExpectedKldDigest = project is { FidelityKldEnabled: true, FidelityKldBaseFingerprint: { Length: > 0 } fingerprint }
                ? BenchmarkKldCacheKey.Create(fingerprint,
                                          BenchmarkFidelityCorpus.Require().Sha256,
                                          BenchmarkFidelityPolicy.ClampChunks(project.FidelityChunks))
                                      .Digest
                : null
        };
}
