namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.ComponentModel;
using System.Diagnostics;

/// <summary>
///     The seam the transient-scope kill authority is reached through, so the startup sweep's DECISIONS — which unit
///     is claimed by a live worker, which is an orphan, which name is not one this engine generated — can be tested
///     without a systemd user manager and without signalling anything real.
/// </summary>
internal interface ISandboxScopeUnitKiller
{
    /// <summary>Signals every process in the unit's cgroup and waits for it to empty.</summary>
    Task KillAsync(string unitName, CancellationToken cancellationToken = default);

    /// <summary>The engine-owned transient scopes currently loaded on the user manager.</summary>
    IReadOnlyList<string> ListEngineOwnedUnits();
}

/// <summary>
///     The kill authority for an isolated command: <c>systemctl --user kill --kill-whom=cgroup --signal=SIGKILL
///     --wait &lt;unit&gt;</c>.
///     <para>
///         Why the cgroup and not the process. An isolated command runs in its own PID namespace, so the engine cannot
///         see the workload's processes at all, and the pid it holds belongs to <c>setsid</c> — three execs and one
///         namespace away from anything the workload started. <c>Process.Kill(entireProcessTree)</c> walks a tree the
///         engine cannot see; <c>kill(-pgid)</c> reaches the group, which a workload can leave with one
///         <c>setsid</c> of its own. The transient scope's cgroup is the one container nothing inside can leave, and
///         signalling it by unit name is the only mechanism that is complete.
///     </para>
///     <para>
///         <c>--wait</c> is not cosmetic either: without it the call returns before the processes are gone, and the
///         jail directory the caller deletes next is still being written to.
///     </para>
///     <para>
///         Every method is best-effort and total. A unit that has already been collected, a manager that cannot be
///         reached, a <c>systemctl</c> that fails — all mean "nothing left to kill here", and the PGID tree-kill the
///         provider performs alongside this remains the fallback.
///     </para>
/// </summary>
internal sealed class SandboxScopeUnitKiller : ISandboxScopeUnitKiller
{
    // Bounded so a wedged user manager cannot stall a teardown path. --wait normally returns as soon as the cgroup is
    // empty, which for SIGKILL is immediate.
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly string _systemctlPath;

    public SandboxScopeUnitKiller(string systemctlPath, IReadOnlyDictionary<string, string> userBusEnvironment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemctlPath);
        ArgumentNullException.ThrowIfNull(userBusEnvironment);
        _systemctlPath = systemctlPath;
        _environment = userBusEnvironment;
    }

    /// <summary>Builds a killer from measured containment, or <see langword="null" /> when the host has no isolation.</summary>
    public static SandboxScopeUnitKiller? TryCreate(SandboxFilesystemIsolation? isolation)
    {
        return isolation is null
            ? null
            : new SandboxScopeUnitKiller(isolation.SystemctlPath, isolation.UserBusEnvironment);
    }

    /// <inheritdoc />
    public async Task KillAsync(string unitName, CancellationToken cancellationToken = default)
    {
        if (!SandboxScopeUnit.IsEngineOwned(unitName))
        {
            // A name this engine did not generate is never signalled. The whole sweep rests on that: a loose match
            // would let a unit some other tool named "xe-…" be killed by a startup that had nothing to do with it.
            return;
        }

        using var process = TryStart(["kill", "--kill-whom=cgroup", "--signal=SIGKILL", "--wait", unitName]);
        if (process is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(KillTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillHelper(process);
        }
    }

    /// <summary>
    ///     The synchronous form, for the disposal path — which is itself synchronous and must finish tearing a jail
    ///     down before the process exits, so there is nothing to await on.
    /// </summary>
    public void Kill(string unitName)
    {
        if (!SandboxScopeUnit.IsEngineOwned(unitName))
        {
            return;
        }

        using var process = TryStart(["kill", "--kill-whom=cgroup", "--signal=SIGKILL", "--wait", unitName]);
        if (process is not null && !process.WaitForExit(KillTimeout))
        {
            TryKillHelper(process);
        }
    }

    /// <summary>
    ///     Lists the transient scopes this engine owns that are still loaded on the user manager. Used by the startup
    ///     sweep; anything whose name fails <see cref="SandboxScopeUnit.IsEngineOwned" /> is dropped here rather than
    ///     later, so a caller cannot act on a name the sweep would refuse to signal anyway.
    /// </summary>
    /// <inheritdoc />
    public IReadOnlyList<string> ListEngineOwnedUnits()
    {
        using var process = TryStart(["list-units", "--type=scope", "--all", "--no-legend", "--plain", SandboxScopeUnit.ListPattern]);
        if (process is null)
        {
            return [];
        }

        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(ListTimeout))
        {
            TryKillHelper(process);

            return [];
        }

        var units = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (SandboxScopeUnit.IsEngineOwned(name))
            {
                units.Add(name!);
            }
        }

        return units;
    }

    private Process? TryStart(IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _systemctlPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--user");
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            // Only the bus address: systemctl needs no other inherited state, and the worker's environment carries
            // secrets that have no business in a teardown helper.
            startInfo.Environment.Clear();
            foreach (var pair in _environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            return Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static void TryKillHelper(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Already gone.
        }
    }
}
