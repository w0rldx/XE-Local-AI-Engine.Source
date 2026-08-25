namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

/// <summary>
///     Process-wide memo of installed-member SHA-256 digests, keyed by <c>(absolute path, length, last-write-time
///     UTC)</c>. It exists because verification re-reads every byte of every member on every acquire, and the benchmark
///     freeze acquires once per run: a ten-cell matrix of repeats re-hashed the same unchanged multi-gigabyte weight
///     file dozens of times before a single token was generated.
///     <para>
///         Keyed by the path, so one entry per member file however many times it is verified; the length and timestamp
///         ride the VALUE and a mismatch on either re-hashes and replaces. The bound is therefore the number of
///         distinct member files a process touches, capped at <see cref="MaxEntries" /> — reached only by a models
///         directory far larger than any this node manages, where the whole memo is dropped rather than half-evicted.
///     </para>
/// </summary>
/// <remarks>
///     ponytail: length + last-write-time is the standard unchanged-file heuristic (make, rsync). Ceiling: a member
///     rewritten with BOTH its length and its timestamp preserved is not re-detected for the life of the process. That
///     is an actor who already holds write access to the models directory, which is the same actor who could rewrite
///     the registry the digest is compared against. Upgrade path if that ever matters: key on the file's inode/change
///     time (<c>st_ctime</c>), which a plain rewrite cannot preserve.
/// </remarks>
internal sealed class GgufMemberHashMemo
{
    /// <summary>Distinct member files remembered before the memo is dropped whole.</summary>
    internal const int MaxEntries = 512;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Lookups answered from the memo. Test-only seam.</summary>
    internal long Hits { get; private set; }

    /// <summary>Lookups that had to hash the file. Test-only seam.</summary>
    internal long Misses { get; private set; }

    /// <summary>The remembered digest for an unchanged file, or <see langword="null" /> when it must be hashed.</summary>
    public string? TryGet(string absolutePath, long length, DateTime lastWriteTimeUtc)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(absolutePath, out var entry) && entry.Length == length && entry.LastWriteTimeUtc == lastWriteTimeUtc)
            {
                Hits++;
                return entry.Sha256;
            }

            Misses++;
            return null;
        }
    }

    /// <summary>Remembers a freshly computed digest, replacing whatever was remembered for the same path.</summary>
    public void Set(string absolutePath, long length, DateTime lastWriteTimeUtc, string sha256)
    {
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries && !_entries.ContainsKey(absolutePath))
            {
                _entries.Clear();
            }

            _entries[absolutePath] = new Entry(length, lastWriteTimeUtc, sha256);
        }
    }

    private readonly record struct Entry(long Length, DateTime LastWriteTimeUtc, string Sha256);
}
