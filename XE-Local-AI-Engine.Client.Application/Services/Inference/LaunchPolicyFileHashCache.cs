namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;

/// <summary>
///     Process-lifetime cache for exact file SHA-256 values used by launch-policy fingerprints. Cache hits are guarded
///     by stable file metadata plus small samples from the beginning, middle, and end of the file; file-system change
///     notifications evict entries even when a writer restores the original length and last-write timestamp.
/// </summary>
/// <remarks>
///     Registered as a container-owned singleton and injected: every consumer shares one instance, because each holds
///     a file-system watcher per directory and a second instance watches the same runtime directory twice.
/// </remarks>
public sealed class LaunchPolicyFileHashCache : IDisposable
{
    private const int GuardBlockSize = 16 * 1024;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(PathComparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(PathComparer);
    private readonly ConcurrentDictionary<string, Lazy<FileSystemWatcher>> _watchers = new(PathComparer);

    private long _fullHashComputationCount;
    private int _disposed;

    internal long FullHashComputationCount => Interlocked.Read(ref _fullHashComputationCount);

    public async Task<string> GetSha256Async(string filePath, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var normalizedPath = Path.GetFullPath(filePath);
        EnsureWatcher(Path.GetDirectoryName(normalizedPath)
                      ?? throw new InvalidOperationException("The fingerprinted file has no parent directory."));

        var gate = _gates.GetOrAdd(normalizedPath, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var before = await CaptureStampAsync(normalizedPath, ct).ConfigureAwait(false);
                if (_entries.TryGetValue(normalizedPath, out var cached) && cached.Stamp == before)
                {
                    return cached.Sha256;
                }

                var sha256 = await ComputeFullSha256Async(normalizedPath, ct).ConfigureAwait(false);
                var after = await CaptureStampAsync(normalizedPath, ct).ConfigureAwait(false);
                if (after != before)
                {
                    continue;
                }

                _entries[normalizedPath] = new CacheEntry(after, sha256);
                return sha256;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<string> GetGuardSha256Async(string filePath, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return GetGuardSha256CoreAsync(Path.GetFullPath(filePath), ct);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var watcher in _watchers.Values)
        {
            if (watcher.IsValueCreated)
            {
                watcher.Value.Dispose();
            }
        }

        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }
    }

    private async Task<string> ComputeFullSha256Async(string filePath, CancellationToken ct)
    {
        Interlocked.Increment(ref _fullHashComputationCount);
        await using var stream = new FileStream(filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static async Task<FileStamp> CaptureStampAsync(string filePath, CancellationToken ct)
    {
        var file = new FileInfo(filePath);
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException("The fingerprinted file no longer exists.", filePath);
        }

        return new FileStamp(file.Length,
            file.LastWriteTimeUtc.Ticks,
            file.CreationTimeUtc.Ticks,
            await GetGuardSha256CoreAsync(filePath, ct).ConfigureAwait(false));
    }

    private static async Task<string> GetGuardSha256CoreAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: GuardBlockSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var lengthBytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, stream.Length);
        hash.AppendData(lengthBytes);

        var positions = ResolveGuardPositions(stream.Length);
        var buffer = new byte[GuardBlockSize];
        foreach (var position in positions)
        {
            ct.ThrowIfCancellationRequested();
            stream.Position = position;
            var requested = (int)Math.Min(buffer.Length, stream.Length - position);
            var read = 0;
            while (read < requested)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read, requested - read), ct).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            var positionBytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(positionBytes, position);
            var readBytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(readBytes, read);
            hash.AppendData(positionBytes);
            hash.AppendData(readBytes);
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static long[] ResolveGuardPositions(long length)
    {
        if (length <= GuardBlockSize * 3L)
        {
            return [0];
        }

        return
        [
            0,
            Math.Max(0, (length / 2) - (GuardBlockSize / 2)),
            length - GuardBlockSize
        ];
    }

    private void EnsureWatcher(string directory)
    {
        var lazy = _watchers.GetOrAdd(directory,
            path => new Lazy<FileSystemWatcher>(() => CreateWatcher(path), LazyThreadSafetyMode.ExecutionAndPublication));
        _ = lazy.Value;
    }

    private FileSystemWatcher CreateWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName
                           | NotifyFilters.CreationTime
                           | NotifyFilters.LastWrite
                           | NotifyFilters.Size
        };
        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        _entries.TryRemove(Path.GetFullPath(args.FullPath), out _);
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        _entries.TryRemove(Path.GetFullPath(args.OldFullPath), out _);
        _entries.TryRemove(Path.GetFullPath(args.FullPath), out _);
    }

    private sealed record CacheEntry(FileStamp Stamp, string Sha256);

    private readonly record struct FileStamp(
        long Length,
        long LastWriteUtcTicks,
        long CreationUtcTicks,
        string GuardSha256);
}
