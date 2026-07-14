namespace XE_Local_AI_Engine.Client.Hosting;

/// <summary>
///     A process-lifetime, per-data-root exclusive lease. Two concurrent first launches sharing one per-user data
///     directory each generate and persist their own operator key and DB, so one process's encrypted writes become
///     unreadable after a restart under the other's key. Acquiring this lease BEFORE any key/DB initialization makes a
///     second instance fail fast instead of silently splitting the encryption key.
///     <para>
///         The primitive is a lease file inside the data directory opened with <see cref="FileShare.None" /> and held
///         open for the process lifetime. On .NET, <see cref="FileShare.None" /> takes an OS-level exclusive lock (an
///         exclusive <c>flock</c> on *nix, a share-mode denial on Windows), so a second open from any process on the
///         machine fails with <see cref="IOException" />. The OS releases the lock when the handle closes — including on
///         a crash — so no stale-lease recovery protocol is needed.
///     </para>
/// </summary>
internal sealed class SingleInstanceLease : IDisposable
{
    /// <summary>The per-user data-directory file name whose exclusive open serializes instances (contents are irrelevant).</summary>
    internal const string LeaseFileName = "instance.lock";

    private FileStream? _handle;

    private SingleInstanceLease(FileStream handle)
    {
        _handle = handle;
    }

    /// <summary>
    ///     Attempts to acquire the exclusive lease for <paramref name="dataDirectory" />. Returns the held lease on
    ///     success (the caller keeps it for the process lifetime and disposes it on shutdown), or <c>null</c> when another
    ///     instance already holds it. Any other IO failure (e.g. the directory is not writable) is surfaced so a genuinely
    ///     broken data directory fails loudly rather than masquerading as a running instance.
    /// </summary>
    /// <param name="dataDirectory">The per-user data directory whose lease serializes process instances.</param>
    internal static SingleInstanceLease? TryAcquire(string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);

        var leasePath = Path.Combine(dataDirectory, LeaseFileName);

        FileStream handle;
        try
        {
            // FileShare.None: an exclusive open. A second instance's identical open throws IOException, which is the
            // "already running" signal. OpenOrCreate so the very first launch creates the lease file.
#pragma warning disable CA2000 // Ownership of the handle transfers to the returned SingleInstanceLease (disposed via its Dispose).
            handle = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
#pragma warning restore CA2000
        }
        catch (IOException)
        {
            // The exclusive lock is held by another live instance (or, transiently, a not-yet-released crashed one that
            // the OS is still cleaning up). Either way this process must not proceed to touch the shared key/DB.
            return null;
        }

        return new SingleInstanceLease(handle);
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
