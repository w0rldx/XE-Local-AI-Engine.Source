namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One quant-fidelity measurement of one run — a perplexity pass, or a KL-divergence pass against a base model's
///     logits. Immutable evidence in exactly the sense <see cref="BenchmarkJudgeAttempt" /> is: re-measuring inserts a
///     new attempt rather than overwriting the previous one, so "what was this number measured against" survives a
///     corpus, chunk-count or base-model change. The run carries a denormalized projection of the LATEST succeeded
///     attempt so the listing stays a flat-column scan; this row is the audit record behind it.
/// </summary>
internal sealed record class BenchmarkFidelityAttempt
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>1..n within the run, in enqueue order.</summary>
    public int Sequence { get; set; }

    /// <summary>
    ///     <c>ppl</c> or <c>kld</c>. A KLD attempt also carries the perplexity the same pass reported, but a PPL
    ///     attempt never carries KLD columns — which pass produced a row is not inferable from its NULLs.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    public BenchmarkJudgeAttemptStatus Status { get; set; }

    /// <summary>The <c>Final estimate: PPL = X +/- Y</c> pair. Plaintext numerics, display only, never ranked.</summary>
    public double? PerplexityMean { get; set; }

    public double? PerplexityStdErr { get; set; }

    /// <summary>Chunks actually scored, and the window they were scored at — pinned to 512 so two numbers compare.</summary>
    public int? PerplexityChunks { get; set; }

    public int? PerplexityContextTokens { get; set; }

    /// <summary>The corpus identity, <c>wikitext2-raw-test@&lt;sha256-12&gt;</c>: two PPL numbers are only ever
    ///     compared when they scored the same bytes.</summary>
    public string? CorpusId { get; set; }

    public double? KldMean { get; set; }
    public double? KldP99 { get; set; }
    public double? TopTokenAgreement { get; set; }
    public string? BaseModelName { get; set; }

    /// <summary>The base model's content fingerprint. Evidence, not the comparability gate.</summary>
    public string? BaseModelContentFingerprint { get; set; }

    /// <summary>
    ///     The comparability gate: <c>v1:</c> + 64 hex over the WHOLE base-logit cache key (base fingerprint, corpus
    ///     sha, context tokens, chunks, KLD format version) — i.e. which file on disk these numbers came from. A KLD
    ///     figure is displayed only while this equals the digest the project's current settings recompute.
    /// </summary>
    public string? BaseLogitsDigest { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_fidelity_receipt_json</c>. A REDUCED evidence block, never a launch receipt: llama-perplexity
    ///     has no readiness probe, so there is no receipt to be had, and presenting one shape as the other is the
    ///     drift the display-only axes exist to prevent.
    /// </summary>
    public byte[]? ReceiptJson { get; set; }

    public string? ErrorMessage { get; set; }
    public long EnqueuedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? CompletedAtUtc { get; set; }
    public long Version { get; set; }
}
