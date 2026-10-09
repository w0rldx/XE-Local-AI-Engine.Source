namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Runtime.Versioning;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Coverage for the per-data-root single-instance lease. The lease serializes process instances that share
///     one per-user data directory so two concurrent first launches cannot split the DB-encryption key. Concurrency is
///     exercised with multiple handles in-process (the exclusive open is enforced by the OS across handles, same as it is
///     across processes), so no second process is needed.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SingleInstanceLeaseTests
{
    [Test]
    public void TryAcquire_OnFreeDataDirectory_Succeeds()
    {
        using var temp = new TempDataDirectory();

        using var lease = SingleInstanceLease.TryAcquire(temp.Path);

        AssertEx.NotNull(lease);
        AssertEx.True(File.Exists(Path.Combine(temp.Path, SingleInstanceLease.LeaseFileName)),
            "Acquiring the lease must create the lease file.");
    }

    [Test]
    public void TryAcquire_WhileAlreadyHeld_ReturnsNull()
    {
        using var temp = new TempDataDirectory();

        using var first = SingleInstanceLease.TryAcquire(temp.Path);
        AssertEx.NotNull(first);

        var second = SingleInstanceLease.TryAcquire(temp.Path);

        AssertEx.Null(second, "A second instance must not acquire the lease while the first holds it.");
    }

    [Test]
    public async Task TryAcquire_WhenParentDirectoryMissing_ThrowsRatherThanReportingAnotherInstance()
    {
        // A data directory whose parent does not exist is a broken/misconfigured data root, not a running instance. The
        // exclusive open raises DirectoryNotFoundException, which must surface (fail loud) rather than be swallowed as
        // contention and returned as null.
        var missingDirectory = Path.Combine(Path.GetTempPath(),
            "xe-single-instance-lease-tests",
            Guid.NewGuid().ToString("N"),
            "missing-parent");

        await AssertEx.ThrowsAsync<DirectoryNotFoundException>(() =>
        {
            SingleInstanceLease.TryAcquire(missingDirectory);
            return Task.CompletedTask;
        });
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task TryAcquire_WhenUnixOpenFailsForAnotherReason_ThrowsRatherThanReportingAnotherInstance()
    {
        // ELOOP stands in for EROFS/EIO: a genuine open fault that is not a flock conflict and must not read as contention.
        using var temp = new TempDataDirectory();
        var leasePath = Path.Combine(temp.Path, SingleInstanceLease.LeaseFileName);
        File.CreateSymbolicLink(leasePath, leasePath);

        await AssertEx.ThrowsAsync<IOException>(() =>
        {
            SingleInstanceLease.TryAcquire(temp.Path);
            return Task.CompletedTask;
        });
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task TryAcquire_WhenDataDirectoryIsNotWritable_ThrowsWithAnOwnershipHint()
    {
        if (Environment.IsPrivilegedProcess)
        {
            Skip.Test("BLOCKED: a privileged process bypasses the directory mode this test denies the creation with.");
        }

        using var temp = new TempDataDirectory();
        File.SetUnixFileMode(temp.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // DesktopDataDirectoryException is what the engine maps to exit 10 with this text, instead of crashing.
            var exception = await AssertEx.ThrowsAsync<DesktopDataDirectoryException>(() =>
            {
                SingleInstanceLease.TryAcquire(temp.Path);
                return Task.CompletedTask;
            });

            AssertEx.Contains(exception.SafeDiagnostic, "ownership", StringComparison.OrdinalIgnoreCase);
            AssertEx.Contains(exception.SafeDiagnostic, temp.Path);
            AssertEx.True(exception.InnerException is UnauthorizedAccessException, "The OS denial stays the inner exception.");
        }
        finally
        {
            File.SetUnixFileMode(temp.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public void TryAcquire_AfterDispose_ReacquiresSuccessfully()
    {
        using var temp = new TempDataDirectory();

        var first = SingleInstanceLease.TryAcquire(temp.Path);
        AssertEx.NotNull(first);
        first!.Dispose();

        using var second = SingleInstanceLease.TryAcquire(temp.Path);

        AssertEx.NotNull(second, "Disposing the held lease must release it so a new instance can acquire it.");
    }

    /// <summary>A disposable temp directory standing in for the per-user data directory.</summary>
    private sealed class TempDataDirectory : IDisposable
    {
        public TempDataDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-single-instance-lease-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
