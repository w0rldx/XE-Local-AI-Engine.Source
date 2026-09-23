namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Fallback process handle for platforms without a dedicated containment primitive (macOS and other Unix).
///     Tree-kill terminates the process and its descendants via <see cref="Process.Kill(bool)" />.
/// </summary>
internal sealed class PlainWhisperProcessHandle : IWhisperServerProcessHandle
{
    private readonly Process _process;
    private readonly WhisperServerStderrTail? _stderrTail;
    private int _disposed;

    public PlainWhisperProcessHandle(Process process, WhisperServerStderrTail? stderrTail = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
        _stderrTail = stderrTail;
    }

    public int ProcessId => _process.Id;

    public bool HasExited => SafeHasExited(_process);

    public int? ExitCode => SafeExitCode(_process);

    public string? StderrTail => _stderrTail?.Snapshot();

    public void TreeKill()
    {
        if (SafeHasExited(_process))
        {
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill — nothing to do.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
        {
            return;
        }

        try
        {
            TreeKill();
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>Takes ownership of an already-started process, disposing it if the wrap itself throws.</summary>
    public static PlainWhisperProcessHandle Wrap(Process process, WhisperServerStderrTail? stderrTail = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return new PlainWhisperProcessHandle(process, stderrTail);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // No associated process — treat as exited.
            return true;
        }
    }

    private static int? SafeExitCode(Process process)
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
}
