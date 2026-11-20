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

    public void Dispose()
    {
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        Thread.Dispose();
        if (!_processReleased)
        {
            Process.Dispose();
        }
    }
}
