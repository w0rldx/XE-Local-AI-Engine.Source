namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Diagnostics;

/// <summary>Kills a short-lived helper process and its descendants, then waits a bounded time for the exit.</summary>
public static class ProcessTreeKill
{
    private const int ExitWaitMilliseconds = 5000;

    /// <summary>
    ///     Best effort: a process that already exited, that the OS refuses to signal, that the platform cannot tree-kill,
    ///     or whose descendants survive the kill is left as it is so the caller's own failure keeps propagating.
    /// </summary>
    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(ExitWaitMilliseconds);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Best-effort bounded cleanup.
        }
    }
}
