namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Fallback process handle for platforms without a dedicated containment primitive (macOS and other Unix).
///     Tree-kill terminates the process and its descendants via <see cref="Process.Kill(bool)" />.
/// </summary>
internal sealed class PlainWhisperProcessHandle(Process process) : IWhisperServerProcessHandle
{
    private readonly Process _process = process ?? throw new ArgumentNullException(nameof(process));
    private int _disposed;

    public int ProcessId => _process.Id;

    public bool HasExited => SafeHasExited(_process);

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
    public static PlainWhisperProcessHandle Wrap(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return new PlainWhisperProcessHandle(process);
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
}
