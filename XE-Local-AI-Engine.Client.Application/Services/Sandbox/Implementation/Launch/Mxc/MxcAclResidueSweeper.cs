namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

/// <summary>
///     Removes AppContainer ACEs that an MXC run left on engine-owned trees. MXC grants ProcessContainer filesystem access as host
///     DACL entries and clears them on exit, but a crash or a failed cleanup step can leave them behind.
/// </summary>
/// <remarks>
///     Top level only: MXC grants exactly the listed paths. Removes explicit <c>S-1-15-2-*</c>/<c>S-1-15-3-*</c> entries except the
///     two well-known package groups; reparse points are not followed; never throws. Engine-owned roots only (<see cref="MxcGrantJournal" />):
///     an operator tree may hold another app's legitimate ACEs, and residue there is hygiene, not security, since each container gets a
///     fresh SID that a dead container's stale ACE does not grant to.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class MxcAclResidueSweeper
{
    private static readonly EnumerationOptions ChildOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly HashSet<string> WellKnownPackageGroups = new(StringComparer.Ordinal) { "S-1-15-2-1", "S-1-15-2-2" };

    /// <returns>The number of ACEs removed.</returns>
    public static int Sweep(IEnumerable<string> roots, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(logger);
        var removed = 0;
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                removed += SweepEntry(new DirectoryInfo(root), logger);
                foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", ChildOptions))
                {
                    removed += SweepEntry(entry, logger);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "MXC ACL residue sweep could not walk {Root}", root);
            }
        }

        if (removed > 0)
        {
            logger.LogInformation("MXC ACL residue sweep removed {Count} AppContainer ACE(s)", removed);
        }

        return removed;
    }

    internal static bool IsAppContainerSid(SecurityIdentifier sid) =>
        (sid.Value.StartsWith("S-1-15-2-", StringComparison.Ordinal) || sid.Value.StartsWith("S-1-15-3-", StringComparison.Ordinal))
        && !WellKnownPackageGroups.Contains(sid.Value);

    private static int SweepEntry(FileSystemInfo entry, ILogger logger)
    {
        try
        {
            return entry switch
            {
                DirectoryInfo directory => SweepSecurity(directory.GetAccessControl(), security => directory.SetAccessControl(security)),
                FileInfo file => SweepSecurity(file.GetAccessControl(), security => file.SetAccessControl(security)),
                _ => 0,
            };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "MXC ACL residue sweep could not clean {Path}", entry.FullName);
            return 0;
        }
    }

    private static int SweepSecurity<TSecurity>(TSecurity security, Action<TSecurity> apply)
        where TSecurity : FileSystemSecurity
    {
        var residue = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference is SecurityIdentifier sid && IsAppContainerSid(sid))
            .ToList();
        if (residue.Count == 0)
        {
            return 0;
        }

        foreach (var rule in residue)
        {
            security.RemoveAccessRuleSpecific(rule);
        }

        apply(security);
        return residue.Count;
    }
}
