namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     The production <see cref="ISandboxContainmentProbe" />: measures what this host can really do by EXERCISING each
///     mechanism once, not by testing for the presence of a binary. A binary can exist and still fail (no user systemd
///     bus, no delegated cgroup controllers, user namespaces administratively disabled), and advertising a capability on
///     the strength of a file existing is precisely the integrity gap this work closes. The probe therefore starts a
///     real constrained scope and a real empty network namespace, each bounded by a short timeout, and reports a
///     mechanism as available only when that succeeded.
///     <para>
///         The result is cached for the lifetime of the process (<see cref="Lazy{T}" />, thread-safe): the provider is a
///         DI singleton and host containment does not change under a running worker. Probing is entirely best-effort —
///         any failure degrades the mechanism to unavailable WITH a reason and never throws into startup or the run
///         flow.
///     </para>
/// </summary>
public sealed class HostSandboxContainmentProbe : ISandboxContainmentProbe
{
    // Each probe starts a trivial child (`true`) under the mechanism being measured. Generous enough for a loaded box,
    // short enough that startup is never visibly delayed even when every probe fails.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

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

    private readonly Lazy<SandboxContainment> _containment;
    private readonly Func<IReadOnlyDictionary<string, string>, SandboxFilesystemIsolationProbeResult> _filesystemProbe;
    private readonly ILogger<HostSandboxContainmentProbe> _logger;

    // The logger is optional so tests can construct the probe directly; ActivatorUtilities injects it in production.
    public HostSandboxContainmentProbe(ILogger<HostSandboxContainmentProbe>? logger = null)
        : this(logger, HostSandboxFilesystemIsolationProbe.Measure)
    {
    }

    // The filesystem-isolation probe is injectable for one reason: a test has to be able to make it FAIL and show that
    // the resource-limit and network results survive that failure intact. That independence is a contract, not an
    // implementation detail, so it is testable rather than asserted in prose.
    internal HostSandboxContainmentProbe(ILogger<HostSandboxContainmentProbe>? logger,
        Func<IReadOnlyDictionary<string, string>, SandboxFilesystemIsolationProbeResult> filesystemProbe)
    {
        ArgumentNullException.ThrowIfNull(filesystemProbe);
        _logger = logger ?? NullLogger<HostSandboxContainmentProbe>.Instance;
        _filesystemProbe = filesystemProbe;
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
                FilesystemIsolationUnavailableReason = "the containment probe failed"
            };
        }
    }

    private SandboxContainment MeasureCore()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Linux is the only enforcement target that shipped. The Windows Job Object containment path was designed
            // but deliberately not built, so a Windows host contains nothing and must advertise nothing. This is a live
            // containment gap on Windows, not a temporary state of this file.
            const string reason = "the host is not Linux (the Windows Job Object path is not implemented)";
            return SandboxContainment.None with
            {
                ResourceLimitsUnavailableReason = reason,
                NetworkIsolationUnavailableReason = reason,
                FilesystemIsolationUnavailableReason = reason
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
            NetworkIsolationUnavailableReason = networkReason
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
    ///     Runs the filesystem-isolation measurement inside its OWN guard.
    ///     <para>
    ///         The separate catch is the point. This probe does far more than the other two — it opens descriptors,
    ///         creates memory files, starts a five-namespace chain and runs a shell script inside it — so it has far
    ///         more ways to fail, and a shared guard would let one of those failures erase the resource-limit and
    ///         network-isolation results that were already measured successfully. A host that cannot isolate the
    ///         filesystem must keep every ceiling and every egress denial it does have.
    ///     </para>
    /// </summary>
    private SandboxFilesystemIsolationProbeResult MeasureFilesystemIsolation(IReadOnlyDictionary<string, string> userBusEnvironment)
    {
        try
        {
            return _filesystemProbe(userBusEnvironment);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The sandbox filesystem-isolation probe failed; the filesystem boundary will not be advertised.");

            return new SandboxFilesystemIsolationProbeResult(Isolation: null, $"the filesystem isolation probe threw: {exception.Message}");
        }
    }

    /// <summary>
    ///     Measures whether a real memory / PID / CPU ceiling can be imposed, by starting a transient
    ///     <c>systemd-run --scope --user</c> carrying all three properties. A scope that starts proves the user systemd
    ///     bus is reachable AND that the cgroup-v2 controllers backing those properties are delegated to the user slice —
    ///     the two conditions a presence check cannot establish.
    /// </summary>
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
            // The env(1) layer is what strips the user-bus address back out before the sandboxed executable runs.
            // Without it the resource-limit wrapper would leave the child able to reach the user systemd bus and start a
            // unit outside its own scope, so the mechanism is refused rather than shipped with that hole.
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

        // Exercise the WHOLE chain the launch path builds — scope properties AND the env(1) strip layer — not a
        // simplified stand-in. A probe that skips a layer can report a mechanism as available while the real chain
        // fails at exec time, which is the same class of dishonesty as advertising an unenforced capability. (This is
        // not hypothetical: a non-executable `env` earlier on PATH broke the real chain while a layer-skipping probe
        // still reported success.)
        //
        // MemorySwapMax is included deliberately: with swap available, MemoryMax alone does NOT produce an OOM kill —
        // the kernel reclaims to swap and the child sails past the ceiling. Both are required for the ceiling to be
        // real (verified live).
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

    /// <summary>
    ///     Measures whether a fresh empty network namespace can be created unprivileged, by really creating one. This
    ///     covers the several ways it can be unavailable that a presence check misses: user namespaces disabled by
    ///     sysctl or seccomp, an exhausted <c>max_user_namespaces</c>, or a container runtime that blocks the syscall.
    /// </summary>
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
    ///     Resolves a wrapper binary to an absolute path, preferring PATH and falling back to the standard system
    ///     directories. An absolute path is used at launch so the wrapper chain cannot be redirected by a PATH entry the
    ///     sandboxed workload could influence.
    ///     <para>
    ///         The candidate must be EXECUTABLE, not merely present. A bare existence check is what a shell's own
    ///         lookup does not do, and the difference is not academic: a non-executable file earlier on PATH (a stray
    ///         <c>~/.local/bin/env</c> is enough) silently shadows the real binary, and the whole wrapper chain then
    ///         fails at exec time with "Permission denied" — after the probe has already reported the mechanism as
    ///         available. Skipping it here keeps resolution and advertisement honest together.
    ///     </para>
    /// </summary>
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

    /// <summary>
    ///     <see langword="true" /> when the path is a regular file carrying at least one execute bit. Any execute bit
    ///     is accepted rather than resolving the exact owner/group question — the subsequent probe RUNS the resolved
    ///     chain, so a file that passes here but still cannot be exec'd is caught by measurement rather than believed.
    /// </summary>
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
