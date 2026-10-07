namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     <see cref="MxcAclResidueSweeper" /> on a real NTFS temp tree: explicit per-run AppContainer ACEs left by a crashed MXC run are
///     removed from a granted root and its direct children only, and every other ACE stays, the well-known package groups included.
/// </summary>
[Category(TestCategories.Integration)]
[RunOn(OS.Windows)]
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MxcAclResidueSweeperWindowsTests
{
    // The shape MXC mints per container (S-1-15-2- plus seven sub-authorities), never one of the two well-known package groups.
    private static readonly SecurityIdentifier ContainerPackage = new("S-1-15-2-1111111111-2222222222-3333333333-444444444-555555555-666666666-777777777");
    private static readonly SecurityIdentifier InternetClientCapability = new("S-1-15-3-1");
    private static readonly SecurityIdentifier AllApplicationPackages = new("S-1-15-2-1");

    [Test]
    public async Task Sweep_RemovesExplicitAppContainerAces_OnTheRootAndItsChildrenOnly_AndKeepsEveryOtherAce()
    {
        var jail = Directory.CreateTempSubdirectory("xe-mxc-sweep-");
        try
        {
            var file = new FileInfo(Path.Combine(jail.FullName, "residue.txt"));
            await File.WriteAllTextAsync(file.FullName, "x");
            var nested = jail.CreateSubdirectory("child").CreateSubdirectory("grandchild");
            var user = WindowsIdentity.GetCurrent().User!;

            var jailSecurity = jail.GetAccessControl();
            jailSecurity.AddAccessRule(new FileSystemAccessRule(ContainerPackage, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            jailSecurity.AddAccessRule(new FileSystemAccessRule(AllApplicationPackages, FileSystemRights.ReadData, AccessControlType.Allow));
            jailSecurity.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadData, AccessControlType.Allow));
            jail.SetAccessControl(jailSecurity);
            AddExplicit(file, InternetClientCapability);
            AddExplicit(nested, ContainerPackage);

            var removed = MxcAclResidueSweeper.Sweep([jail.FullName], NullLogger.Instance);

            AssertEx.Equal(2, removed);
            AssertEx.Equal(0, ExplicitAppContainerAces(jail.GetAccessControl()));
            AssertEx.Equal(0, ExplicitAppContainerAces(file.GetAccessControl()));
            AssertEx.Equal(1, ExplicitAppContainerAces(nested.GetAccessControl()), "the sweep is top-level: a grandchild is not walked");
            var kept = jail.GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToList();
            AssertEx.Contains(kept, rule => rule.IdentityReference.Equals(user), "the sweep must keep non-AppContainer ACEs");
            AssertEx.Contains(kept, rule => rule.IdentityReference.Equals(AllApplicationPackages), "the well-known package group is not MXC residue");
        }
        finally
        {
            jail.Delete(recursive: true);
        }
    }

    private static void AddExplicit(FileSystemInfo entry, SecurityIdentifier sid)
    {
        switch (entry)
        {
            case DirectoryInfo directory:
                var directorySecurity = directory.GetAccessControl();
                directorySecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Read, AccessControlType.Allow));
                directory.SetAccessControl(directorySecurity);
                break;
            case FileInfo file:
                var fileSecurity = file.GetAccessControl();
                fileSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Read, AccessControlType.Deny));
                file.SetAccessControl(fileSecurity);
                break;
        }
    }

    [Test]
    public async Task Sweep_WhenARootIsMissing_ReturnsZeroAndDoesNotThrow()
    {
        AssertEx.Equal(0, MxcAclResidueSweeper.Sweep([Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], NullLogger.Instance));
        await Task.CompletedTask;
    }

    private static int ExplicitAppContainerAces(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Count(rule => rule.IdentityReference is SecurityIdentifier sid && MxcAclResidueSweeper.IsAppContainerSid(sid));
}
