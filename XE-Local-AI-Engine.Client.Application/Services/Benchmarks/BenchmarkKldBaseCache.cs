namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Globalization;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

public sealed class BenchmarkKldCacheOptions
{
    public const string SectionName = "Benchmarks";

    /// <summary>
    ///     How much the base-logit cache may hold before whole files are evicted, least recently used first. A single
    ///     base model at 200 chunks is ~25 GB, so this is "two of them and a little room", not a generous allowance.
    /// </summary>
    public long KldCacheMaxBytes { get; init; } = 64L * 1024 * 1024 * 1024;
}

/// <summary>
///     The base-model logit files KL-divergence is measured against: how they are named, how one process avoids
///     writing over another's, how a partial write is never mistaken for a finished one, and when the disk says no.
///     <para>
///         Every file is named by <see cref="BenchmarkKldCacheKey.Digest" /> rather than by the base model's
///         fingerprint, because a fingerprint is <c>v1:&lt;hex&gt;</c> and <c>:</c> is not a legal path character on
///         Windows. A plaintext sidecar carries the key beside it, so the directory stays readable to a human.
///     </para>
/// </summary>
public sealed class BenchmarkKldBaseCache(IFreeSpaceProbe freeSpace, string? rootDirectory = null)
{
    private readonly IFreeSpaceProbe _freeSpace = freeSpace ?? throw new ArgumentNullException(nameof(freeSpace));
    private readonly string _root = rootDirectory ?? DefaultRoot();

    public string Root => _root;

    public string PathFor(BenchmarkKldCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Path.Combine(_root, key.FileName);
    }

    /// <summary>The finished file for this key, or <see langword="null" /> when it has not been measured yet.</summary>
    public string? TryResolveExisting(BenchmarkKldCacheKey key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    ///     Refuses when writing <paramref name="estimatedBytes" /> would leave less than the headroom free. A
    ///     multi-gigabyte write that fills the volume is a worse outcome than a refusal, and the message names both
    ///     numbers because an operator's next question is always "by how much".
    /// </summary>
    public void EnsureSpaceFor(long estimatedBytes)
    {
        Directory.CreateDirectory(_root);
        var free = _freeSpace.GetAvailableFreeBytes(_root);
        if (free - estimatedBytes >= BenchmarkFidelityPolicy.KldFreeSpaceHeadroomBytes)
        {
            return;
        }

        var needed = Gigabytes(estimatedBytes);
        var available = Gigabytes(free);
        var headroom = Gigabytes(BenchmarkFidelityPolicy.KldFreeSpaceHeadroomBytes);
        throw new BenchmarkExecutionException(string.Create(CultureInfo.InvariantCulture,
            $"Measuring KL divergence needs about {needed} GB of base logits, only {available} GB is free, and {headroom} GB must remain afterwards. Free space, lower the chunk count, or clear the fidelity cache."));
    }

    /// <summary>
    ///     Takes the per-key write lease, or returns <see langword="null" /> when another process holds it.
    ///     <para>
    ///         <see cref="FileOptions.DeleteOnClose" /> is what makes this correct across a crash: the OS drops the
    ///         handle when the process dies, so the next caller's <see cref="FileMode.CreateNew" /> succeeds instead of
    ///         finding a stale lock nobody will ever release. That crashed-predecessor case is precisely the one a
    ///         bare "does the file exist" check gets wrong.
    ///     </para>
    /// </summary>
    public FileStream? TryAcquireLease(BenchmarkKldCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Directory.CreateDirectory(_root);
        try
        {
            return new FileStream(Path.Combine(_root, key.LockFileName),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            // Held by a live writer. The caller polls the finished file rather than writing a second copy.
            return null;
        }
    }

    /// <summary>
    ///     The temp path the base phase writes to. Named per invocation, so two writers that somehow both got here
    ///     cannot interleave into one file, and so a leftover from a killed run is identifiable as garbage.
    /// </summary>
    public string TempPathFor(BenchmarkKldCacheKey key, Guid invocationId)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Path.Combine(_root, string.Create(CultureInfo.InvariantCulture, $"{key.FileName}.tmp.{invocationId:N}"));
    }

    /// <summary>
    ///     Moves a finished temp file into place and writes the plaintext sidecar beside it. The move is
    ///     same-directory, so it is atomic on every filesystem this app supports: a reader never observes a partial
    ///     logit file, which is the whole reason the write does not go to the final path directly.
    /// </summary>
    public void Publish(BenchmarkKldCacheKey key, string tempPath)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        var final = PathFor(key);
        File.Move(tempPath, final, overwrite: false);
        File.WriteAllText(Path.Combine(_root, key.SidecarFileName), key.CanonicalJson);
    }

    public static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is swept by the next retention pass. Failing the measurement over it would turn a
            // tidying problem into a lost result.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>
    ///     Evicts whole least-recently-used files until the cache fits under <paramref name="maximumBytes" />. A file
    ///     whose lease is held is never evicted, and neither is one <paramref name="inUseDigests" /> names — a
    ///     queued or running measurement is about to read it.
    /// </summary>
    public long Trim(long maximumBytes, IReadOnlySet<string> inUseDigests)
    {
        ArgumentNullException.ThrowIfNull(inUseDigests);
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        var files = new DirectoryInfo(_root).GetFiles("*.logits")
                                            .OrderBy(file => file.LastAccessTimeUtc)
                                            .ToList();
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= maximumBytes)
            {
                break;
            }

            // The file is named by the digest's FIRST 32 hex characters, after the "v1:" prefix — so the match is a
            // slice, not a suffix. A suffix test would never match and eviction would happily delete a file a queued
            // measurement is on its way to reading.
            var shortDigest = Path.GetFileNameWithoutExtension(file.Name);
            if (inUseDigests.Any(digest => digest.Length >= 35 && digest.AsSpan(3, 32).SequenceEqual(shortDigest))
                || File.Exists(Path.Combine(_root, string.Concat(file.Name, ".lock"))))
            {
                continue;
            }

            total -= file.Length;
            DeleteBestEffort(file.FullName);
            DeleteBestEffort(Path.Combine(_root, string.Concat(shortDigest, ".json")));
        }

        return total;
    }

    /// <summary>Deletes every cached base file. Refused by the caller while any fidelity work item is live.</summary>
    public void Clear()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_root).Where(static path => !path.EndsWith(".lock", StringComparison.Ordinal)))
        {
            DeleteBestEffort(file);
        }
    }

    public long TotalBytes() =>
        Directory.Exists(_root)
            ? new DirectoryInfo(_root).GetFiles("*.logits").Sum(file => file.Length)
            : 0;

    public long AvailableFreeBytes()
    {
        Directory.CreateDirectory(_root);
        return _freeSpace.GetAvailableFreeBytes(_root);
    }

    private static double Gigabytes(long bytes) =>
        Math.Round(bytes / 1_000_000_000.0, 1);

    /// <summary>
    ///     Under the same machine-global base the llama.cpp binaries and the compute runtime use, so one measurement
    ///     serves every node profile on the box and the existing uninstaller sweep already reaches it.
    /// </summary>
    private static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine",
            "benchmarks",
            "kld-base");
}
