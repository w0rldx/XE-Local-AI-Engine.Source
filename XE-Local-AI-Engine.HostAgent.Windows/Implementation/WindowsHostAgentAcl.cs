namespace XE_Local_AI_Engine.HostAgent.Windows.Implementation;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

public sealed class WindowsHostAgentAcl
{
    private readonly IWindowsIdentityProvider _identityProvider;

    public WindowsHostAgentAcl(IWindowsIdentityProvider identityProvider)
    {
        _identityProvider = identityProvider;
    }

    [SupportedOSPlatform("windows")]
    public void ApplySecretDirectoryAcl(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(directoryPath);

        var identity = _identityProvider.GetCurrent();
        var directorySecurity = new DirectorySecurity();
        directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        AddDirectoryRule(directorySecurity, identity.UserSid, FileSystemRights.FullControl);
        AddDirectoryRule(directorySecurity, identity.AdministratorsSid, FileSystemRights.FullControl);
        AddDirectoryRule(directorySecurity, identity.SystemSid, FileSystemRights.FullControl);

        new DirectoryInfo(directoryPath).SetAccessControl(directorySecurity);
    }

    [SupportedOSPlatform("windows")]
    public void ApplySecretFileAcl(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var identity = _identityProvider.GetCurrent();
        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        AddFileRule(fileSecurity, identity.UserSid, FileSystemRights.FullControl);
        AddFileRule(fileSecurity, identity.AdministratorsSid, FileSystemRights.FullControl);
        AddFileRule(fileSecurity, identity.SystemSid, FileSystemRights.FullControl);

        new FileInfo(filePath).SetAccessControl(fileSecurity);
    }

    [SupportedOSPlatform("windows")]
    private static void AddDirectoryRule(DirectorySecurity security, SecurityIdentifier sid, FileSystemRights rights)
    {
        security.AddAccessRule(new FileSystemAccessRule(sid,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    [SupportedOSPlatform("windows")]
    private static void AddFileRule(FileSecurity security, SecurityIdentifier sid, FileSystemRights rights)
    {
        security.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
    }
}
