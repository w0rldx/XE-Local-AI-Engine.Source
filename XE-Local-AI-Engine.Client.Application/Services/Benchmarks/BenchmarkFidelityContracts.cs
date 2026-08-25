namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Globalization;
using System.Security.Cryptography;

/// <summary>
///     The constants the quant-fidelity axis is measured under. They are constants rather than settings because a
///     perplexity number is only comparable to another one measured the same way, and the ones that CAN move
///     (<see cref="DefaultChunks" />) are inside the KLD comparability digest so a change is visible rather than
///     silent.
/// </summary>
public static class BenchmarkFidelityPolicy
{
    /// <summary>
    ///     The perplexity window, pinned. Perplexity is only comparable at a fixed window, and every published
    ///     llama.cpp / Unsloth / bartowski number uses 512. The run's frozen placement, KV-cache type and flash-attn
    ///     setting ARE replayed — those are what differ between the runs being compared; the window is not.
    /// </summary>
    public const int ContextTokens = 512;

    /// <summary>~102k tokens of prompt evaluation: about a minute on a 27B Q4_K_M, and enough to separate two quants.</summary>
    public const int DefaultChunks = 200;

    public const int MinimumChunks = 50;

    /// <summary>The whole wikitext-2-raw test split at a 512 window.</summary>
    public const int MaximumChunks = 655;

    /// <summary>
    ///     Bumped when the meaning of a stored KLD number changes for a reason no operator setting captures — a
    ///     llama.cpp logit-file format change, or a change to how this code drives the two phases. It is inside the
    ///     comparability digest, so a bump renders every previously measured figure stale rather than comparing it
    ///     against numbers it no longer means the same thing as.
    /// </summary>
    public const int KldFormatVersion = 1;

    /// <summary>
    ///     Bytes per logit in llama.cpp's KL-divergence base file. MEASURED, not derived from the format: a real
    ///     10-chunk base file for Qwen3.8-27B (n_vocab 151 936) on an RTX 5090 came to 1 266 472 900 bytes over
    ///     777 912 320 logits, i.e. <b>1.628</b> — llama.cpp does not store a bare f16 per logit, so the format's
    ///     2.0 would have promised an operator 31.1 GB where the file is 25.3 GB. The constant carries ~7 % headroom
    ///     over the measurement because it is an ESTIMATE shown before a multi-gigabyte write, and the free-space
    ///     reservation below is what actually stops the write.
    /// </summary>
    public const double KldBytesPerLogit = 1.75;

    /// <summary>The fixed part of the base file — its header and per-chunk bookkeeping.</summary>
    public const long KldHeaderBytes = 1024;

    /// <summary>
    ///     Free space that must remain AFTER the base write. A multi-gigabyte write that fills the disk to 100% is a
    ///     worse outcome than a refusal an operator can act on.
    /// </summary>
    public const long KldFreeSpaceHeadroomBytes = 10L * 1024 * 1024 * 1024;

    public static int ClampChunks(int? chunks) =>
        chunks is not { } value ? DefaultChunks : Math.Clamp(value, MinimumChunks, MaximumChunks);

    /// <summary>An upper bound on the base-logit file for a model of this vocabulary at this chunk count.</summary>
    public static long EstimateKldBytes(int chunks, int vocabSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vocabSize);
        return KldHeaderBytes + (long)Math.Ceiling(chunks * (double)ContextTokens * vocabSize * KldBytesPerLogit);
    }
}

/// <summary>
///     The identity of one base-logit cache file, and — through <see cref="Digest" /> — the identity of every KLD
///     number measured against it.
///     <para>
///         This is the ONLY place the comparability digest is computed. The base phase names its file by it, the
///         display gate compares a stored number against it, and the disk-estimate endpoint reports it. A second copy
///         of the expression is the bug this type exists to prevent: four of its five inputs are settable or bumpable
///         without the base model's fingerprint moving, so gating on the fingerprint alone would present a number
///         measured over 50 chunks of one corpus as comparable with one measured over 200 chunks of another.
///     </para>
/// </summary>
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

    /// <summary>
    ///     32 hex characters plus an extension. The digest is used rather than the key itself because a content
    ///     fingerprint is <c>v1:&lt;hex&gt;</c> and <c>:</c> is not a legal path character on Windows — where NTFS
    ///     would not merely reject it but reinterpret the tail as an alternate data stream.
    /// </summary>
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
    ///     project's current settings recompute. A mismatch is rendered as a stale badge, never as a number and never
    ///     as a greyed or parenthesised number — a figure the reader can still see is a figure they will still compare.
    /// </summary>
    public static bool IsComparable(string? storedDigest, string? expectedDigest) =>
        !string.IsNullOrEmpty(storedDigest)
        && !string.IsNullOrEmpty(expectedDigest)
        && string.Equals(storedDigest, expectedDigest, StringComparison.Ordinal);
}

/// <summary>
///     The wikitext-2-raw test split shipped with the app, and the identity two perplexity numbers must share before
///     they may be compared. Resolved once per process: the file is 1.3 MB and its hash never changes for a build.
/// </summary>
public static class BenchmarkFidelityCorpus
{
    /// <summary>The name the corpus is linked under in the publish output (see the Client csproj).</summary>
    private const string PublishedDirectoryName = "benchmark-corpus";

    private const string RepositoryRelativePath = "tools/benchmark/corpus";
    private const string FileName = "wikitext2-raw-test.txt";
    private const string CorpusName = "wikitext2-raw-test";

    private static readonly Lazy<BenchmarkFidelityCorpusFile> Resolved = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Throws when the corpus did not ship — a fidelity measurement with no corpus is not a measurement.</summary>
    public static BenchmarkFidelityCorpusFile Require() =>
        Resolved.Value;

    /// <summary>Test seam: hashes an arbitrary file the same way, so a fixture corpus carries a real identity too.</summary>
    public static BenchmarkFidelityCorpusFile Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new BenchmarkFidelityCorpusFile(path, sha256, string.Create(CultureInfo.InvariantCulture, $"{CorpusName}@{sha256[..12]}"));
    }

    private static BenchmarkFidelityCorpusFile Load()
    {
        var published = Path.Combine(AppContext.BaseDirectory, PublishedDirectoryName, FileName);
        if (File.Exists(published))
        {
            return Read(published);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, RepositoryRelativePath, FileName);
            if (File.Exists(candidate))
            {
                return Read(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("The benchmark perplexity corpus did not ship with this build.", published);
    }
}

/// <param name="CorpusId">
///     <c>wikitext2-raw-test@&lt;sha256-12&gt;</c>, stored beside every perplexity number so two of them are only ever
///     compared when they scored the same bytes.
/// </param>
public sealed record BenchmarkFidelityCorpusFile(string Path, string Sha256, string CorpusId);
