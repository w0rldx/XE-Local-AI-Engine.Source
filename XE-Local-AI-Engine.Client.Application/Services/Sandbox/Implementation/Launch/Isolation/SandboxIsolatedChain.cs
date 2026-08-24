namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

/// <summary>
///     One read-only tree the jail must see, as an already-opened descriptor plus the canonical path it is bound at.
/// </summary>
internal sealed record SandboxIsolatedTreeBinding(int FileDescriptor, string Path);

/// <summary>
///     Everything the isolated chain needs that is NOT a decision: resolved helper paths, descriptor numbers, the unit
///     name, the host's filesystem layout, and the ceilings. Every field is already measured or already opened, which
///     is what lets <see cref="SandboxIsolatedChain.Render" /> stay a pure function of its input.
/// </summary>
internal sealed record SandboxIsolatedChainInputs
{
    public required string SetsidPath { get; init; }

    public required string SystemdRunPath { get; init; }

    public required string BwrapPath { get; init; }

    /// <summary>The transient scope's unit name — the handle every later kill and every orphan sweep works through.</summary>
    public required string ScopeUnitName { get; init; }

    /// <summary>Wall-clock ceiling the user manager enforces on the scope, independent of the engine being alive.</summary>
    public required long RuntimeMaxSeconds { get; init; }

    public required uint UserId { get; init; }

    public required uint GroupId { get; init; }

    public required IReadOnlyList<SandboxUsrMergeEntry> UsrMergeEntries { get; init; }

    public required int PasswdDescriptor { get; init; }

    public required int GroupDescriptor { get; init; }

    public required int NameServiceSwitchDescriptor { get; init; }

    public required int HostsDescriptor { get; init; }

    public required int JailDescriptor { get; init; }

    public required int JailTempDescriptor { get; init; }

    public IReadOnlyList<SandboxIsolatedTreeBinding> ReadOnlyTrees { get; init; } = [];

    public SandboxResourceLimits? ResourceLimits { get; init; }

    /// <summary>The value every numeric-library thread-count variable is pinned to inside the jail.</summary>
    public required int ThreadLimit { get; init; }

    /// <summary>
    ///     The command's working directory INSIDE the sandbox. Always <c>/work</c> or a path beneath it — the jail is
    ///     the only writable tree and the only place a caller's requested subdirectory can be.
    /// </summary>
    public string WorkingDirectory { get; init; } = SandboxIsolatedChain.WorkPath;

    /// <summary>
    ///     Variables the CALLER asked the command to run with, emitted after the fixed allow-list so they override it.
    ///     <para>
    ///         Passing them through matters more than it looks. The chain is <c>--clearenv</c> plus an allow-list, so a
    ///         caller's <c>SandboxCommandRequest.Environment</c> — which the non-isolated path honours — would
    ///         otherwise be silently dropped the moment a sandbox opted into isolation. A variable that quietly stops
    ///         arriving is a far worse failure than one that is refused.
    ///     </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> AdditionalEnvironment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
///     Renders the exact argument vector of the filesystem-isolated launch chain. Pure, total, and byte-exact: given
///     the same inputs it produces the same vector, which is what lets the whole chain — a security boundary whose
///     argument ORDER is semantic — be asserted by unit test without starting anything.
///     <para>
///         The chain, outermost first:
///     </para>
///     <code>
///     setsid                                     ← process-group leader, so the PGID fallback kill has a target
///       systemd-run --user --scope               ← a named transient scope: the cgroup that IS the kill authority
///         --unit=xe-&lt;role&gt;-&lt;guid&gt;.scope
///         -p KillMode=control-group -p RuntimeMaxSec=… -p MemoryMax=… -p TasksMax=… -p CPUQuota=…
///         bwrap …                                ← the mount, pid, ipc, uts and network namespaces
///           &lt;command&gt;
///     </code>
///     <para>
///         Order is load-bearing at three points, each verified live on this host rather than assumed:
///     </para>
///     <list type="bullet">
///         <item>
///             <c>setsid</c> outermost. Started from .NET's <c>Process.Start</c> it EXECs rather than forks, so the
///             started pid IS the process-group id and the exit code still propagates all the way back through
///             <c>bwrap</c>'s inner pid 1.
///         </item>
///         <item>
///             <c>systemd-run</c> outside <c>bwrap</c>. It talks to the per-user systemd bus over a UNIX socket, and
///             the jail deliberately has no such socket — it must therefore create the scope BEFORE the namespaces
///             exist. This is also why the jail needs no <c>env -u</c> layer the way the non-isolated chain does:
///             <c>--clearenv</c> inside <c>bwrap</c> removes the bus address (and everything else) unconditionally.
///         </item>
///         <item>
///             Inside <c>bwrap</c>, every mount operation is applied in argument order, so the <c>--remount-ro</c>
///             flags must come after the binds they harden and the <c>--bind-fd</c> of the writable jail must come
///             after the <c>--dir /work</c> that creates the mount point.
///         </item>
///     </list>
/// </summary>
internal static class SandboxIsolatedChain
{
    /// <summary>
    ///     The hostname inside the UTS namespace. A fixed string rather than anything derived from the workload: the
    ///     jail's hostname is observable by everything running in it, and the host's real name is not something a
    ///     sandboxed workload needs to learn.
    /// </summary>
    public const string Hostname = "xe-compute";

    /// <summary>The writable jail's mount point inside the sandbox, and the command's working directory.</summary>
    public const string WorkPath = "/work";

    // Not a host directory. This is the mount point INSIDE the sandbox's own mount namespace, backed by a 0700
    // engine-owned jail subdirectory that only this one command can reach: no other process on the box shares that
    // namespace, so there is no publicly writable directory here to avoid. The name is fixed by every library that
    // reads TMPDIR, so "use a different directory" is not available.
    [SuppressMessage("Security Hotspot",
        "S5443:Using publicly writable directories is security-sensitive",
        Justification = "In-namespace mount point backed by a private 0700 jail subdirectory, not a host directory.")]
    private const string TempPath = "/tmp";

    /// <summary><c>HOME</c> inside the sandbox: a subdirectory of the jail, so everything it accumulates is metered.</summary>
    public const string HomePath = "/work/home";

    /// <summary><c>PATH</c> inside the sandbox. Only the two system directories the read-only <c>/usr</c> bind provides.</summary>
    public const string SandboxPath = "/usr/bin:/bin";

    /// <summary>
    ///     The numeric-library thread-count variables. They are pinned rather than left to the library's own core
    ///     detection because that detection reads the HOST's cpu count through <c>/proc</c>, which is not what the
    ///     scope's <c>CPUQuota</c> allows — an unpinned BLAS spawns a thread per host core and then thrashes inside a
    ///     fraction of one.
    /// </summary>
    public static readonly string[] ThreadCountVariableNames =
    [
        "OPENBLAS_NUM_THREADS",
        "OMP_NUM_THREADS",
        "MKL_NUM_THREADS",
        "NUMEXPR_NUM_THREADS"
    ];

    /// <summary>
    ///     The mount points a read-only tree may not sit under, because the chain itself puts a mount there.
    ///     <para>
    ///         Three of them (<c>/usr</c>, <c>/dev</c>, <c>/proc</c>) are mounted BEFORE the read-only trees, so
    ///         nesting under one means asking <c>bwrap</c> to create a directory inside a read-only bind, a devtmpfs
    ///         or procfs — none of which permits it. The other two (<c>/work</c> and the jail-backed <c>/tmp</c>) are
    ///         mounted AFTER them, so nesting under one means the tree is mounted and then silently SHADOWED: the
    ///         workload sees the jail there and no error is raised anywhere.
    ///     </para>
    ///     <para>
    ///         The silent case is why this is a rejection rather than a re-ordering. Re-ordering would move the
    ///         shadowing from one pair of paths to another, and a read-only tree that quietly is not there is exactly
    ///         the failure this whole layer exists to prevent. The legacy roots are covered because each is either a
    ///         symlink into <c>/usr</c> or its own read-only bind.
    ///     </para>
    /// </summary>
    [SuppressMessage("Security Hotspot",
        "S5443:Using publicly writable directories is security-sensitive",
        Justification = "These are in-namespace mount points the chain owns, listed so a caller cannot be shadowed by one; none is used as a host directory.")]
    public static readonly string[] ReservedMountPoints =
    [
        "/usr",
        "/dev",
        "/proc",
        "/work",
        TempPath,
        "/bin",
        "/sbin",
        "/lib",
        "/lib64",
        "/libx32"
    ];

    /// <summary>
    ///     <see langword="true" /> when a canonical host path can be bound read-only inside the jail without
    ///     colliding with a mount the chain places itself. Callers reject rather than reorder; see
    ///     <see cref="ReservedMountPoints" />.
    /// </summary>
    public static bool CanBindReadOnlyTree(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);

        var trimmed = canonicalPath.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            // The filesystem root: binding it would be the opposite of a boundary.
            return false;
        }

        return !ReservedMountPoints.Any(reserved => string.Equals(trimmed, reserved, StringComparison.Ordinal)
                                                   || trimmed.StartsWith(reserved + "/", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Renders the full argument vector: element 0 is the executable to start, the rest are its arguments.
    /// </summary>
    public static IReadOnlyList<string> Render(SandboxIsolatedChainInputs inputs, string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var chain = new List<string> { inputs.SetsidPath };

        AppendScope(chain, inputs);
        AppendNamespaces(chain, inputs);
        AppendSystemTrees(chain, inputs);
        AppendSyntheticEtc(chain, inputs);
        AppendJailTrees(chain, inputs);
        AppendReadOnlyPosture(chain);
        AppendEnvironment(chain, inputs);

        chain.Add("--die-with-parent");
        chain.Add("--new-session");
        chain.Add("--");
        chain.Add(executable);
        chain.AddRange(arguments);

        return chain;
    }

    private static void AppendScope(List<string> chain, SandboxIsolatedChainInputs inputs)
    {
        chain.Add(inputs.SystemdRunPath);
        chain.Add("--user");
        chain.Add("--scope");
        // Quiet: the "Running scope as unit …" banner would otherwise land in the captured stderr the caller surfaces.
        chain.Add("-q");
        // --collect garbage-collects the unit even when it fails, so a failed run leaves no unit behind for the next
        // launch to collide with.
        chain.Add("--collect");
        chain.Add(string.Create(CultureInfo.InvariantCulture, $"--unit={inputs.ScopeUnitName}"));
        // Without this systemd expands "$"-specifiers in the command line it is given. The command comes from a
        // caller, so leaving expansion on would let a "%i" or "$FOO" in an argument mean something to systemd.
        chain.Add("--expand-environment=no");
        // KillMode=control-group is what makes `systemctl kill` reach every process in the scope rather than only its
        // leader — the property the whole termination story rests on.
        chain.Add("-p");
        chain.Add("KillMode=control-group");
        // A ceiling the USER MANAGER enforces, so a scope survives the engine's own death by at most this long.
        chain.Add("-p");
        chain.Add(string.Create(CultureInfo.InvariantCulture, $"RuntimeMaxSec={inputs.RuntimeMaxSeconds}"));

        if (inputs.ResourceLimits?.MemoryMb is { } memoryMb and > 0)
        {
            chain.Add("-p");
            chain.Add(string.Create(CultureInfo.InvariantCulture, $"MemoryMax={memoryMb}M"));
            // Never emitted independently: with swap available MemoryMax alone does not OOM-kill, it reclaims.
            chain.Add("-p");
            chain.Add("MemorySwapMax=0");
        }

        if (inputs.ResourceLimits?.PidsLimit is { } pidsLimit and > 0)
        {
            chain.Add("-p");
            chain.Add(string.Create(CultureInfo.InvariantCulture, $"TasksMax={pidsLimit}"));
        }

        if (inputs.ResourceLimits?.CpuCount is { } cpuCount and > 0)
        {
            var percent = (long)Math.Ceiling(cpuCount * 100d);
            chain.Add("-p");
            chain.Add(string.Create(CultureInfo.InvariantCulture, $"CPUQuota={percent}%"));
        }

        chain.Add("--");
        chain.Add(inputs.BwrapPath);
    }

    private static void AppendNamespaces(List<string> chain, SandboxIsolatedChainInputs inputs)
    {
        chain.Add("--unshare-user");
        chain.Add("--uid");
        chain.Add(inputs.UserId.ToString(CultureInfo.InvariantCulture));
        chain.Add("--gid");
        chain.Add(inputs.GroupId.ToString(CultureInfo.InvariantCulture));
        chain.Add("--unshare-pid");
        chain.Add("--unshare-ipc");
        chain.Add("--unshare-uts");
        chain.Add("--unshare-net");
        chain.Add("--hostname");
        chain.Add(Hostname);
        // Nested user namespaces are the standard route back out of a namespace jail: with one, the workload can gain
        // capabilities in its own namespace and mount things. --disable-userns forbids creating them and
        // --assert-userns-disabled makes bwrap FAIL rather than continue if the kernel could not enforce that.
        chain.Add("--disable-userns");
        chain.Add("--assert-userns-disabled");
    }

    private static void AppendSystemTrees(List<string> chain, SandboxIsolatedChainInputs inputs)
    {
        chain.Add("--ro-bind");
        chain.Add("/usr");
        chain.Add("/usr");

        foreach (var entry in inputs.UsrMergeEntries)
        {
            if (entry.Action == SandboxUsrMergeAction.Symlink)
            {
                chain.Add("--symlink");
                chain.Add(entry.Target!);
                chain.Add(entry.Path);
                continue;
            }

            chain.Add("--ro-bind");
            chain.Add(entry.Path);
            chain.Add(entry.Path);
        }
    }

    private static void AppendSyntheticEtc(List<string> chain, SandboxIsolatedChainInputs inputs)
    {
        chain.Add("--dir");
        chain.Add("/etc");

        AppendEtcEntry(chain, inputs.PasswdDescriptor, "/etc/passwd");
        AppendEtcEntry(chain, inputs.GroupDescriptor, "/etc/group");
        AppendEtcEntry(chain, inputs.NameServiceSwitchDescriptor, "/etc/nsswitch.conf");
        AppendEtcEntry(chain, inputs.HostsDescriptor, "/etc/hosts");
    }

    private static void AppendEtcEntry(List<string> chain, int descriptor, string destination)
    {
        // --perms applies to the NEXT operation that takes a mode, so it is repeated per entry rather than set once.
        chain.Add("--perms");
        chain.Add("0444");
        chain.Add("--ro-bind-data");
        chain.Add(descriptor.ToString(CultureInfo.InvariantCulture));
        chain.Add(destination);
    }

    private static void AppendJailTrees(List<string> chain, SandboxIsolatedChainInputs inputs)
    {
        chain.Add("--dev");
        chain.Add("/dev");
        chain.Add("--proc");
        chain.Add("/proc");

        // Empty directories, so the names exist without exposing anything. /run in particular: leaving it absent
        // breaks tools that stat it, while binding the host's would hand over the session bus and any daemon socket.
        chain.Add("--dir");
        chain.Add("/home");
        chain.Add("--dir");
        chain.Add("/run");
        chain.Add("--dir");
        chain.Add("/var");
        chain.Add("--dir");
        chain.Add(TempPath);
        chain.Add("--dir");
        chain.Add(WorkPath);

        foreach (var tree in inputs.ReadOnlyTrees)
        {
            // Bound at the SAME canonical path it has on the host, so an interpreter that has absolute paths compiled
            // into it (a venv's pyvenv.cfg, a sysconfig prefix) finds what it expects.
            chain.Add("--ro-bind-fd");
            chain.Add(tree.FileDescriptor.ToString(CultureInfo.InvariantCulture));
            chain.Add(tree.Path);
        }

        chain.Add("--bind-fd");
        chain.Add(inputs.JailDescriptor.ToString(CultureInfo.InvariantCulture));
        chain.Add(WorkPath);
        // /tmp is jail-backed rather than a fresh tmpfs: a tmpfs would be RAM the memory ceiling does not see, and
        // anything written to it would escape the jail-occupancy watchdog entirely.
        chain.Add("--bind-fd");
        chain.Add(inputs.JailTempDescriptor.ToString(CultureInfo.InvariantCulture));
        chain.Add(TempPath);
        chain.Add("--chdir");
        chain.Add(inputs.WorkingDirectory);
    }

    private static void AppendReadOnlyPosture(List<string> chain)
    {
        // The root here is bwrap's own tmpfs, which is writable by default — so without this the workload could create
        // top-level directories and fill RAM. /dev and /proc are remounted too: the device nodes and the proc entries
        // remain readable, but nothing new can be created in either.
        chain.Add("--remount-ro");
        chain.Add("/");
        chain.Add("--remount-ro");
        chain.Add("/dev");
        chain.Add("--remount-ro");
        chain.Add("/proc");
    }

    private static void AppendEnvironment(List<string> chain, SandboxIsolatedChainInputs inputs)
    {
        // --clearenv first: everything after it is an explicit allow-list, so nothing the engine happens to carry —
        // including the systemd bus address the outer layer needed — can reach the workload.
        chain.Add("--clearenv");
        AppendVariable(chain, "PATH", SandboxPath);
        AppendVariable(chain, "HOME", HomePath);
        AppendVariable(chain, "PWD", WorkPath);
        AppendVariable(chain, "TMPDIR", TempPath);
        AppendVariable(chain, "TMP", TempPath);
        AppendVariable(chain, "TEMP", TempPath);
        AppendVariable(chain, "LANG", "C.UTF-8");
        AppendVariable(chain, "LC_ALL", "C.UTF-8");
        // No user site-packages, and no .pyc files written back next to the read-only interpreter.
        AppendVariable(chain, "PYTHONNOUSERSITE", "1");
        AppendVariable(chain, "PYTHONDONTWRITEBYTECODE", "1");

        var threads = inputs.ThreadLimit.ToString(CultureInfo.InvariantCulture);
        foreach (var name in ThreadCountVariableNames)
        {
            AppendVariable(chain, name, threads);
        }

        // Last, so a caller that genuinely needs to override PATH or HOME can. Ordered by name so the rendered vector
        // is a pure function of its input and can be asserted byte for byte.
        foreach (var pair in inputs.AdditionalEnvironment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AppendVariable(chain, pair.Key, pair.Value);
        }
    }

    private static void AppendVariable(List<string> chain, string name, string value)
    {
        chain.Add("--setenv");
        chain.Add(name);
        chain.Add(value);
    }
}
