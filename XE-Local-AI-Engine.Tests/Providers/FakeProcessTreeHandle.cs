namespace XE_Local_AI_Engine.Tests.Providers;

using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>An in-memory process handle whose exit and tree-kill are directly controllable by the test.</summary>
internal sealed class FakeProcessTreeHandle : IProcessTreeHandle
{
    private readonly TaskCompletionSource _exitSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _exitOnTreeKill;
    private readonly Action? _onTreeKill;
    private int _exited;
    private int _killed;

    public FakeProcessTreeHandle(int pid, bool exitOnTreeKill = true, Action? onTreeKill = null)
    {
        _exitOnTreeKill = exitOnTreeKill;
        _onTreeKill = onTreeKill;
        ProcessId = pid;
    }

    public bool WasTreeKilled => Volatile.Read(ref _killed) != 0;

    public bool WasDisposed { get; private set; }

    public int ProcessId { get; }

    public bool HasExited => Volatile.Read(ref _exited) != 0;

    public int? ExitCode { get; private set; }

    public string? StderrTail { get; private set; }

    public void TreeKill()
    {
        // Runs BEFORE the exit is signaled, so a test can hold a teardown open and observe the supervisor's state
        // mid-removal.
        _onTreeKill?.Invoke();
        Interlocked.Exchange(ref _killed, value: 1);
        if (_exitOnTreeKill)
        {
            // Leaves ExitCode and StderrTail alone: killing a child that already died keeps its diagnostics.
            MarkExited();
        }
    }

    // The supervisor waits on a registered daemon's exit to tick residency; completes on a kill or a simulated exit.
    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _exitSignal.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        WasDisposed = true;
    }

    /// <summary>Simulates a crash or exit so the next ensure-running sees a dead process, optionally with the real handle's diagnostics.</summary>
    public void SimulateExit(int? exitCode = null, string? stderrTail = null)
    {
        ExitCode = exitCode;
        StderrTail = stderrTail;
        MarkExited();
    }

    private void MarkExited()
    {
        Interlocked.Exchange(ref _exited, value: 1);
        _exitSignal.TrySetResult();
    }
}
