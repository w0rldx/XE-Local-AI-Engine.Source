namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     One succeeded fidelity measurement. <paramref name="ReceiptJson" /> contains the reduced execution evidence;
///     llama-perplexity has no readiness probe and therefore produces no launch receipt.
/// </summary>
public sealed record BenchmarkFidelitySuccessCommand(
    Guid RunId,
    long ExpectedWorkVersion,
    Guid FidelityAttemptId,
    double? PerplexityMean = null,
    double? PerplexityStdErr = null,
    int? PerplexityChunks = null,
    int? PerplexityContextTokens = null,
    string? CorpusId = null,
    double? KldMean = null,
    double? KldP99 = null,
    double? TopTokenAgreement = null,
    string? BaseModelName = null,
    string? BaseModelContentFingerprint = null,
    string? BaseLogitsDigest = null,
    ReadOnlyMemory<byte> ReceiptJson = default);

/// <param name="FidelityKldBaseFingerprint">
///     Resolved by the service from the eligible-model catalog, never by a caller: it is an input to the KLD
///     comparability digest, so a supplied value could make numbers measured against different weights compare equal.
/// </param>
public sealed record BenchmarkProjectFidelityInput(
    bool FidelityEnabled,
    bool FidelityKldEnabled,
    int? FidelityChunks,
    string? FidelityKldBaseModelName,
    string? FidelityKldBaseFingerprint);

/// <param name="EnqueuedRunIds">The runs a <c>measureExisting</c> write queued a measurement for; empty otherwise.</param>
public sealed record BenchmarkProjectFidelityChange(BenchmarkProjectRecord Project, IReadOnlyList<Guid> EnqueuedRunIds);
