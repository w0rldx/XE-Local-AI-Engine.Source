namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

/// <summary>
///     One node-scoped process jail: the handle it was created under, its jail directory, the containment resolved at
///     create time, and the commands currently running inside it.
///     <para>
///         It is shared by exactly two owners and belongs to neither alone: <see cref="SandboxLifecycleRegistry" />
///         creates, finds and terminates it, while <see cref="ProcessSandboxRuntimeProvider" />'s command-execution
///         path reads <see cref="JailRoot" />/<see cref="LaunchPolicy" /> and fills <see cref="InFlight" />. There is
///         one instance per live sandbox, held by the registry's single dictionary — never copied.
///     </para>
/// </summary>
internal sealed class JailState
{
    public JailState(SandboxHandle handle, string jailRoot, SandboxLaunchPolicy launchPolicy, bool preserveJailRoot = false)
    {
        Handle = handle;
        JailRoot = jailRoot;
        LaunchPolicy = launchPolicy;
        PreserveJailRoot = preserveJailRoot;
    }

    public SandboxHandle Handle { get; }

    public string JailRoot { get; }

    /// <summary>The containment resolved at create time and applied to every command this sandbox runs.</summary>
    public SandboxLaunchPolicy LaunchPolicy { get; }

    public bool PreserveJailRoot { get; }

    public object Sync { get; } = new();

    /// <summary>
    ///     Liveness flag, flipped only through <see cref="MarkDead" />.
    /// </summary>
    public bool Alive { get; private set; } = true;

    public ConcurrentDictionary<string, InFlightExecution> InFlight { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Clears <see cref="Alive" />. The only legitimate caller is <c>SandboxLifecycleRegistry.TerminateState</c>,
    ///     which pairs the flip with cancelling in-flight executions, tree-killing the process and deleting the jail
    ///     directory — flipping it anywhere else leaves a live process behind a "dead" state. Callers hold
    ///     <see cref="Sync" />; this method does not take it.
    /// </summary>
    public void MarkDead() =>
        Alive = false;
}

/// <summary>
///     A single in-flight command: the spawned process plus the per-command cancel source that best-effort cancel
///     and sandbox kill fire to make <see cref="ProcessSandboxRuntimeProvider.ExecuteAsync" /> return a non-throwing
///     Completed=false result.
/// </summary>
internal sealed class InFlightExecution
{
    private readonly CancellationTokenSource _cancelSource;

    public InFlightExecution(Process process, CancellationTokenSource cancelSource)
    {
        Process = process;
        _cancelSource = cancelSource;
    }

    public Process Process { get; }

    public void RequestCancel()
    {
        try
        {
            _cancelSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The command already completed and disposed its source; nothing to cancel.
        }
    }
}

/// <summary>
///     The tree-kill both jail owners need: the command-execution path uses it on every abnormal command exit, and
///     <see cref="SandboxLifecycleRegistry" /> uses it when terminating a jail that still has commands running. It
///     lives here rather than on either owner so neither has to reach into the other for it.
/// </summary>
internal static class SandboxProcessTree
{
    public static void TreeKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // entireProcessTree:true kills descendants too. On Linux the runtime kills the process group; on
                // Windows it walks the tree via the OS APIs. (A dedicated Windows Job Object with
                // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE would be strictly stronger for orphan reaping, but Process.Kill
                // with entireProcessTree is sufficient and OS-correct here; the Job Object polish is deferred and is
                // not load-bearing for the Linux-primary runtime.)
                process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the check and the kill — nothing to do.
        }
        catch (NotSupportedException)
        {
            // Tree-kill unsupported on this platform; fall back to a single-process kill.
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }
    }
}
