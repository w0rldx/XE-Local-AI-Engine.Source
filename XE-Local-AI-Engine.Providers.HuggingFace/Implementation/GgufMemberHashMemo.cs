namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

/// <summary>
///     Process-wide memo of installed-member SHA-256 digests, keyed by <c>(absolute path, length, last-write-time UTC)</c>.
/// </summary>
/// <remarks>
///     Verification re-reads every byte of every member on every acquire, and the benchmark freeze acquires once per run, so a ten-cell
///     matrix re-hashes one unchanged multi-gigabyte weight dozens of times before a single token. ponytail: length + last-write-time is
///     the standard unchanged-file heuristic. Ceiling: a member rewritten with BOTH preserved is never re-detected in-process — but that
///     actor already has write access to the models directory, and could equally rewrite the registry it is compared against. Upgrade
///     path: key on inode/change time (<c>st_ctime</c>), which a plain rewrite cannot preserve.
/// </remarks>
internal sealed class GgufMemberHashMemo
{
    /// <summary>Distinct member files remembered before the memo is dropped whole.</summary>
    /// <remarks>
    ///     Keyed by the path, so one entry per member file however many times it is verified; the length and timestamp
    ///     ride the VALUE and a mismatch on either re-hashes and replaces. The bound is therefore the number of distinct
    ///     member files a process touches — reached only by a models directory far larger than any this node manages,
    ///     where the whole memo is dropped rather than half-evicted.
    /// </remarks>
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
