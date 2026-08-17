namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

/// <summary>
///     Narrows a secret-bearing file on disk to the current user only: an explicit, inheritance-free Windows ACL, or
///     mode <c>0600</c> on Linux/macOS. Every token, credential and MSAL-cache store on the node hardens through this
///     one implementation — the rule is the last line of defence for a protected blob, so a copy that drifts is a
///     silent downgrade on whichever store holds the stale copy.
/// </summary>
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
