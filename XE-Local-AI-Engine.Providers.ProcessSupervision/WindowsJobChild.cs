namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Runtime.InteropServices;

/// <summary>The handles and parent pipe ends of a just-created, still suspended child.</summary>
internal sealed class WindowsJobChild : IDisposable
{
    public required SafeHandle Process { get; init; }

    public required SafeHandle Thread { get; init; }

    public required int ProcessId { get; init; }

    public required Stream StandardInput { get; init; }

    public required Stream StandardOutput { get; init; }

    public required Stream StandardError { get; init; }

    private bool _processReleased;

    /// <summary>Hands the process handle to a new owner; <see cref="Dispose" /> then closes only the thread and the streams.</summary>
    public SafeHandle ReleaseProcess()
    {
        _processReleased = true;
        return Process;
    }

    /// <summary>Closes every owned handle; a close that throws (stdin flushing into an exited reader) never skips the others.</summary>
    public void Dispose()
    {
        try
        {
            StandardInput.Dispose();
        }
        finally
        {
            try
            {
                StandardOutput.Dispose();
                StandardError.Dispose();
            }
            finally
            {
                Thread.Dispose();
                if (!_processReleased)
                {
                    Process.Dispose();
                }
            }
        }
    }
}
