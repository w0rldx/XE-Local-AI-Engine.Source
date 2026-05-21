namespace XE_Local_AI_Engine.HostAgent.Windows;

using System.Runtime.Versioning;
using System.Security.Principal;

public sealed record WindowsHostIdentity(
    SecurityIdentifier UserSid,
    SecurityIdentifier AdministratorsSid,
    SecurityIdentifier SystemSid)
{
    [SupportedOSPlatform("windows")]
    public static WindowsHostIdentity Create(SecurityIdentifier userSid)
    {
        ArgumentNullException.ThrowIfNull(userSid);

        return new WindowsHostIdentity(userSid,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
    }
}

public interface IWindowsIdentityProvider
{
    WindowsHostIdentity GetCurrent();
}

public sealed class WindowsIdentityProvider : IWindowsIdentityProvider
{
    [SupportedOSPlatform("windows")]
    public WindowsHostIdentity GetCurrent()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("HostAgent.Windows identity is only available on Windows.");
        }

        var userSid = WindowsIdentity.GetCurrent().User
                      ?? throw new InvalidOperationException("Unable to resolve the current Windows user SID.");

        return WindowsHostIdentity.Create(userSid);
    }
}
