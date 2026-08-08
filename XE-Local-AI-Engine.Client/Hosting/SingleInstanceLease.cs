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

    // The two Win32 HResults that a FileShare.None open raises when the file is already locked by another handle. Only
    // these (on Windows) mean "another instance holds the lease"; other IOExceptions are genuine faults.
    private const int HResultSharingViolation = unchecked((int)0x80070020); // ERROR_SHARING_VIOLATION
    private const int HResultLockViolation = unchecked((int)0x80070021); // ERROR_LOCK_VIOLATION

    /// <summary>
    ///     Attempts to acquire the exclusive lease for <paramref name="dataDirectory" />. Returns the held lease on
    ///     success (the caller keeps it for the process lifetime and disposes it on shutdown), or <c>null</c> only when
    ///     another instance already holds it. A genuinely broken data directory fails loudly rather than masquerading as a
    ///     running instance: <see cref="UnauthorizedAccessException" /> (permission denied — not an
    ///     <see cref="IOException" />, so uncaught), a missing parent directory
    ///     (<see cref="DirectoryNotFoundException" />), and an over-long path (<see cref="PathTooLongException" />) all
    ///     propagate, as does any other Windows IO fault such as disk-full. Only a Windows sharing/lock violation, or a
    ///     Unix <c>flock</c> conflict (which surfaces as a plain <see cref="IOException" />), is treated as contention.
    /// </summary>
    /// <param name="dataDirectory">The per-user data directory whose lease serializes process instances.</param>
    internal static SingleInstanceLease? TryAcquire(string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);

        var leasePath = Path.Combine(dataDirectory, LeaseFileName);

        FileStream handle;
        try
        {
            // FileShare.None: an exclusive open. A second instance's identical open fails with a sharing/lock violation,
            // which is the "already running" signal. OpenOrCreate so the very first launch creates the lease file.
#pragma warning disable CA2000 // Ownership of the handle transfers to the returned SingleInstanceLease (disposed via its Dispose).
            handle = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
#pragma warning restore CA2000
        }
        catch (DirectoryNotFoundException)
        {
            // The parent data directory does not exist. That is a broken/misconfigured data root, not a running
            // instance — surface it so it fails loudly instead of silently masquerading as contention.
            throw;
        }
        catch (PathTooLongException)
        {
            // A misconfigured, over-long data path. Also a broken data root, not contention — surface it.
            throw;
        }
        catch (IOException exception) when (OperatingSystem.IsWindows() && !IsWindowsSharingOrLockViolation(exception))
        {
            // On Windows a non-sharing IOException (e.g. disk full, ERROR_DISK_FULL) is a real fault, not another
            // instance holding the lease — surface it. Sharing/lock violations fall through to the contention path below.
            throw;
        }
        catch (IOException)
        {
            // A Windows sharing/lock violation, or a Unix flock conflict (surfaced as a plain IOException with no
            // portable distinguishing HResult): the exclusive lock is held by another live instance (or, transiently, a
            // not-yet-released crashed one the OS is still cleaning up). Either way this process must not proceed to touch
            // the shared key/DB.
            return null;
        }

        return new SingleInstanceLease(handle);
    }

    private static bool IsWindowsSharingOrLockViolation(IOException exception)
    {
        return exception.HResult is HResultSharingViolation or HResultLockViolation;
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
