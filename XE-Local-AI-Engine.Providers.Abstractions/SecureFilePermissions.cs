namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

/// <summary>
///     Narrows a secret-bearing file on disk to the current user only: an explicit, inheritance-free Windows ACL, or
///     mode <c>0600</c> on Linux/macOS.
/// </summary>
/// <remarks>
///     Every token, credential and MSAL-cache store on the node hardens through this one implementation — the rule is
///     the last line of defence for a protected blob, so a copy that drifts is a silent downgrade on whichever store
///     holds the stale copy.
/// </remarks>
public static class SecureFilePermissions
{
    /// <summary>
    ///     Applies the user-only permission set to <paramref name="path" />, which must already exist. Callers invoke
    ///     this immediately after the write; on platforms with neither ACLs nor Unix modes it is a no-op.
    /// </summary>
    public static void Apply(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OperatingSystem.IsWindows())
        {
            ApplyWindowsFileSecurity(path);
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    ///     Replaces <paramref name="path" /> with <paramref name="content" /> atomically and user-only: a failed or
    ///     cancelled write leaves the previous file intact instead of a torn one.
    /// </summary>
    /// <remarks>
    ///     The bytes go to a temp sibling created <c>0600</c> on Linux/macOS in the create call itself (no world-readable
    ///     window), <see cref="Apply" /> narrows it (the Windows ACL) before it is renamed over the target, so the target
    ///     never exists with weaker permissions. Same directory is load-bearing: a rename across filesystems is a copy.
    /// </remarks>
    public static async Task WriteAllBytesAtomicAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var tempPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            var stream = new FileStream(tempPath, options);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }

            Apply(tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            DeleteBestEffort(tempPath);
            throw;
        }
    }

    /// <summary>
    ///     A leftover temp sibling is litter, never a correctness problem, and must not mask the exception being propagated.
    /// </summary>
    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; an orphan .tmp sibling stays behind.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; an orphan .tmp sibling stays behind.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsFileSecurity(string path)
    {
        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var currentIdentity = WindowsIdentity.GetCurrent();
        if (currentIdentity.User is not null)
        {
            fileSecurity.AddAccessRule(new FileSystemAccessRule(currentIdentity.User,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        var fileInfo = new FileInfo(path);
        fileInfo.SetAccessControl(fileSecurity);
    }
}
