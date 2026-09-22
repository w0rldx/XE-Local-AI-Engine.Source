namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Fallback process handle for platforms without a dedicated containment primitive (macOS / other Unix). Tree-kill
///     terminates the process and its descendants via <see cref="Process.Kill(bool)" /> with <c>entireProcessTree</c>.
///     Mirrors <c>PlainProcessHandle</c>.
/// </summary>
internal sealed class PlainImageProcessHandle : IImageServerProcessHandle
{
    private readonly Process _process;
    private readonly ImageServerStderrTail? _stderrTail;
    private int _disposed;

    public PlainImageProcessHandle(Process process, ImageServerStderrTail? stderrTail = null)
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
            // Process already exited between the check and the kill — nothing to do.
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
    public static PlainImageProcessHandle Wrap(Process process, ImageServerStderrTail? stderrTail = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return new PlainImageProcessHandle(process, stderrTail);
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
            return null; // No associated process — the code is unavailable.
        }
    }
}
