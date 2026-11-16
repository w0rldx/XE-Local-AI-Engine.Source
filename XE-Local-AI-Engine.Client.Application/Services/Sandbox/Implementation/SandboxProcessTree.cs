namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Diagnostics;

/// <summary>The tree-kill both jail owners need.</summary>
/// <remarks>
///     The command-execution path uses it on every abnormal command exit and <see cref="SandboxLifecycleRegistry" /> when terminating a
///     jail with commands still running. It lives here rather than on either owner so neither has to reach into the other for it.
/// </remarks>
internal static class SandboxProcessTree
{
    public static void TreeKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // Linux kills the process group; Windows walks the tree and misses a reparented descendant. A Windows command with ceilings
                // runs in a Job Object instead (WindowsJobSandboxChildProcess), whose kill reaches every member.
                process.Kill(true);
            }
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
    }
}
