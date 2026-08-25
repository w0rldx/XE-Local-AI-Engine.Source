namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

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
    IReadOnlyList<SandboxScopeUnitStatus> ListEngineOwnedUnits();
}

/// <summary>
///     One engine-owned transient scope as the startup sweep sees it: its unit name, and how long it has been active.
///     <para>
///         The age is what lets the sweep tell a scope a previous run abandoned from one a worker created seconds ago
///         and has not finished registering. It is <see langword="null" /> when the manager did not answer for that
///         unit, and the sweep treats an unmeasurable age as a reason NOT to signal: killing another instance's live
///         command is unrecoverable, while leaving an orphan costs one <c>RuntimeMaxSec</c> of runtime.
///     </para>
/// </summary>
internal sealed record SandboxScopeUnitStatus(string UnitName, TimeSpan? ActiveFor);

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
    ///     Lists the transient scopes this engine owns that are still loaded on the user manager, each with the age the
    ///     manager reports for it. Used by the startup sweep; anything whose name fails
    ///     <see cref="SandboxScopeUnit.IsEngineOwned" /> is dropped here rather than later, so a caller cannot act on a
    ///     name the sweep would refuse to signal anyway.
    /// </summary>
    /// <inheritdoc />
    public IReadOnlyList<SandboxScopeUnitStatus> ListEngineOwnedUnits()
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

        var names = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (SandboxScopeUnit.IsEngineOwned(name))
            {
                names.Add(name!);
            }
        }

        var ages = ReadActiveDurations(names);

        return [.. names.Select(name => new SandboxScopeUnitStatus(name, ages.TryGetValue(name, out var age) ? age : null))];
    }

    /// <summary>
    ///     Asks the user manager how long each unit has been active, in ONE <c>systemctl show</c> call.
    ///     <para>
    ///         <c>ActiveEnterTimestampMonotonic</c> is microseconds on <c>CLOCK_MONOTONIC</c>, which is why the
    ///         reference clock here is <c>clock_gettime(CLOCK_MONOTONIC)</c> rather than <c>/proc/uptime</c>
    ///         (<c>CLOCK_BOOTTIME</c>, which counts time spent suspended and would report every scope as older than it
    ///         is) or a wall clock (which a time step would move underneath us).
    ///     </para>
    ///     <para>
    ///         A unit the manager does not answer for simply gets no entry: the caller reads a missing age as "do not
    ///         signal this", so a parse that goes wrong fails towards leaving processes alone.
    ///     </para>
    /// </summary>
    private Dictionary<string, TimeSpan> ReadActiveDurations(IReadOnlyList<string> unitNames)
    {
        var durations = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        if (unitNames.Count == 0 || TryReadMonotonicMicroseconds() is not { } nowMicroseconds)
        {
            return durations;
        }

        var arguments = new List<string>(unitNames.Count + 3)
        {
            "show",
            "--property=Id",
            "--property=ActiveEnterTimestampMonotonic"
        };
        arguments.AddRange(unitNames);

        using var process = TryStart(arguments);
        if (process is null)
        {
            return durations;
        }

        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(ListTimeout))
        {
            TryKillHelper(process);

            return durations;
        }

        // One blank-line-separated block per unit, in the order they were asked for. The blocks are correlated by the
        // Id property rather than by position, so a manager that drops or reorders one cannot shift every age onto the
        // wrong unit.
        string? id = null;
        long? activeEnter = null;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                AddDuration(durations, id, activeEnter, nowMicroseconds);
                id = null;
                activeEnter = null;
                continue;
            }

            if (trimmed.StartsWith("Id=", StringComparison.Ordinal))
            {
                id = trimmed["Id=".Length..];
            }
            else if (trimmed.StartsWith("ActiveEnterTimestampMonotonic=", StringComparison.Ordinal)
                     && long.TryParse(trimmed["ActiveEnterTimestampMonotonic=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                activeEnter = parsed;
            }
        }

        AddDuration(durations, id, activeEnter, nowMicroseconds);

        return durations;
    }

    private static void AddDuration(Dictionary<string, TimeSpan> durations, string? unitName, long? activeEnterMicroseconds, long nowMicroseconds)
    {
        // Zero is what systemd prints for a unit that never entered the active state, and a timestamp in the future is
        // a clock we do not understand. Neither is an age, and neither may be turned into one.
        if (unitName is null || activeEnterMicroseconds is not { } activeEnter || activeEnter <= 0 || activeEnter > nowMicroseconds)
        {
            return;
        }

        durations[unitName] = TimeSpan.FromMicroseconds(nowMicroseconds - activeEnter);
    }

    private static long? TryReadMonotonicMicroseconds()
    {
        try
        {
            return clock_gettime(ClockMonotonic, out var now) == 0
                ? (now.Seconds * 1_000_000L) + (now.Nanoseconds / 1_000L)
                : null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
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

    // CLOCK_MONOTONIC — the clock systemd's *Monotonic timestamps are measured on. DllImport rather than the
    // source-generated LibraryImport, matching the libc imports elsewhere in this folder.
    private const int ClockMonotonic = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;

        public long Nanoseconds;
    }

    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int clock_gettime(int clockId, out Timespec timespec);

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
