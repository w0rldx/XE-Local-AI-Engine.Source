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
///     <para>
///         It also carries the two pieces of disk-ceiling state that outlive a single command and therefore belong to
///         neither owner's call frame: the per-sandbox ceiling an attach may tighten
///         (<see cref="TightenMaxJailDiskBytes" />) and the occupancy baseline every command meters against
///         (<see cref="GetOrCaptureOccupancyBaseline" />). Both are read from the command path without the registry
///         lock, so both are interlocked.
///     </para>
/// </summary>
internal sealed class JailState
{
    /// <summary>
    ///     The stored value of <see cref="MaxJailDiskBytes" /> that means "no per-sandbox ceiling; inherit the
    ///     node-wide one". <see cref="long.MaxValue" /> rather than a separate flag so
    ///     <see cref="TightenMaxJailDiskBytes" /> is a plain compare-and-swap against a number: every positive request
    ///     is smaller than it, so the first one always wins and no null case has to be special-cased inside the loop.
    /// </summary>
    private const long InheritsNodeCeiling = long.MaxValue;

    /// <summary>The sentinel <see cref="GetOrCaptureOccupancyBaseline" /> starts from; any real measurement is >= 0.</summary>
    private const long NotYetMeasured = -1;

    private long _maxJailDiskBytes;
    private long _occupancyBaseline = NotYetMeasured;

    public JailState(SandboxHandle handle,
        string jailRoot,
        SandboxLaunchPolicy launchPolicy,
        bool preserveJailRoot = false,
        long? maxJailDiskBytes = null)
    {
        Handle = handle;
        JailRoot = jailRoot;
        LaunchPolicy = launchPolicy;
        PreserveJailRoot = preserveJailRoot;
        _maxJailDiskBytes = maxJailDiskBytes is { } ceiling && ceiling > 0 ? ceiling : InheritsNodeCeiling;
    }

    public SandboxHandle Handle { get; }

    public string JailRoot { get; }

    /// <summary>The containment resolved at create time and applied to every command this sandbox runs.</summary>
    public SandboxLaunchPolicy LaunchPolicy { get; }

    public bool PreserveJailRoot { get; }

    /// <summary>
    ///     The per-sandbox jail-occupancy ceiling in force for the NEXT command (<c>SandboxCreateRequest.MaxJailDiskBytes</c>),
    ///     or <see langword="null" /> to inherit the node-wide one. It is stored RAW rather than pre-resolved: the
    ///     node-wide ceiling belongs to the provider, and
    ///     <c>ProcessSandboxRuntimeProvider.ResolveJailDiskCeiling</c> is the single place the tighten-only
    ///     <c>min(node, request)</c> is applied.
    ///     <para>
    ///         Read without the registry lock, from the command path, while an attach may be lowering it — hence the
    ///         interlocked read rather than a plain field. A command reads it ONCE at start and keeps that snapshot for
    ///         its whole run; see <see cref="TightenMaxJailDiskBytes" />.
    ///     </para>
    /// </summary>
    public long? MaxJailDiskBytes
    {
        get
        {
            var stored = Interlocked.Read(ref _maxJailDiskBytes);
            return stored == InheritsNodeCeiling ? null : stored;
        }
    }

    public object Sync { get; } = new();

    /// <summary>
    ///     Liveness flag, flipped only through <see cref="MarkDead" />.
    /// </summary>
    public bool Alive { get; private set; } = true;

    public ConcurrentDictionary<string, InFlightExecution> InFlight { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Lowers the per-sandbox ceiling to <paramref name="requested" /> when that is stricter than what this sandbox
    ///     already carries, and does nothing otherwise. The ceiling is a CREATE-TIME property of the sandbox; this is
    ///     what an attach under the same key is allowed to do to it.
    ///     <para>
    ///         TIGHTEN-ONLY, in the same direction and for the same reason as the provider's <c>min(node, request)</c>:
    ///         a second caller attaching to a live jail must never be able to buy itself more room than the caller that
    ///         created it, and an attach that names no ceiling at all must not erase one. A non-positive request cannot
    ///         be constructed through <c>SandboxCreateRequest</c>, and is ignored here rather than trusted, so nothing
    ///         can re-enable a watchdog by handing this a zero.
    ///     </para>
    ///     <para>
    ///         It applies to FUTURE commands only. A command that is already running keeps the ceiling it started
    ///         with — it was launched under a budget, and moving the line under a process that is mid-write would
    ///         terminate it for bytes that were within the rules when it wrote them.
    ///     </para>
    ///     <para>
    ///         Lock-free and idempotent: concurrent attaches converge on the minimum, because each retry re-reads the
    ///         current value and only ever swaps in a smaller one.
    ///     </para>
    /// </summary>
    public void TightenMaxJailDiskBytes(long? requested)
    {
        if (requested is not { } ceiling || ceiling <= 0)
        {
            return;
        }

        while (true)
        {
            var current = Interlocked.Read(ref _maxJailDiskBytes);
            if (ceiling >= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _maxJailDiskBytes, ceiling, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>
    ///     The jail's occupancy when this sandbox ran its FIRST command, measured once and reused by every later one.
    ///     <paramref name="measure" /> runs only on the first call; a concurrent caller that loses the race gets the
    ///     winner's value, so every command in a sandbox meters against the same reference point.
    ///     <para>
    ///         That fixed reference is what makes the disk ceiling a ceiling on OCCUPANCY rather than on per-command
    ///         growth. Re-measuring per command handed each new command a fresh allowance, so N commands could leave
    ///         N times the ceiling on disk while no single one ever exceeded it. Anchoring instead at what the ENGINE
    ///         staged before the sandbox ran anything keeps that loophole closed without charging a command for a
    ///         workspace it did not write: an AgentHome jail is filled by copy-in before its first command, and that
    ///         content is the baseline, not the budget.
    ///     </para>
    /// </summary>
    public long GetOrCaptureOccupancyBaseline(Func<long> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);

        var existing = Interlocked.Read(ref _occupancyBaseline);
        if (existing >= 0)
        {
            return existing;
        }

        // Deliberately outside any lock: measuring walks the jail, and holding a lock across that would serialize
        // unrelated commands. A loser of the race pays one extra walk and then discards it.
        var measured = Math.Max(val1: 0, measure());
        var won = Interlocked.CompareExchange(ref _occupancyBaseline, measured, NotYetMeasured);
        return won == NotYetMeasured ? measured : won;
    }

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
