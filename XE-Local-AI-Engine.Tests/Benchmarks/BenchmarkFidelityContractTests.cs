namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The quant-fidelity axis is display-only, which makes its comparability contract the whole of its correctness:
///     a perplexity or KL-divergence number next to another one is a claim that both were measured the same way. These
///     cover the two identities that claim rests on — the corpus id and the base-logit digest — and the file naming
///     that has to survive a filesystem that forbids the character a content fingerprint starts with.
/// </summary>
public sealed class BenchmarkFidelityContractTests
{
    private const string Qwen3Fingerprint = "v1:" + "ab12cd34" + "00000000000000000000000000000000000000000000000000000000";
    private const string CorpusSha = "173c87a53759e0201f33e0ccf978e510c2042d7f2cb78229d9a50d79b9e7dd08";
    private const int Qwen3VocabSize = 151_936;

    [Test]
    public void EstimateKldBytes_ForQwen3Vocab_UsesTheMeasuredBytesPerLogit()
    {
        var estimate = BenchmarkFidelityPolicy.EstimateKldBytes(chunks: 200, Qwen3VocabSize);

        var expected = BenchmarkFidelityPolicy.KldHeaderBytes
                       + (long)Math.Ceiling(200 * (double)BenchmarkFidelityPolicy.ContextTokens * Qwen3VocabSize * BenchmarkFidelityPolicy.KldBytesPerLogit);
        AssertEx.Equal(expected, estimate);

        // A real 10-chunk file for this vocabulary was 1 266 472 900 bytes, so 200 chunks is ~25.3 GB. The estimate
        // must sit ABOVE that (it gates a write) but nowhere near the format-derived 2 B/logit's 31.1 GB, which is the
        // number this constant was corrected away from.
        const long measuredAt200Chunks = 1_266_472_900L * 20;
        AssertEx.True(estimate > measuredAt200Chunks, $"The estimate must not promise less than the file costs; got {estimate}.");
        AssertEx.True(estimate < (long)(measuredAt200Chunks * 1.15), $"The estimate must not overstate the file by more than 15 %; got {estimate}.");
    }

    /// <summary>
    ///     A content fingerprint is <c>v1:&lt;hex&gt;</c>, and <c>:</c> is not a legal path character on Windows —
    ///     where NTFS does not reject it but reinterprets the tail as an alternate data stream, so the failure would be
    ///     a silently empty file rather than an error. The cache is therefore named by a digest OF the key.
    /// </summary>
    [Test]
    public void CacheKeyFileNames_CarryNoFingerprintAndNoIllegalPathCharacter()
    {
        var key = BenchmarkKldCacheKey.Create(Qwen3Fingerprint, CorpusSha, chunks: 200);

        foreach (var name in new[] { key.FileName, key.SidecarFileName, key.LockFileName })
        {
            AssertEx.False(name.Contains(':', StringComparison.Ordinal), $"A cache file name must not contain a colon; got {name}.");
            AssertEx.False(name.Contains(Qwen3Fingerprint, StringComparison.Ordinal), $"A cache file name must not embed the fingerprint; got {name}.");
            AssertEx.Empty(name.Where(character => Path.GetInvalidFileNameChars().Contains(character)));
        }

        AssertEx.True(key.FileName.EndsWith(".logits", StringComparison.Ordinal));
        AssertEx.Equal(expected: 39, key.FileName.Length, "32 hex characters plus '.logits'.");
        AssertEx.True(key.Digest.StartsWith("v1:", StringComparison.Ordinal));
        AssertEx.Equal(expected: 67, key.Digest.Length, "'v1:' plus 64 hex is 67 — the width both stored digest columns declare.");

        // The sidecar is the plaintext key, so a human browsing the cache directory can answer "what is this file".
        AssertEx.True(key.CanonicalJson.Contains(CorpusSha, StringComparison.Ordinal), "The auditable key must name the corpus it was measured over.");
        AssertEx.True(key.CanonicalJson.Contains("\"chunks\":200", StringComparison.Ordinal), "The auditable key must name the chunk count.");
    }

    /// <summary>
    ///     The finding this type exists for: four of the digest's five inputs move without the base model's
    ///     fingerprint moving, and <c>kld_p99</c> in particular is strongly chunk-count dependent. Gating display on
    ///     the fingerprint alone would present numbers measured over 50 chunks as comparable with numbers measured
    ///     over 200.
    /// </summary>
    [Test]
    public void CacheKeyDigest_MovesWithEveryInput_NotOnlyWithTheBaseFingerprint()
    {
        var baseline = BenchmarkKldCacheKey.Create(Qwen3Fingerprint, CorpusSha, chunks: 200);

        var otherModel = BenchmarkKldCacheKey.Create("v1:" + new string('9', 64), CorpusSha, chunks: 200);
        var otherChunks = BenchmarkKldCacheKey.Create(Qwen3Fingerprint, CorpusSha, chunks: 50);
        var otherCorpus = BenchmarkKldCacheKey.Create(Qwen3Fingerprint, new string('0', 64), chunks: 200);

        AssertEx.False(BenchmarkKldCacheKey.IsComparable(baseline.Digest, otherModel.Digest), "A different base model is not comparable.");
        AssertEx.False(BenchmarkKldCacheKey.IsComparable(baseline.Digest, otherChunks.Digest), "A different chunk count is not comparable — the gate v2 missed.");
        AssertEx.False(BenchmarkKldCacheKey.IsComparable(baseline.Digest, otherCorpus.Digest), "A different corpus is not comparable.");
        AssertEx.True(BenchmarkKldCacheKey.IsComparable(baseline.Digest, BenchmarkKldCacheKey.Create(Qwen3Fingerprint, CorpusSha, chunks: 200).Digest),
            "The same inputs must recompute the same digest, or nothing is ever displayable.");

        // A missing digest is not a match. An unmeasured run and a stale one both render a badge, never a number.
        AssertEx.False(BenchmarkKldCacheKey.IsComparable(storedDigest: null, baseline.Digest));
        AssertEx.False(BenchmarkKldCacheKey.IsComparable(baseline.Digest, expectedDigest: null));
        AssertEx.False(BenchmarkKldCacheKey.IsComparable(storedDigest: null, expectedDigest: null), "Two absences are not a comparability claim.");
    }

    [Test]
    public void CacheKeyDigest_ChangesWithTheFormatVersion()
    {
        // The one input no operator touches but a release does. Asserted through the constant rather than by rewriting
        // it: if a bump ever stops moving the digest, every previously measured figure would silently stay displayed.
        var canonical = BenchmarkKldCacheKey.Create(Qwen3Fingerprint, CorpusSha, chunks: 200).CanonicalJson;

        AssertEx.True(canonical.Contains($"\"kldFormatVersion\":{BenchmarkFidelityPolicy.KldFormatVersion}", StringComparison.Ordinal),
            $"The format version must be inside the hashed key; got {canonical}.");
    }

    [Test]
    public void ClampChunks_KeepsTheOperatorInsideTheMeasurableRange()
    {
        AssertEx.Equal(BenchmarkFidelityPolicy.DefaultChunks, BenchmarkFidelityPolicy.ClampChunks(null));
        AssertEx.Equal(BenchmarkFidelityPolicy.MinimumChunks, BenchmarkFidelityPolicy.ClampChunks(1));
        AssertEx.Equal(BenchmarkFidelityPolicy.MaximumChunks, BenchmarkFidelityPolicy.ClampChunks(100_000));
        AssertEx.Equal(expected: 123, BenchmarkFidelityPolicy.ClampChunks(123));
    }

    /// <summary>
    ///     The corpus ships, and its identity is the hash of the bytes that were actually scored — not its file name,
    ///     which would let a replaced file pass as the same corpus.
    /// </summary>
    [Test]
    public void Corpus_ShipsWithTheBuildAndCarriesAContentIdentity()
    {
        var corpus = BenchmarkFidelityCorpus.Require();

        AssertEx.True(File.Exists(corpus.Path), $"The perplexity corpus must ship; looked at {corpus.Path}.");
        AssertEx.Equal(CorpusSha, corpus.Sha256, "The shipped corpus is the standard wikitext-2-raw test split.");
        AssertEx.Equal($"wikitext2-raw-test@{CorpusSha[..12]}", corpus.CorpusId);
        AssertEx.True(new FileInfo(corpus.Path).Length > 1_000_000, "The test split is ~1.29 MB; a truncated one would silently shorten every measurement.");
    }

    /// <summary>
    ///     The comparability digest is computed in exactly one expression. A second copy is the bug the whole design
    ///     note is about: two expressions drift, and the one on the display path is the one that would keep showing a
    ///     number that no longer means what the reader thinks.
    /// </summary>
    [Test]
    public void KldDigest_IsComputedByExactlyOneExpression()
    {
        string[] projects =
        [
            "XE-Local-AI-Engine.Client.Application",
            "XE-Local-AI-Engine.Client",
            "XE-Local-AI-Engine.Client.Persistence"
        ];
        var offenders = projects
                        .Select(project => RepositoryPaths.Combine(project))
                        .Where(Directory.Exists)
                        .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                        .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                              && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                        .Where(path => File.ReadAllText(path).Contains("kldFormatVersion", StringComparison.Ordinal))
                        .Select(path => Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/'))
                        .Order(StringComparer.Ordinal)
                        .ToArray();

        AssertEx.Equal(expected: 1, offenders.Length,
            $"Exactly one file may build the KLD comparability key; found [{string.Join(", ", offenders)}].");
        AssertEx.True(offenders[0].EndsWith("BenchmarkFidelityContracts.cs", StringComparison.Ordinal), $"That file is BenchmarkKldCacheKey's; found {offenders[0]}.");
    }
}
