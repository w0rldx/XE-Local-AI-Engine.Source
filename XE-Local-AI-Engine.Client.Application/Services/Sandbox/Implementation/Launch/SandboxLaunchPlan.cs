namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

using System.Globalization;

/// <summary>
///     The pure, side-effect-free mapping from (requested policy × measured host containment × the command) to the exact
///     wrapper chain that will be exec'd. Keeping this a static function of its inputs is what makes the containment
///     mapping unit-testable without starting a process; <see cref="SandboxLauncher" /> is the thin adapter that applies
///     a plan to a <see cref="System.Diagnostics.ProcessStartInfo" />.
///     <para>
///         The chain, outermost first, with every layer independently optional:
///     </para>
///     <code>
///     setsid                                   ← process group leader (pgid == pid) for group-kill and orphan reaping
///       systemd-run --scope --user -q          ← memory / PID / CPU ceilings via cgroup v2
///         -p MemoryMax=…M -p MemorySwapMax=0
///         -p TasksMax=… -p CPUQuota=…%
///         -- unshare --user --map-current-user --net   ← empty network namespace: default-deny egress
///           -- env -u XDG_RUNTIME_DIR -u DBUS_SESSION_BUS_ADDRESS   ← strip the user-bus address (escape guard)
///             -- &lt;executable&gt; &lt;arguments…&gt;
///     </code>
///     <para>
///         Ordering is load-bearing and was verified live, not assumed:
///     </para>
///     <list type="bullet">
///         <item>
///             <c>setsid</c> must be outermost. Started from a .NET <c>Process.Start</c> (a fork/exec whose child is not
///             already a group leader) <c>setsid</c> EXECs rather than forks, so the started pid IS the process-group id
///             and the exit code of the whole chain still propagates. Both were measured.
///         </item>
///         <item>
///             <c>systemd-run</c> must sit OUTSIDE <c>unshare</c>: it talks to the user systemd bus, and it must do so
///             before the namespace is entered.
///         </item>
///         <item>
///             <c>env -u</c> must be innermost, and is emitted only when the <c>systemd-run</c> layer is present — that
///             layer is the only reason the bus address is in the environment at all. See
///             <see cref="SandboxContainment.UserBusEnvironment" /> for the escape this closes.
///         </item>
///     </list>
/// </summary>
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

    /// <summary>
    ///     Builds the wrapper chain for one command. Never throws: a policy asking for a mechanism the host does not
    ///     have simply yields a plan without that layer, and the corresponding <c>Applied…</c> flag stays false so the
    ///     caller can log the degradation honestly.
    /// </summary>
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
            // --map-current-user, NOT --map-root-user: creating the namespace needs a user namespace either way, but
            // mapping the real uid keeps the child running as itself. Mapping it to root would make tools that refuse
            // to run as root (or that change behavior when they think they are root, e.g. git ownership checks) behave
            // differently inside the sandbox than outside it, for no isolation benefit.
            chain.Add("--map-current-user");
            chain.Add("--net");
            chain.Add("--");
        }

        if (applyLimits)
        {
            // Innermost, and only under the limits layer: drop the user-bus address so the sandboxed executable cannot
            // reach the per-user systemd manager. A network namespace does not confine UNIX sockets, so without this
            // the child could start a unit outside its own scope and namespace — verified live.
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

    /// <summary>
    ///     Maps <see cref="SandboxResourceLimits" /> onto systemd resource-control properties.
    ///     <para>
    ///         <c>MemorySwapMax=0</c> accompanies every <c>MemoryMax</c> and is not optional. On a host with swap,
    ///         <c>memory.max</c> alone does not produce an OOM kill — the kernel reclaims to swap and the child
    ///         allocates straight past the ceiling. Measured on this box: 400 MiB allocated successfully under
    ///         <c>MemoryMax=128M</c>; with <c>MemorySwapMax=0</c> added the same child was SIGKILLed (exit 137).
    ///     </para>
    /// </summary>
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
            // systemd expresses the CPU quota as a percentage of ONE core, so two cores is two hundred percent. Only
            // whole percentages are accepted, and rounding up keeps a fractional request from being silently tightened
            // below what the caller asked for.
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
