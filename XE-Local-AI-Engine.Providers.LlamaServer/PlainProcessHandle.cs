namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Diagnostics;

/// <summary>
///     Fallback process handle for platforms without a dedicated containment primitive (macOS / other Unix). Tree-kill
///     terminates the process and its descendants via <see cref="Process.Kill(bool)" /> with <c>entireProcessTree</c>.
///     Lane A's supervised GPU paths are Windows + Linux; this keeps the launcher functional elsewhere on the CPU floor.
/// </summary>
internal sealed class PlainProcessHandle(Process process) : ILlamaServerProcessHandle
{
    private readonly Process _process = process ?? throw new ArgumentNullException(nameof(process));
    private int _disposed;

    /// <summary>Takes ownership of an already-started process, disposing it if the wrap itself throws.</summary>
    public static PlainProcessHandle Wrap(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return new PlainProcessHandle(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

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
            // Process already exited between the check and the kill — nothing to do.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
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
}
