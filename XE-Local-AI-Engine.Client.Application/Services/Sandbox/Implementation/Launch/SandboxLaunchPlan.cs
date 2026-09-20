namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

using System.Globalization;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>The pure mapping from policy, measured host containment and the command to the exact wrapper chain that will be exec'd.</summary>
/// <code>
///     setsid                                                     process group leader, for group-kill and orphan reaping
///       systemd-run --scope --user -q -p MemoryMax=…M -p MemorySwapMax=0 -p TasksMax=… -p CPUQuota=…%   cgroup-v2 ceilings
///         -- unshare --user --map-current-user --net             empty network namespace, default-deny egress
///           -- env -u XDG_RUNTIME_DIR -u DBUS_SESSION_BUS_ADDRESS   strip the user-bus address, an escape guard
///             -- &lt;executable&gt; &lt;arguments…&gt;
/// </code>
/// <remarks>
///     A static function of its inputs, so the mapping is unit-testable without starting a process. Ordering is load-bearing and verified
///     live: <c>setsid</c> is outermost because, started from a .NET <c>Process.Start</c>, it EXECs rather than forks, so the started pid
///     IS the process-group id and the exit code still propagates; <c>systemd-run</c> sits OUTSIDE <c>unshare</c>, reaching the user bus
///     before the namespace is entered; <c>env -u</c> is innermost and emitted only under that layer, the only reason the bus address is
///     present at all — see <see cref="SandboxContainment.UserBusEnvironment" /> for the escape it closes.
/// </remarks>
public static class SandboxLaunchPlan
{
    /// <summary>
    ///     The environment variables that address the per-user systemd bus. Injected for the wrapper, stripped before
    ///     the sandboxed executable — see <see cref="SandboxContainment.UserBusEnvironment" />.
    /// </summary>
    public static readonly string[] UserBusVariableNames =
    [
        "XDG_RUNTIME_DIR",
        "DBUS_SESSION_BUS_ADDRESS"
    ];

    /// <summary>Builds the wrapper chain for one command.</summary>
    /// <remarks>
    ///     Never throws: a policy asking for a mechanism the host does not have yields a plan without that layer, and the corresponding
    ///     <c>Applied…</c> flag stays false so the caller can log the degradation honestly.
    /// </remarks>
    public static SandboxLaunchDescriptor Create(string executable,
        IReadOnlyList<string> arguments,
        SandboxLaunchPolicy policy,
        SandboxContainment containment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(containment);

        var limits = NormalizeLimits(policy.ResourceLimits);
        var applyLimits = limits is not null
                          && containment is { SupportsResourceLimits: true, SystemdRunPath: not null, EnvPath: not null };
        var applyNetwork = policy.DenyNetworkEgress
                           && containment is { SupportsNetworkIsolation: true, UnsharePath: not null };
        var applyProcessGroup = containment is { SupportsProcessGroup: true, SetsidPath: not null };

        // Nothing to wrap: the plain child, byte-identical to the pre-hardening launch.
        if (!applyLimits && !applyNetwork && !applyProcessGroup)
        {
            return new SandboxLaunchDescriptor
            {
                FileName = executable,
                Arguments = [.. arguments]
            };
        }

        // Built inside-out, then the outermost wrapper becomes FileName and the rest become its arguments.
        var chain = new List<string>();

        if (applyProcessGroup)
        {
            chain.Add(containment.SetsidPath!);
        }

        if (applyLimits)
        {
            chain.Add(containment.SystemdRunPath!);
            chain.Add("--scope");
            chain.Add("--user");
            // Quiet: without it systemd-run writes a "Running scope as unit …" banner to stderr on every command, which
            // would pollute the captured StandardError the AgentHome run flow surfaces to the user.
            chain.Add("-q");
            foreach (var property in BuildScopeProperties(limits!))
            {
                chain.Add("-p");
                chain.Add(property);
            }

            chain.Add("--");
        }

        if (applyNetwork)
        {
            chain.Add(containment.UnsharePath!);
            chain.Add("--user");
            // --map-current-user, NOT --map-root-user: the namespace needs a user namespace either way, and mapping the real uid keeps the
            // child running as itself. Mapping to root changes how tools behave inside versus outside, for no isolation benefit.
            chain.Add("--map-current-user");
            chain.Add("--net");
            chain.Add("--");
        }

        if (applyLimits)
        {
            // Innermost, and only under the limits layer: drop the user-bus address so the executable cannot reach the per-user systemd
            // manager. A network namespace does not confine UNIX sockets, so without this the child starts units outside its own scope.
            chain.Add(containment.EnvPath!);
            foreach (var name in UserBusVariableNames)
            {
                chain.Add("-u");
                chain.Add(name);
            }

            chain.Add("--");
        }

        chain.Add(executable);
        chain.AddRange(arguments);

        var wrapperEnvironment = applyLimits
            ? containment.UserBusEnvironment
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return new SandboxLaunchDescriptor
        {
            FileName = chain[0],
            Arguments = [.. chain.Skip(1)],
            AppliedProcessGroup = applyProcessGroup,
            AppliedResourceLimits = applyLimits,
            AppliedNetworkIsolation = applyNetwork,
            WrapperEnvironment = wrapperEnvironment
        };
    }

    /// <summary>The ISOLATED layer: the descriptor for a command that runs behind a filesystem boundary.</summary>
    /// <remarks>
    ///     A different chain rather than another optional wrapper, because the mechanisms overlap — the namespaces <c>bwrap</c> creates
    ///     subsume <c>unshare</c>, and <c>--clearenv</c> subsumes the <c>env -u</c> layer — and composing both would leave neither half's
    ///     guarantees legible. Everything is already decided: <paramref name="launch" /> holds the rendered vector and the unit name, and
    ///     the flags are facts about that vector. A command reaching here was accepted against a host the probe measured able to isolate,
    ///     so there is no degradation branch — an isolated launch either happens or the create request was refused.
    /// </remarks>
    internal static SandboxLaunchDescriptor CreateIsolated(SandboxIsolationLaunch launch,
        SandboxLaunchPolicy policy,
        SandboxContainment containment)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(containment);

        var chain = launch.Chain;

        return new SandboxLaunchDescriptor
        {
            FileName = chain[0],
            Arguments = [.. chain.Skip(1)],
            // setsid is unconditional in the isolated chain, so the pid started really is a process-group leader and
            // the PGID fallback kill has a legitimate target.
            AppliedProcessGroup = true,
            AppliedResourceLimits = NormalizeLimits(policy.ResourceLimits) is not null,
            // bwrap's --unshare-net is an empty network namespace by construction; the probe proves it by failing a
            // loopback connect that succeeds outside.
            AppliedNetworkIsolation = true,
            AppliedFilesystemIsolation = true,
            ScopeUnitName = launch.ScopeUnitName,
            LaunchResources = launch,
            // systemd-run --user still needs the bus address. The isolated chain needs no env -u layer to take it away
            // again: --clearenv inside bwrap removes the whole environment before the workload runs.
            WrapperEnvironment = containment.FilesystemIsolation?.UserBusEnvironment
                                 ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    /// <summary>Maps <see cref="SandboxResourceLimits" /> onto systemd resource-control properties.</summary>
    /// <remarks>
    ///     <c>MemorySwapMax=0</c> accompanies every <c>MemoryMax</c> and is not optional: on a host with swap, <c>memory.max</c> alone does
    ///     not produce an OOM kill, the kernel reclaiming to swap while the child allocates straight past the ceiling. Measured — 400 MiB
    ///     allocated successfully under <c>MemoryMax=128M</c>, and the same child SIGKILLed once <c>MemorySwapMax=0</c> was added.
    /// </remarks>
    private static IEnumerable<string> BuildScopeProperties(SandboxResourceLimits limits)
    {
        if (limits.MemoryMb is { } memoryMb)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"MemoryMax={memoryMb}M");
            yield return "MemorySwapMax=0";
        }

        if (limits.PidsLimit is { } pidsLimit)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"TasksMax={pidsLimit}");
        }

        if (limits.CpuCount is { } cpuCount)
        {
            // systemd expresses the CPU quota as a percentage of ONE core, so two cores is two hundred percent. Only whole percentages
            // are accepted, and rounding up keeps a fractional request from being silently tightened below what was asked for.
            var percent = (long)Math.Ceiling(cpuCount * 100d);
            yield return string.Create(CultureInfo.InvariantCulture, $"CPUQuota={percent}%");
        }
    }

    /// <summary>
    ///     Reduces a limits record to <see langword="null" /> when it carries no usable ceiling, so an empty or
    ///     non-positive request does not produce a <c>systemd-run</c> wrapper with no properties.
    /// </summary>
    private static SandboxResourceLimits? NormalizeLimits(SandboxResourceLimits? limits)
    {
        if (limits is null)
        {
            return null;
        }

        var normalized = new SandboxResourceLimits
        {
            MemoryMb = limits.MemoryMb is > 0 ? limits.MemoryMb : null,
            PidsLimit = limits.PidsLimit is > 0 ? limits.PidsLimit : null,
            CpuCount = limits.CpuCount is > 0 ? limits.CpuCount : null
        };

        return normalized.MemoryMb is null && normalized.PidsLimit is null && normalized.CpuCount is null
            ? null
            : normalized;
    }
}
