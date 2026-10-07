namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

/// <summary>
///     The production <see cref="ISandboxContainmentProbe" />: measures what this host can really do by EXERCISING each mechanism once,
///     never by testing for the presence of a binary.
/// </summary>
/// <remarks>
///     A binary can exist and still fail — no user systemd bus, no delegated cgroup controllers, user namespaces disabled — and
///     advertising a capability because a file exists is the integrity gap this closes, so the probe starts a real constrained scope and a
///     real empty network namespace, each under a short timeout, and reports a mechanism available only when that succeeded. The result is
///     cached for the process lifetime, host containment not changing under a running worker. Probing is best-effort: a failure degrades
///     the mechanism to unavailable WITH a reason and never throws.
/// </remarks>
public sealed class HostSandboxContainmentProbe : ISandboxContainmentProbe
{
    // Each probe starts a trivial child (`true`) under the mechanism being measured. Generous enough for a loaded machine,
    // short enough that startup is never visibly delayed even when every probe fails.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The AppContainer boundary's reason on every host that is not Windows.</summary>
    internal const string NotWindowsReason = "the host is not Windows";

    /// <summary>The measured AppContainer mechanism's name (ADR 0019).</summary>
    internal const string MxcMechanism = "mxc-processcontainer";

    /// <summary>Why a Windows host has no ceilings: ProcessContainer has none and the Job Object path (S1) was not built.</summary>
    internal const string WindowsResourceLimitsReason =
        "Windows has no CPU, memory or process-count ceiling for a sandboxed child (the MXC ProcessContainer has none and the Job Object path is not implemented); a command is bounded only by its timeout";

    /// <summary>Why a Windows host has no SEPARATE egress mechanism: denial is part of the AppContainer boundary.</summary>
    internal const string WindowsNetworkIsolationReason =
        "Windows has no egress-denial mechanism of its own: the network is denied only inside the MXC AppContainer boundary, which serves only SandboxIsolationMode.Filesystem";

    // Standard locations for the wrapper binaries. PATH is consulted first; these are the fallback because the worker's
    // own PATH can be minimal under a service manager.
    private static readonly string[] BinarySearchDirectories =
    [
        "/usr/bin",
        "/bin",
        "/usr/local/bin",
        "/usr/sbin",
        "/sbin"
    ];

    // The default path's measurement, shared process-wide so each new host does not re-run systemd-run/unshare/bwrap. The first
    // instance publishes its Lazy and its logger writes the one probe line.
    private static Lazy<SandboxContainment>? processContainment;

    private readonly Lazy<SandboxContainment> _containment;
    private readonly Func<IReadOnlyDictionary<string, string>, SandboxFilesystemIsolationProbeResult> _filesystemProbe;
    private readonly Func<bool> _isWindows;
    private readonly ILogger<HostSandboxContainmentProbe> _logger;
    private readonly IMxcSandboxRuntime? _mxcRuntime;

    // The logger is optional so tests can construct the probe directly; ActivatorUtilities injects it in production.
    public HostSandboxContainmentProbe(ILogger<HostSandboxContainmentProbe>? logger = null)
        : this(logger,
            HostSandboxFilesystemIsolationProbe.Measure,
            OperatingSystem.IsWindows() ? new MxcSandboxRuntime() : null,
            OperatingSystem.IsWindows)
    {
        var own = _containment;
        _containment = LazyInitializer.EnsureInitialized(ref processContainment, () => own);
    }

    // The filesystem-isolation probe is injectable for one reason: a test must be able to make it FAIL and show the resource-limit and
    // network results survive intact. Instances built here keep their own cache, never the process-wide one.
    internal HostSandboxContainmentProbe(ILogger<HostSandboxContainmentProbe>? logger,
        Func<IReadOnlyDictionary<string, string>, SandboxFilesystemIsolationProbeResult> filesystemProbe)
        : this(logger, filesystemProbe, mxcRuntime: null, OperatingSystem.IsWindows)
    {
    }

    // The MXC runtime and the OS test are injectable so a Linux test drives the Windows branch through a fake runtime; the branch reaches
    // the SDK only through the IMxcSandboxRuntime seam, so nothing Windows-only runs on the test host.
    internal HostSandboxContainmentProbe(ILogger<HostSandboxContainmentProbe>? logger,
        Func<IReadOnlyDictionary<string, string>, SandboxFilesystemIsolationProbeResult> filesystemProbe,
        IMxcSandboxRuntime? mxcRuntime,
        Func<bool> isWindows)
    {
        ArgumentNullException.ThrowIfNull(filesystemProbe);
        ArgumentNullException.ThrowIfNull(isWindows);
        _logger = logger ?? NullLogger<HostSandboxContainmentProbe>.Instance;
        _filesystemProbe = filesystemProbe;
        _mxcRuntime = mxcRuntime;
        _isWindows = isWindows;
        _containment = new Lazy<SandboxContainment>(Measure, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public SandboxContainment Containment => _containment.Value;

    private SandboxContainment Measure()
    {
        try
        {
            return MeasureCore();
        }
        catch (Exception exception)
        {
            // A probe failure must never fail startup or a command. Degrade to the plain-child fallback, which is
            // exactly the pre-hardening behavior, and advertise nothing.
            _logger.LogWarning(exception, "Sandbox containment probe failed; running sandboxed children without containment.");
            return SandboxContainment.None with
            {
                ResourceLimitsUnavailableReason = "the containment probe failed",
                NetworkIsolationUnavailableReason = "the containment probe failed",
                FilesystemIsolationUnavailableReason = "the containment probe failed",
                AppContainerBoundaryUnavailableReason = "the containment probe failed"
            };
        }
    }

    private SandboxContainment MeasureCore()
    {
        if (_isWindows())
        {
            return MeasureWindows();
        }

        if (!OperatingSystem.IsLinux())
        {
            // Neither enforcement target: nothing is measured and nothing may be advertised.
            const string reason = "the host is neither Linux nor Windows";
            return SandboxContainment.None with
            {
                ResourceLimitsUnavailableReason = reason,
                NetworkIsolationUnavailableReason = reason,
                FilesystemIsolationUnavailableReason = reason,
                AppContainerBoundaryUnavailableReason = NotWindowsReason
            };
        }

        var setsid = ResolveBinary("setsid");
        var systemdRun = ResolveBinary("systemd-run");
        var unshare = ResolveBinary("unshare");
        var envBinary = ResolveBinary("env");
        var trueBinary = ResolveBinary("true") ?? "/bin/true";

        var (limitsActive, limitsReason, userBusEnvironment) = MeasureResourceLimits(setsid: setsid, systemdRun: systemdRun, envBinary: envBinary, trueBinary: trueBinary);
        var (networkActive, networkReason) = MeasureNetworkIsolation(unshare, trueBinary);
        var filesystem = MeasureFilesystemIsolation(CollectUserBusEnvironment());

        var containment = new SandboxContainment
        {
            FilesystemIsolation = filesystem.Isolation,
            FilesystemIsolationUnavailableReason = filesystem.Reason,
            SupportsProcessGroup = setsid is not null,
            SupportsResourceLimits = limitsActive,
            SupportsNetworkIsolation = networkActive,
            SetsidPath = setsid,
            SystemdRunPath = limitsActive ? systemdRun : null,
            UnsharePath = networkActive ? unshare : null,
            EnvPath = envBinary,
            UserBusEnvironment = userBusEnvironment,
            ResourceLimitsUnavailableReason = limitsReason,
            NetworkIsolationUnavailableReason = networkReason,
            // The AppContainer boundary is a Windows mechanism; a Linux host measures bwrap instead and never this.
            AppContainerBoundaryUnavailableReason = NotWindowsReason
        };

        _logger.LogInformation(
            "Sandbox containment probe: process group {ProcessGroup}, resource limits {Limits}{LimitsReason}, network isolation {Network}{NetworkReason}, filesystem isolation {Filesystem}{FilesystemReason}.",
            containment.SupportsProcessGroup,
            containment.SupportsResourceLimits,
            limitsReason is null ? string.Empty : $" ({limitsReason})",
            containment.SupportsNetworkIsolation,
            networkReason is null ? string.Empty : $" ({networkReason})",
            containment.SupportsFilesystemIsolation,
            filesystem.Reason is null ? string.Empty : $" ({filesystem.Reason})");

        return containment;
    }

    /// <summary>
    ///     The Windows measurement: the MXC ProcessContainer, read-only (<c>GetPlatformSupport</c> + <c>Probe</c> on the engine's own policy),
    ///     whatever the preview setting; eligibility is decided per call, never here (ADR 0019).
    /// </summary>
    /// <remarks>
    ///     The boundary is reported only when MXC accepts the policy AND reports no missing host preparation: MXC 1.0.0 warns rather than
    ///     fails when the one-time admin prep is absent, and children then fail to start, so a warned host is unavailable WITH the exact
    ///     command to run. The product never runs the prep itself. Ceilings and a separate egress mechanism stay unavailable, with reasons.
    /// </remarks>
    private SandboxContainment MeasureWindows()
    {
        var result = _mxcRuntime is null
            ? new MxcProbeResult { Supported = false, Warnings = [], Reason = "the MXC runtime is not available in this process" }
            : MxcProbe.Measure(_mxcRuntime);

        SandboxAppContainerBoundary? boundary = null;
        string? reason;
        if (result is { Supported: true, HostPrepMissing: false, Tier: { } tier })
        {
            boundary = new SandboxAppContainerBoundary
            {
                Mechanism = MxcMechanism,
                Tier = tier,
                Maturity = SandboxMechanismMaturity.Preview,
                Warnings = result.Warnings
            };
            reason = null;
        }
        else
        {
            reason = result.Supported ? DescribeMissingHostPrep(result.Warnings) : result.Reason ?? "MXC reported the ProcessContainer unavailable";
        }

        var containment = SandboxContainment.None with
        {
            AppContainerBoundary = boundary,
            AppContainerBoundaryUnavailableReason = reason,
            ResourceLimitsUnavailableReason = WindowsResourceLimitsReason,
            NetworkIsolationUnavailableReason = WindowsNetworkIsolationReason,
            FilesystemIsolationUnavailableReason = boundary is null
                ? $"the Windows filesystem boundary is the MXC AppContainer boundary, and it is unavailable on this host: {reason}"
                : "the Windows filesystem boundary is the MXC AppContainer boundary (Preview); it serves a role only while execution previews are enabled"
        };

        _logger.LogInformation(
            "Sandbox containment probe (Windows): AppContainer boundary {Available}{Detail}; resource limits unavailable; separate network isolation unavailable.",
            boundary is not null,
            boundary is null ? $" ({reason})" : $" (mechanism {boundary.Mechanism}, tier {boundary.Tier}, maturity {boundary.Maturity}, {boundary.Warnings.Count} warning(s))");

        return containment;
    }

    /// <summary>The unavailable reason for a host MXC warned is missing its one-time admin preparation, naming the exact commands.</summary>
    internal static string DescribeMissingHostPrep(IReadOnlyList<string> warnings)
    {
        var commands = new List<string>(2);
        if (warnings.Any(static warning => warning.Contains("prepare-system-drive", StringComparison.OrdinalIgnoreCase)))
        {
            commands.Add("'wxc-host-prep prepare-system-drive' (once per host)");
        }

        if (warnings.Any(static warning => warning.Contains("prepare-null-device", StringComparison.OrdinalIgnoreCase)))
        {
            commands.Add("'wxc-host-prep prepare-null-device' (after every boot)");
        }

        var action = commands.Count == 0
            ? "run the wxc-host-prep step MXC names below"
            : "run " + string.Join(" and ", commands);
        return $"MXC needs one-time administrator host preparation before an AppContainer child can start: as an administrator, {action} (wxc-host-prep.exe ships in the MXC 1.0.0 release assets, not in the NuGet package). MXC reported: {string.Join(" | ", warnings)}";
    }

    /// <summary>Runs the filesystem-isolation measurement inside its OWN guard.</summary>
    /// <remarks>
    ///     The separate catch is the point: this probe opens descriptors, creates memory files, starts a five-namespace chain and runs a
    ///     script inside it, so it has far more ways to fail, and a shared guard would let one of them erase resource-limit and network
    ///     results already measured. A host that cannot isolate the filesystem must keep every ceiling and egress denial it does have.
    /// </remarks>
    private SandboxFilesystemIsolationProbeResult MeasureFilesystemIsolation(IReadOnlyDictionary<string, string> userBusEnvironment)
    {
        try
        {
            return _filesystemProbe(userBusEnvironment);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The sandbox filesystem-isolation probe failed; the filesystem boundary will not be advertised.");

            return new SandboxFilesystemIsolationProbeResult
            {
                Isolation = null,
                Reason = $"the filesystem isolation probe threw: {exception.Message}"
            };
        }
    }

    /// <summary>
    ///     Measures whether a real memory, PID and CPU ceiling can be imposed, by starting a transient
    ///     <c>systemd-run --scope --user</c> carrying all three properties.
    /// </summary>
    /// <remarks>
    ///     A scope that starts proves the user systemd bus is reachable AND that the cgroup-v2 controllers backing those properties are
    ///     delegated to the user slice — the two conditions a presence check cannot establish.
    /// </remarks>
    private static ResourceLimitProbe MeasureResourceLimits(string? setsid,
        string? systemdRun,
        string? envBinary,
        string trueBinary)
    {
        var empty = (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal);

        if (systemdRun is null)
        {
            return new ResourceLimitProbe(Active: false, "systemd-run is not installed", empty);
        }

        if (envBinary is null)
        {
            // The env(1) layer strips the user-bus address back out before the sandboxed executable runs; without it the wrapper would
            // leave the child able to reach the bus and start a unit outside its own scope, so the mechanism is refused, not shipped.
            return new ResourceLimitProbe(Active: false, "env(1) is not installed, so the user-bus address could not be stripped from the child", empty);
        }

        if (setsid is null)
        {
            return new ResourceLimitProbe(Active: false, "setsid is not installed", empty);
        }

        // systemd-run --user reaches the per-user manager over the session bus. The worker's own environment is the only
        // place that address exists; if it is absent here, no scope can be started.
        var userBusEnvironment = CollectUserBusEnvironment();
        if (userBusEnvironment.Count == 0)
        {
            return new ResourceLimitProbe(Active: false, "neither XDG_RUNTIME_DIR nor DBUS_SESSION_BUS_ADDRESS is set, so the user systemd bus is unreachable", empty);
        }

        // Exercise the WHOLE chain, scope properties AND the env(1) strip layer, never a stand-in: a probe skipping a layer reports a
        // mechanism available while the chain fails at exec. MemorySwapMax is deliberate — MemoryMax alone reclaims to swap, never kills.
        var probeArguments = new List<string>
        {
            "--scope",
            "--user",
            "-q",
            "-p",
            "MemoryMax=64M",
            "-p",
            "MemorySwapMax=0",
            "-p",
            "TasksMax=16",
            "-p",
            "CPUQuota=100%",
            "--",
            envBinary
        };
        foreach (var name in SandboxLaunchPlan.UserBusVariableNames)
        {
            probeArguments.Add("-u");
            probeArguments.Add(name);
        }

        probeArguments.Add("--");
        probeArguments.Add(trueBinary);

        var probed = RunProbe(systemdRun, probeArguments, userBusEnvironment);

        return probed
            ? new ResourceLimitProbe(Active: true, Reason: null, userBusEnvironment)
            : new ResourceLimitProbe(Active: false, "a transient systemd user scope carrying MemoryMax/TasksMax/CPUQuota could not be started", empty);
    }

    /// <summary>Measures whether a fresh empty network namespace can be created unprivileged, by really creating one.</summary>
    /// <remarks>
    ///     That covers the ways it can be unavailable which a presence check misses: user namespaces disabled by sysctl or seccomp, an
    ///     exhausted <c>max_user_namespaces</c>, or a container runtime blocking the syscall.
    /// </remarks>
    private static NetworkIsolationProbe MeasureNetworkIsolation(string? unshare, string trueBinary)
    {
        if (unshare is null)
        {
            return new NetworkIsolationProbe(Active: false, "unshare is not installed");
        }

        var probed = RunProbe(unshare,
            [
                "--user",
                "--map-current-user",
                "--net",
                "--",
                trueBinary
            ],
            environment: null);

        return probed
            ? new NetworkIsolationProbe(Active: true, Reason: null)
            : new NetworkIsolationProbe(Active: false, "an unprivileged empty network namespace could not be created (user namespaces may be restricted)");
    }

    /// <summary>
    ///     Starts one bounded probe child and reports whether it exited 0. Output is discarded — only the exit status
    ///     matters. Any failure to start, a non-zero exit, or a timeout all mean "mechanism unavailable"; nothing here
    ///     propagates.
    /// </summary>
    private static bool RunProbe(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (environment is not null)
            {
                foreach (var pair in environment)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(ProbeTimeout))
            {
                // A hung probe is not a working mechanism. Kill the tree so the measurement leaves nothing behind.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
                {
                    // Already gone, or tree-kill unsupported — nothing further to do for a probe.
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Collects the worker's own user-bus addressing variables. These are injected into the WRAPPER's environment
    ///     only; <see cref="SandboxContainment.UserBusEnvironment" /> documents why they must not survive into the
    ///     sandboxed child.
    /// </summary>
    private static IReadOnlyDictionary<string, string> CollectUserBusEnvironment()
    {
        var collected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in SandboxLaunchPlan.UserBusVariableNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                collected[name] = value;
            }
        }

        return collected;
    }

    /// <summary>
    ///     Resolves a wrapper binary to an absolute path, preferring PATH and otherwise the standard system directories, so the chain
    ///     cannot be redirected by a PATH entry the sandboxed workload could influence.
    /// </summary>
    /// <remarks>
    ///     The candidate must be EXECUTABLE, not merely present, which a shell's own lookup checks and a bare existence check does not: a
    ///     non-executable file earlier on PATH — a stray <c>~/.local/bin/env</c> is enough — silently shadows the real binary and the
    ///     wrapper chain then fails at exec time with "Permission denied", after the probe already reported the mechanism available.
    /// </remarks>
    private static string? ResolveBinary(string name)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVariable))
        {
            foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = SafeCombine(directory, name);
                if (candidate is not null && IsExecutableFile(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var directory in BinarySearchDirectories)
        {
            var candidate = SafeCombine(directory, name);
            if (candidate is not null && IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary><see langword="true" /> when the path is a regular file carrying at least one execute bit.</summary>
    /// <remarks>
    ///     Any execute bit is accepted rather than resolving the exact owner and group question: the subsequent probe RUNS the resolved
    ///     chain, so a file that passes here and still cannot be exec'd is caught by measurement rather than believed.
    /// </remarks>
    private static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (!OperatingSystem.IsLinux())
            {
                // Unix permission bits are meaningless off Linux, and every caller here is already Linux-gated; fall
                // back to existence so this helper stays total.
                return true;
            }

            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != UnixFileMode.None;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static string? SafeCombine(string directory, string name)
    {
        try
        {
            return Path.Combine(directory, name);
        }
        catch (ArgumentException)
        {
            // An unparseable PATH entry simply does not contribute a candidate.
            return null;
        }
    }

    // Outcome of the resource-limit probe: whether a real ceiling can be imposed, why not when it cannot, and the
    // user-bus variables the launch path must carry into the scope (empty whenever the mechanism is unavailable).
    private sealed record ResourceLimitProbe(bool Active, string? Reason, IReadOnlyDictionary<string, string> UserBusEnvironment);

    // Outcome of the network-isolation probe: whether an empty network namespace can be created, and why not when it
    // cannot.
    private sealed record NetworkIsolationProbe(bool Active, string? Reason);
}
