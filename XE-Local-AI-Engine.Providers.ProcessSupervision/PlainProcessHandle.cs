namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Fallback process handle for platforms without a dedicated containment primitive (macOS and other Unix), whose
///     tree-kill terminates the process and its descendants via <see cref="Process.Kill(bool)" />.
/// </summary>
/// <remarks>
///     The supervised GPU paths are Windows and Linux; this keeps a launcher functional elsewhere on the CPU floor.
/// </remarks>
public sealed class PlainProcessHandle : IProcessTreeHandle
{
    private readonly Process _process;
    private readonly ProcessStderrTail? _stderrTail;
    private int _disposed;

    private PlainProcessHandle(Process process, ProcessStderrTail? stderrTail)
    {
        _process = process;
        _stderrTail = stderrTail;
    }

    public int ProcessId => _process.Id;

    public bool HasExited => ProcessExitState.SafeHasExited(_process);

    public int? ExitCode => ProcessExitState.SafeExitCode(_process);

    public string? StderrTail => _stderrTail?.Snapshot();

    public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct) =>
        ProcessExitState.WaitForExitAsync(_process, timeout, ct);

    public void TreeKill()
    {
        if (ProcessExitState.SafeHasExited(_process))
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
    public static PlainProcessHandle Wrap(Process process, ProcessStderrTail? stderrTail = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return new PlainProcessHandle(process, stderrTail);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}
