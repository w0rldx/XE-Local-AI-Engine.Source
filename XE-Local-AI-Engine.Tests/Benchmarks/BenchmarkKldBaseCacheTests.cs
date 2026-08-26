namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The base-logit cache holds tens of gigabytes per base model, so its four dangerous moments each get a test: a
///     second writer, a killed writer, a full disk, and an eviction pass that must not delete a file a queued
///     measurement is about to read.
/// </summary>
public sealed class BenchmarkKldBaseCacheTests : IDisposable
{
    private static readonly string Fingerprint = "v1:" + new string('a', 64);
    private static readonly string CorpusSha = new('b', 64);
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void TryAcquireLease_WhileAnotherWriterHoldsIt_ReturnsNullInsteadOfWritingASecondCopy()
    {
        var cache = Cache(long.MaxValue);
        var key = Key();

        using var first = AssertEx.NotNull(cache.TryAcquireLease(key));
        var second = cache.TryAcquireLease(key);

        AssertEx.Null(second, "A second writer must not start a parallel multi-gigabyte write of the same file.");
    }

    /// <summary>
    ///     The crash case, which a bare "does the lock file exist" check gets exactly wrong: the OS drops the handle
    ///     when the holder dies, so <c>DeleteOnClose</c> releases the lease with it and the next caller takes over.
    /// </summary>
    [Test]
    public void TryAcquireLease_AfterTheHolderIsGone_SucceedsAgain()
    {
        var cache = Cache(long.MaxValue);
        var key = Key();

        var first = cache.TryAcquireLease(key);
        AssertEx.NotNull(first);
        first!.Dispose();

        using var second = cache.TryAcquireLease(key);
        AssertEx.NotNull(second, "A lease nobody holds must not block the next measurement forever.");
    }

    [Test]
    public void Publish_MovesTheTempFileAtomicallyAndWritesTheAuditableSidecar()
    {
        var cache = Cache(long.MaxValue);
        var key = Key();
        var temp = cache.TempPathFor(key, Guid.NewGuid());
        Directory.CreateDirectory(cache.Root);
        File.WriteAllText(temp, "logits");

        cache.Publish(key, temp);

        AssertEx.True(File.Exists(cache.PathFor(key)), "The finished file must land under the key's own name.");
        AssertEx.False(File.Exists(temp), "The temp file is consumed by the move, not copied.");
        AssertEx.Equal(key.CanonicalJson, File.ReadAllText(Path.Combine(cache.Root, key.SidecarFileName)),
            "The sidecar carries the plaintext key, so a cache directory stays readable to a human.");
    }

    /// <summary>
    ///     A killed base phase leaves a <c>.tmp</c>, never a <c>.logits</c>. That is the whole reason the write does
    ///     not go to the final path: a partial logit file that LOOKS finished would be read as a measurement.
    /// </summary>
    [Test]
    public void PartialWrite_IsNeverPublished()
    {
        var cache = Cache(long.MaxValue);
        var key = Key();
        var temp = cache.TempPathFor(key, Guid.NewGuid());
        Directory.CreateDirectory(cache.Root);
        File.WriteAllText(temp, "half a file");

        // The writer dies here — the executor's finally sweeps the temp and never calls Publish.
        BenchmarkKldBaseCache.DeleteBestEffort(temp);

        AssertEx.Null(cache.TryResolveExisting(key), "A killed base phase must leave nothing that resolves as a measurement.");
        AssertEx.Empty(Directory.EnumerateFiles(cache.Root, "*.logits"));
    }

    [Test]
    public void EnsureSpaceFor_WhenTheWriteWouldFillTheVolume_RefusesAndNamesBothNumbers()
    {
        var cache = Cache(freeBytes: 12L * 1024 * 1024 * 1024);

        var refusal = AssertEx.Throws<BenchmarkExecutionException>(() => cache.EnsureSpaceFor(5L * 1024 * 1024 * 1024));

        AssertEx.True(refusal.Message.Contains("5.4 GB", StringComparison.Ordinal), $"The refusal must name what it needs; got {refusal.Message}.");
        AssertEx.True(refusal.Message.Contains("12.9 GB", StringComparison.Ordinal), $"...and what is free; got {refusal.Message}.");

        // Comfortably inside the headroom: the guard exists to stop a 100 %-full volume, not to stop measuring.
        cache.EnsureSpaceFor(1L * 1024 * 1024 * 1024);
    }

    [Test]
    public void Trim_EvictsLeastRecentlyUsedFiles_ButNeverOneAQueuedMeasurementWillRead()
    {
        var cache = Cache(long.MaxValue);
        var oldest = Key(chunks: 50);
        var middle = Key(chunks: 100);
        var newest = Key(chunks: 200);
        Directory.CreateDirectory(cache.Root);
        foreach (var (key, accessed) in new[]
                 {
                     (oldest, DateTime.UtcNow.AddHours(-3)),
                     (middle, DateTime.UtcNow.AddHours(-2)),
                     (newest, DateTime.UtcNow.AddHours(-1))
                 })
        {
            File.WriteAllBytes(cache.PathFor(key), new byte[1000]);
            File.SetLastAccessTimeUtc(cache.PathFor(key), accessed);
        }

        // The oldest is the one a queued attempt names, so the next-oldest must be evicted in its place.
        var remaining = cache.Trim(maximumBytes: 2500, new HashSet<string>(StringComparer.Ordinal)
        {
            oldest.Digest
        });

        AssertEx.True(File.Exists(cache.PathFor(oldest)), "A file a queued measurement is about to read must survive eviction.");
        AssertEx.False(File.Exists(cache.PathFor(middle)), "The next-least-recently-used file is evicted instead.");
        AssertEx.True(File.Exists(cache.PathFor(newest)));
        AssertEx.Equal(expected: 2000L, remaining);
    }

    [Test]
    public void Clear_RemovesEveryCachedFile()
    {
        var cache = Cache(long.MaxValue);
        Directory.CreateDirectory(cache.Root);
        File.WriteAllBytes(cache.PathFor(Key()), new byte[10]);
        File.WriteAllBytes(cache.PathFor(Key(chunks: 100)), new byte[10]);

        cache.Clear();

        AssertEx.Equal(expected: 0L, cache.TotalBytes());
    }

    private BenchmarkKldBaseCache Cache(long freeBytes) =>
        new(new StubFreeSpace(freeBytes), _root);

    private static BenchmarkKldCacheKey Key(int chunks = 200) =>
        BenchmarkKldCacheKey.Create(Fingerprint, CorpusSha, chunks);

    private sealed class StubFreeSpace(long freeBytes) : IFreeSpaceProbe
    {
        public long GetAvailableFreeBytes(string path) =>
            freeBytes;
    }
}
