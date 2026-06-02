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

/// <summary>
///     Provider implementation for i windows identity behavior.
/// </summary>
public interface IWindowsIdentityProvider
{
    WindowsHostIdentity GetCurrent();
}
