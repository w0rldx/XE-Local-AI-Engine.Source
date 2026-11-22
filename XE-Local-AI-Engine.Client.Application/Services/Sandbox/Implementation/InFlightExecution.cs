namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

/// <summary>
///     A single in-flight command: the spawned process plus the per-command cancel source that best-effort cancel
///     and sandbox kill fire to make <see cref="ProcessSandboxRuntimeProvider.ExecuteAsync" /> return a non-throwing
///     Completed=false result.
/// </summary>
internal sealed class InFlightExecution
{
    private readonly CancellationTokenSource _cancelSource;

    public InFlightExecution(SandboxLaunchTeardown teardown, CancellationTokenSource cancelSource, string? scopeUnitName = null)
    {
        Teardown = teardown;
        _cancelSource = cancelSource;
        ScopeUnitName = scopeUnitName;
    }

    /// <summary>The child's one shared group-and-leader kill, which a sandbox kill runs instead of a kill of its own.</summary>
    public SandboxLaunchTeardown Teardown { get; }

    public ISandboxChildProcess Process => Teardown.Process;

    /// <summary>The transient systemd scope this command runs in, when it runs behind a filesystem boundary.</summary>
    /// <remarks>
    ///     Carried here rather than only in the descriptor the command path holds, because a sandbox KILL arrives from a different call
    ///     frame and without the unit name could only tree-kill a pid whose descendants live in a PID namespace it cannot see.
    /// </remarks>
    public string? ScopeUnitName { get; }

    public void RequestCancel()
    {
        try
        {
            // Forced sync: RequestCancel is the kill path, called from SandboxLifecycleRegistry.TerminateState, which runs under
            // state.Sync and deletes the jail directory right after. CancelAsync would move callbacks off this thread and break that.
#pragma warning disable MA0045 // forced sync: synchronous kill path (see comment above)
            _cancelSource.Cancel();
#pragma warning restore MA0045
        }
        catch (ObjectDisposedException)
        {
            // The command already completed and disposed its source; nothing to cancel.
        }
    }
}
