namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     One succeeded fidelity measurement. <see cref="ReceiptJson" /> contains the reduced execution evidence;
///     llama-perplexity has no readiness probe and therefore produces no launch receipt.
/// </summary>
public sealed record BenchmarkFidelitySuccessCommand
{
    public required Guid RunId { get; init; }

    public required long ExpectedWorkVersion { get; init; }

    public required Guid FidelityAttemptId { get; init; }

    public double? PerplexityMean { get; init; }

    public double? PerplexityStdErr { get; init; }

    public int? PerplexityChunks { get; init; }

    public int? PerplexityContextTokens { get; init; }

    public string? CorpusId { get; init; }

    public double? KldMean { get; init; }

    public double? KldP99 { get; init; }

    public double? TopTokenAgreement { get; init; }

    public string? BaseModelName { get; init; }

    public string? BaseModelContentFingerprint { get; init; }

    public string? BaseLogitsDigest { get; init; }

    public ReadOnlyMemory<byte> ReceiptJson { get; init; }
}

public sealed class BenchmarkProjectFidelityInput
{
    public required bool FidelityEnabled { get; init; }

    public required bool FidelityKldEnabled { get; init; }

    public required int? FidelityChunks { get; init; }

    public required string? FidelityKldBaseModelName { get; init; }

    /// <summary>
    ///     Resolved by the service from the eligible-model catalog, never by a caller: it is an input to the KLD
    ///     comparability digest, so a supplied value could make numbers measured against different weights compare equal.
    /// </summary>
    public required string? FidelityKldBaseFingerprint { get; init; }
}

public sealed class BenchmarkProjectFidelityChange
{
    public required BenchmarkProjectRecord Project { get; init; }

    /// <summary>The runs a <c>measureExisting</c> write queued a measurement for; empty otherwise.</summary>
    public required IReadOnlyList<Guid> EnqueuedRunIds { get; init; }
}
