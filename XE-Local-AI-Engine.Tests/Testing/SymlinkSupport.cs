namespace XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Whether this host lets the test process create symbolic links, answered by trying it once rather than by
///     guessing from the OS.
///     <para>
///         Creating a symbolic link on Windows needs <c>SeCreateSymbolicLinkPrivilege</c>, which an ordinary account
///         does not hold unless Developer Mode is on or the process is elevated. Without it
///         <see cref="Directory.CreateSymbolicLink" /> throws
///         <c>IOException: A required privilege is not held by the client</c> — a host-configuration fact, not a
///         product defect, and the tests that plant a link to prove a guard rejects it cannot even reach their
///         assertion.
///     </para>
///     <para>
///         Deliberately a probe and not <c>if (OperatingSystem.IsWindows()) Skip</c>. These are security guards
///         (no-follow, escape rejection), and a blanket OS skip retires them on every Windows box including the ones
///         that could run them. With a probe, a Developer-Mode host proves the guard; only a host that genuinely
///         cannot create the link skips — and it says so.
///     </para>
/// </summary>
internal static class SymlinkSupport
{
    private static readonly Lazy<bool> Supported = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when this process can create symbolic links on this host.</summary>
    public static bool IsSupported => Supported.Value;

    /// <summary>
    ///     Skips the calling test when symbolic links cannot be created here. Call it before planting any link.
    /// </summary>
    public static void EnsureSupported()
    {
        if (!IsSupported)
        {
            Skip.Test("This host does not permit creating symbolic links (on Windows this needs Developer Mode or an elevated process).");
        }
    }

    private static bool Probe()
    {
        var root = Path.Combine(Path.GetTempPath(), $"xe-symlink-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, "target.txt");
            File.WriteAllText(target, "probe");

            // A FILE link is the weaker of the two on Windows — a directory link needs the same privilege — so one
            // probe answers for both call shapes.
            File.CreateSymbolicLink(Path.Combine(root, "link.txt"), target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort probe cleanup.
            }
        }
    }
}
