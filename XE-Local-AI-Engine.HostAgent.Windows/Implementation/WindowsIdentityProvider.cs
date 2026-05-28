namespace XE_Local_AI_Engine.HostAgent.Windows.Implementation;

using System.Runtime.Versioning;
using System.Security.Principal;

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
