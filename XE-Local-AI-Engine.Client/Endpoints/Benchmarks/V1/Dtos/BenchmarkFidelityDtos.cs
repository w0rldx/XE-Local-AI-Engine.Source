namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

public class BenchmarkFidelityResponse
{
    /// <summary><c>queued</c>, <c>running</c>, <c>succeeded</c>, <c>failed</c>, <c>cancelled</c> or <c>skipped</c>.</summary>
    public required string Status { get; init; }

    public Guid? AttemptId { get; init; }
    public double? PerplexityMean { get; init; }
    public double? PerplexityStdErr { get; init; }
    public int? PerplexityChunks { get; init; }

    /// <summary>The window perplexity was measured at — pinned to 512, so two numbers are comparable.</summary>
    public int? PerplexityContextTokens { get; init; }

    /// <summary><c>wikitext2-raw-test@&lt;sha256-12&gt;</c>: two perplexity numbers compare only when this matches.</summary>
    public string? PerplexityCorpusId { get; init; }

    /// <summary>
    ///     <c>none</c> when this run has no KL-divergence measurement, <c>ok</c> when its numbers are comparable
    ///     against the project's current settings, and <c>kld-stale</c> when they are not.
    ///     <para>
    ///         When it is <c>kld-stale</c>, the three KLD fields below are NULL and the client renders a badge. They
    ///         are withheld rather than sent for the client to grey out, because a number a reader can still see is a
    ///         number they will still compare — and a figure measured over a different corpus, chunk count or base
    ///         model means something different from the one beside it.
    ///     </para>
    /// </summary>
    public required string KldState { get; init; }

    public double? KldMean { get; init; }
    public double? KldP99 { get; init; }

    /// <summary>How often the quant's most likely token is the base model's, as a 0..1 fraction.</summary>
    public double? TopTokenAgreement { get; init; }

    /// <summary>The base model's content fingerprint. Evidence, NOT the comparability gate.</summary>
    public string? KldBaseFingerprint { get; init; }

    /// <summary>Operator-safe reason for a failed measurement.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>One immutable fidelity measurement, as the attempt history serves it.</summary>
public class BenchmarkFidelityAttemptResponse
{
    public Guid Id { get; init; }
    public int Sequence { get; init; }

    /// <summary><c>ppl</c> or <c>kld</c>.</summary>
    public required string Kind { get; init; }

    public required string Status { get; init; }
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

    /// <summary>The digest this measurement's comparability is judged by.</summary>
    public string? BaseLogitsDigest { get; init; }

    public string? ErrorMessage { get; init; }
    public long EnqueuedAtUtc { get; init; }
    public long? StartedAtUtc { get; init; }
    public long? CompletedAtUtc { get; init; }
}

public class ListBenchmarkFidelityAttemptsRequest
{
    public Guid RunId { get; init; }
}

public class ListBenchmarkFidelityAttemptsResponse
{
    public required IReadOnlyList<BenchmarkFidelityAttemptResponse> Items { get; init; }
}

public class StartRunFidelityRequest
{
    public Guid RunId { get; init; }
}

public class GetKldDiskEstimateRequest
{
    public Guid ProjectId { get; init; }

    /// <summary>Chunks to estimate for, or null for the project's setting. Clamped to the measurable range.</summary>
    public int? Chunks { get; init; }
}

/// <summary>
///     What enabling KL divergence will cost on disk, shown BEFORE the operator commits to a multi-gigabyte write.
/// </summary>
public class GetKldDiskEstimateResponse
{
    public long EstimatedBytes { get; init; }
    public long FreeDiskBytes { get; init; }

    /// <summary>What the base-logit cache already holds.</summary>
    public long CachedBytes { get; init; }

    public int Chunks { get; init; }
    public int ContextTokens { get; init; }

    /// <summary>The vocabulary the estimate assumes — the largest among supported families, so it errs high.</summary>
    public int VocabSize { get; init; }

    /// <summary>The estimate in words, so the number is checkable rather than magic.</summary>
    public required string Formula { get; init; }

    /// <summary>False when the write would leave less than the required headroom free.</summary>
    public bool FitsOnDisk { get; init; }
}
