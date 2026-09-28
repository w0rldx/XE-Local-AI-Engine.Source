namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Diagnostics;

/// <summary>The exit reads every handle shares, each tolerant of a process that is gone or already disposed.</summary>
internal static class ProcessExitState
{
    public static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true; // No associated process — treat as exited.
        }
    }

    public static int? SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            // No associated process, or the handle is already disposed: the code is unknown.
            return null;
        }
    }

    public static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        if (SafeHasExited(process))
        {
            return true;
        }

        try
        {
            await process.WaitForExitAsync(ct).WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
