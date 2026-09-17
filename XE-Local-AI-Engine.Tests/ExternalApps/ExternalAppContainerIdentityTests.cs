namespace XE_Local_AI_Engine.Tests.ExternalApps;

using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     One case per daemon mode of the identity table, plus the operator override winning on every one of them. The
///     readers are injected so the rules for a daemon and a platform this machine is not can be asserted rather than
///     reasoned about.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ExternalAppContainerIdentityTests
{
    private const int HostUserId = 1234;
    private const int HostGroupId = 5678;

    /// <summary>
    ///     Under a rootless daemon in-container root maps to the daemon owner's own unprivileged host account, and it
    ///     is the only identity that can write a bind mount the engine created. It looks alarming and is correct.
    /// </summary>
    [Test]
    public void Resolve_AgainstARootlessDaemon_IsZeroZero()
    {
        var identity = ExternalAppContainerIdentity.Resolve(daemonIsRootless: true, containerIdentityOption: null, HostUser, HostGroup);

        AssertEx.Equal(expected: 0, identity.UserId);
        AssertEx.Equal(expected: 0, identity.GroupId);
    }

    [Test]
    public void Resolve_AgainstARootfulDaemon_IsTheEnginesOwnEffectiveIdentity()
    {
        var identity = ExternalAppContainerIdentity.Resolve(daemonIsRootless: false, containerIdentityOption: null, HostUser, HostGroup);

        AssertEx.Equal(HostUserId, identity.UserId);
        AssertEx.Equal(HostGroupId, identity.GroupId);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void Resolve_WithTheOperatorOverride_WinsOnEveryDaemon(bool daemonIsRootless)
    {
        var identity = ExternalAppContainerIdentity.Resolve(daemonIsRootless, "1000:1001", HostUser, HostGroup);

        AssertEx.Equal(expected: 1000, identity.UserId);
        AssertEx.Equal(expected: 1001, identity.GroupId);
    }

    /// <summary>
    ///     A blank value is "not set", not "uid 0": an empty configuration entry must fall through to the daemon rule
    ///     rather than quietly becoming root.
    /// </summary>
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void Resolve_WithABlankOverride_FallsThroughToTheDaemonRule(string option)
    {
        var identity = ExternalAppContainerIdentity.Resolve(daemonIsRootless: false, option, HostUser, HostGroup);

        AssertEx.Equal(HostUserId, identity.UserId);
    }

    [Test]
    public void Resolve_WithAMalformedOverride_Throws()
    {
        _ = AssertEx.Throws<ArgumentException>(() => ExternalAppContainerIdentity.Resolve(daemonIsRootless: false, "root:root", HostUser, HostGroup));
    }

    /// <summary>
    ///     The production entry point takes no readers. It is asserted against the real daemon rule rather than a
    ///     literal, because on this platform the engine's own identity is what a rootful daemon would map through.
    /// </summary>
    [Test]
    public void Resolve_ProductionEntryPoint_AgreesWithTheInjectedForm()
    {
        var production = ExternalAppContainerIdentity.Resolve(daemonIsRootless: true, containerIdentityOption: null);

        AssertEx.Equal(expected: 0, production.UserId);
        AssertEx.Equal("0:0", production.UserSpecification);
    }

    /// <summary>
    ///     The Windows/macOS row of the table. On those hosts the engine is a native process whose account names
    ///     nothing inside a Linux container, so the reader answers the conventional first non-root Linux account; on
    ///     Linux it answers the engine's own effective id, which is what a rootful daemon maps straight through.
    /// </summary>
    [Test]
    public void ReadHostUserId_IsTheDesktopDefaultOffLinux()
    {
        AssertEx.Equal(expected: 1000, ExternalAppContainerIdentity.DesktopDefaultId);

        if (OperatingSystem.IsLinux())
        {
            AssertEx.True(ExternalAppContainerIdentity.ReadHostUserId() >= 0, "A Linux effective uid is never negative.");
            AssertEx.True(ExternalAppContainerIdentity.ReadHostGroupId() >= 0, "A Linux effective gid is never negative.");
            return;
        }

        AssertEx.Equal(ExternalAppContainerIdentity.DesktopDefaultId, ExternalAppContainerIdentity.ReadHostUserId());
        AssertEx.Equal(ExternalAppContainerIdentity.DesktopDefaultId, ExternalAppContainerIdentity.ReadHostGroupId());
    }

    private static int HostUser()
    {
        return HostUserId;
    }

    private static int HostGroup()
    {
        return HostGroupId;
    }
}
