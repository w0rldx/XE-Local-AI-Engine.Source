namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

/// <summary>
///     One node-scoped process jail: the handle it was created under, its jail directory, the containment resolved at create time, and the
///     commands currently running inside it.
/// </summary>
/// <remarks>
///     Shared by exactly two owners and belonging to neither alone — <see cref="SandboxLifecycleRegistry" /> creates, finds and terminates
///     it, while <see cref="ProcessSandboxRuntimeProvider" />'s command path reads <see cref="JailRoot" /> and fills
///     <see cref="InFlight" /> — with one instance per live sandbox in the registry's single dictionary, never copied. It also carries the
///     disk-ceiling state outliving a single command: the ceiling an attach may tighten, and the occupancy baseline every command meters
///     against. Both are read from the command path without the registry lock, so both are interlocked.
/// </remarks>
internal sealed class JailState
{
    /// <summary>
    ///     The stored value of <see cref="MaxJailDiskBytes" /> meaning "no per-sandbox ceiling; inherit the node-wide one".
    /// </summary>
    /// <remarks>
    ///     <see cref="long.MaxValue" /> rather than a separate flag, so <see cref="TightenMaxJailDiskBytes" /> is a plain compare-and-swap
    ///     against a number: every positive request is smaller, so the first always wins and no null case is special-cased in the loop.
    /// </remarks>
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
    ///     The per-sandbox jail-occupancy ceiling in force for the NEXT command, or <see langword="null" /> to inherit the node-wide one.
    /// </summary>
    /// <remarks>
    ///     Stored RAW rather than pre-resolved: the node-wide ceiling belongs to the provider, and
    ///     <c>ProcessSandboxRuntimeProvider.ResolveJailDiskCeiling</c> is the single place the tighten-only <c>min(node, request)</c> is
    ///     applied. It is read from the command path without the registry lock while an attach may be lowering it, hence the interlocked
    ///     read; a command reads it ONCE at start and keeps that snapshot for its whole run.
    /// </remarks>
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
    ///     Lowers the per-sandbox ceiling to <paramref name="requested" /> when that is stricter than what the sandbox already carries, and
    ///     does nothing otherwise — the one thing an attach under the same key may do to a create-time property.
    /// </summary>
    /// <remarks>
    ///     TIGHTEN-ONLY, for the provider's reason: a second caller attaching to a live jail must never buy itself more room than the
    ///     creator, and an attach naming no ceiling must not erase one. A non-positive request cannot be constructed and is ignored rather
    ///     than trusted, so nothing re-enables a watchdog with a zero. It applies to FUTURE commands only: one already running keeps the
    ///     budget it was launched under, since moving the line mid-write would terminate it for bytes that were within the rules.
    ///     Lock-free and idempotent — concurrent attaches converge on the minimum.
    /// </remarks>
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
    /// </summary>
    /// <remarks>
    ///     <paramref name="measure" /> runs only on the first call and a caller losing the race gets the winner's value, so every command
    ///     meters against the same reference point. That fixed reference is what makes the ceiling one on OCCUPANCY rather than per-command
    ///     growth: re-measuring per command hands each a fresh allowance, so N commands leave N times the ceiling while no single one
    ///     exceeds it. Anchoring at what the ENGINE staged does not charge a command for a workspace copy-in it did not write.
    /// </remarks>
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

    /// <summary>Clears <see cref="Alive" />.</summary>
    /// <remarks>
    ///     The only legitimate caller is <c>SandboxLifecycleRegistry.TerminateState</c>, which pairs the flip with cancelling in-flight
    ///     executions, tree-killing the process and deleting the jail directory; flipping it anywhere else leaves a live process behind a
    ///     "dead" state. Callers hold <see cref="Sync" /> — this method does not take it.
    /// </remarks>
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

    public InFlightExecution(Process process, CancellationTokenSource cancelSource, string? scopeUnitName = null)
    {
        Process = process;
        _cancelSource = cancelSource;
        ScopeUnitName = scopeUnitName;
    }

    public Process Process { get; }

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

/// <summary>The tree-kill both jail owners need.</summary>
/// <remarks>
///     The command-execution path uses it on every abnormal command exit and <see cref="SandboxLifecycleRegistry" /> when terminating a
///     jail with commands still running. It lives here rather than on either owner so neither has to reach into the other for it.
/// </remarks>
internal static class SandboxProcessTree
{
    public static void TreeKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // entireProcessTree:true kills descendants too: on Linux the runtime kills the process group, on Windows it walks the tree
                // via the OS APIs. A Windows Job Object would be stronger for orphan reaping but is not load-bearing for a Linux runtime.
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
