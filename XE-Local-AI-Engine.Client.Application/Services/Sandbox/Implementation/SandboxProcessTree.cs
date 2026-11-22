namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>The tree-kill both jail owners need.</summary>
/// <remarks>
///     The command-execution path uses it on every abnormal command exit and <see cref="SandboxLifecycleRegistry" /> when terminating a
///     jail with commands still running. It lives here rather than on either owner so neither has to reach into the other for it.
/// </remarks>
internal static class SandboxProcessTree
{
    /// <summary>Kills the tree; <see langword="false" /> when the walk could not confirm every descendant killed.</summary>
    public static bool TreeKill(Process process)
    {
        try
        {
            // Linux walks the tree through pidfds (never a recycled pid); Windows walks it via the OS APIs and misses a reparented descendant.
            return process.HasExited
                   || LinuxProcessTreeKill.Kill(process) is not (LinuxProcessTreeKillResult.Incomplete or LinuxProcessTreeKillResult.KillFailed);
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the check and the kill — nothing to do.
        }
        catch (NotSupportedException)
        {
            // Tree-kill unsupported on this platform; fall back to a single-process kill.
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }

        return true;
    }
}
