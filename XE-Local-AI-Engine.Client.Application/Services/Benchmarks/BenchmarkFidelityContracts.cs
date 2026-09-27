namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     The identity of one base-logit cache file, and — through <see cref="Digest" /> — the identity of every KLD
///     number measured against it.
/// </summary>
/// <remarks>
///     This is the ONLY place the comparability digest is computed: the base phase names its file by it, the display
///     gate compares a stored number against it, and the disk-estimate endpoint reports it. A second copy of the
///     expression is the bug this type exists to prevent — four of its five inputs are settable or bumpable without
///     the base model's fingerprint moving, so gating on the fingerprint alone would present a number measured over
///     50 chunks of one corpus as comparable with one measured over 200 chunks of another.
/// </remarks>
public sealed record BenchmarkKldCacheKey
{
    private BenchmarkKldCacheKey(string canonicalJson, string digest)
    {
        CanonicalJson = canonicalJson;
        Digest = digest;
    }

    /// <summary>The plaintext key, written beside the cache file so a cache directory is auditable by a human.</summary>
    public string CanonicalJson { get; }

    /// <summary><c>v1:</c> + 64 lowercase hex. The comparability gate, and the source of both file names.</summary>
    public string Digest { get; }

    /// <summary>32 hex characters plus an extension.</summary>
    /// <remarks>
    ///     The digest is used rather than the key itself because a content fingerprint is <c>v1:&lt;hex&gt;</c> and
    ///     <c>:</c> is not a legal path character on Windows — where NTFS would not merely reject it but reinterpret
    ///     the tail as an alternate data stream.
    /// </remarks>
    public string FileName => string.Concat(ShortDigest, ".logits");

    public string SidecarFileName => string.Concat(ShortDigest, ".json");

    public string LockFileName => string.Concat(ShortDigest, ".logits.lock");

    private string ShortDigest => Digest.AsSpan(3, 32).ToString();

    public static BenchmarkKldCacheKey Create(string baseModelContentFingerprint, string corpusSha256, int chunks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseModelContentFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusSha256);
        var canonicalJson = BenchmarkCanonicalJson.Serialize(new
        {
            baseModelContentFingerprint,
            corpusSha256,
            contextTokens = BenchmarkFidelityPolicy.ContextTokens,
            chunks = BenchmarkFidelityPolicy.ClampChunks(chunks),
            kldFormatVersion = BenchmarkFidelityPolicy.KldFormatVersion
        });
        return new BenchmarkKldCacheKey(canonicalJson, string.Concat("v1:", BenchmarkCanonicalJson.Hash(canonicalJson)));
    }

    /// <summary>
    ///     Whether a stored KLD figure may be DISPLAYED: only while the digest it was measured under is the one the
    ///     project's current settings recompute.
    /// </summary>
    /// <remarks>
    ///     A mismatch is rendered as a stale badge, never as a number and never as a greyed or parenthesised number —
    ///     a figure the reader can still see is a figure they will still compare.
    /// </remarks>
    public static bool IsComparable(string? storedDigest, string? expectedDigest) =>
        !string.IsNullOrEmpty(storedDigest)
        && !string.IsNullOrEmpty(expectedDigest)
        && string.Equals(storedDigest, expectedDigest, StringComparison.Ordinal);
}

public sealed class BenchmarkFidelityCorpusFile
{
    public required string Path { get; init; }

    public required string Sha256 { get; init; }

    /// <summary>
    ///     <c>wikitext2-raw-test@&lt;sha256-12&gt;</c>, stored beside every perplexity number so two of them are only ever
    ///     compared when they scored the same bytes.
    /// </summary>
    public required string CorpusId { get; init; }
}

/// <summary>
///     Whether a run's KL-divergence numbers may be shown. Their own vocabulary rather than a boolean, because
///     "never measured" and "measured against something else" are different answers and the UI says different things.
/// </summary>
public static class BenchmarkFidelityKldStates
{
    public const string None = "none";
    public const string Ok = "ok";

    /// <summary>Measured, but not against what the project now expects. A badge is rendered, never a number.</summary>
    public const string Stale = "kld-stale";
}
