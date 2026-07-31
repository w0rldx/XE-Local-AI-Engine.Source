namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

using System.Globalization;

/// <summary>
///     The §3.8 minimum Docker hardening contract, in one place: it builds the container specification, and it
///     verifies the settings the daemon read back against what was asked for.
///     <para>
///         §3.8 is <em>fail-closed</em>. Passing a flag is not evidence the flag took — a daemon may ignore a setting
///         it does not understand, a newer API may rename one, and a socket an operator did not intend may be a
///         daemon configured to do neither. So every guarantee is checked against the daemon's own inspect output,
///         and any single unverified guarantee rejects the container. There is no "log a warning and continue" path
///         here by design: a warning would leave the caller holding a sandbox weaker than it asked for while
///         believing otherwise, which is the exact failure the whole seam exists to prevent.
///     </para>
/// </summary>
internal static class DockerSandboxHardening
{
    /// <summary>Label marking a container as owned by this engine, so a later reaper can find it.</summary>
    internal const string OwnerLabel = "com.xe-local-ai-engine.sandbox";

    /// <summary>Label value for Development Mode containers.</summary>
    internal const string OwnerLabelValue = "development";

    /// <summary>Label carrying the attach key's sandbox id, so an attach can find its container by query.</summary>
    internal const string SandboxIdLabel = "com.xe-local-ai-engine.sandbox-id";

    internal const string DropAllCapabilities = "ALL";
    internal const string NoNewPrivileges = "no-new-privileges:true";
    internal const string PrivateMountPropagation = "private";
    internal const string NoNetworkMode = "none";

    /// <summary>Docker's default bridge network: a private namespace with NAT egress, and no host interface.</summary>
    internal const string BridgeNetworkMode = "bridge";

    internal const string HostNamespaceMode = "host";

    /// <summary>The mount options every engine-created <c>tmpfs</c> carries, and that the read-back then re-checks.</summary>
    internal static readonly string[] RequiredTmpfsOptions = ["noexec", "nosuid", "nodev"];

    /// <summary>
    ///     The Docker network mode that serves a requested policy, or a rejection for one that has no mechanism here.
    ///     <para>
    ///         <see cref="SandboxNetworkPolicy.None" /> is the network namespace with nothing in it.
    ///         <see cref="SandboxNetworkPolicy.Unrestricted" /> is the default bridge — still a private namespace with
    ///         no host interface, but with NAT egress, which is what Development Mode's <c>dotnet restore</c> needs
    ///         until the D6 package-proxy machinery exists. <see cref="SandboxNetworkPolicy.Restricted" /> is an egress
    ///         allow-list and stays fail-closed rejected: there is no mechanism for it here, and returning a bridge
    ///         while the caller believed it had an allow-list would be exactly the silent weakening this contract
    ///         exists to prevent.
    ///     </para>
    /// </summary>
    internal static string ResolveNetworkMode(SandboxNetworkPolicy policy)
    {
        return policy switch
        {
            SandboxNetworkPolicy.None => NoNetworkMode,
            SandboxNetworkPolicy.Unrestricted => BridgeNetworkMode,
            _ => throw new SandboxCapabilityNotSupportedException(
                $"The docker sandbox provider has no mechanism for '{policy}'. It serves "
                + $"{nameof(SandboxNetworkPolicy.None)} (an empty network namespace) and "
                + $"{nameof(SandboxNetworkPolicy.Unrestricted)} (the default bridge). A restricted egress allow-list is the "
                + "separate v2 package-proxy project (plan D6).")
        };
    }

    /// <summary>
    ///     Mount options for an engine-created <c>tmpfs</c>. <c>noexec</c>/<c>nosuid</c>/<c>nodev</c> are set because a
    ///     tmpfs is writable and a writable place is where a dropped payload lands; <c>size=</c> is set because an
    ///     unbounded <c>tmpfs</c> is host RAM.
    ///     <para>
    ///         Note what these options are and are not worth here, because the honest accounting is what makes a second
    ///         <c>tmpfs</c> defensible. They are NOT the container's only writable surface: the workspace bind mount and
    ///         every engine-generated runtime mount are writable and carry no <c>noexec</c>, and an ELF binary dropped
    ///         into the workspace was measured to execute. So a <c>noexec</c> <c>tmpfs</c> is strictly weaker than
    ///         surfaces that already exist, and adding one widens nothing. Nor is <c>size=</c> the only bound on memory:
    ///         <c>tmpfs</c> pages are charged to the container's memory cgroup, so the existing
    ///         <see cref="DockerContainerSpecification.MemoryBytes" /> ceiling already caps them — a 1 GB <c>tmpfs</c>
    ///         against a 256 MB limit was measured to OOM-kill the container at ~254 MB. <c>size=</c> is the second,
    ///         tighter belt, and it is kept because a per-mount bound fails the write rather than the container.
    ///     </para>
    /// </summary>
    internal static string BuildTmpfsOptions(long sizeBytes)
    {
        return "rw," + string.Join(',', RequiredTmpfsOptions) + ",size=" + sizeBytes.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Build the §3.8-conformant specification for one sandbox container.
    ///     <para>
    ///         <paramref name="requestedLimits" /> are the caller's ceilings and they WIN over the configured defaults,
    ///         field by field. This provider advertises
    ///         <see cref="SandboxProviderCapabilities.SupportsResourceLimits" />, and advertising a capability while
    ///         quietly substituting your own numbers is the same silent-ignore the fail-closed contract exists to
    ///         prevent — the caller would believe it received the ceiling it asked for. A null field means "no opinion",
    ///         and only then does the configured default apply.
    ///     </para>
    /// </summary>
    internal static DockerContainerSpecification BuildSpecification(ContainerSandboxOptions options,
        ResolvedContainerIdentity identity,
        string containerName,
        string sandboxId,
        IReadOnlyList<DockerBindMount> bindMounts,
        SandboxResourceLimits? requestedLimits = null,
        SandboxNetworkPolicy networkPolicy = SandboxNetworkPolicy.None)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(bindMounts);

        var scratchBytes = (long)options.ScratchSizeMb * 1024 * 1024;
        var tempBytes = (long)options.TempSizeMb * 1024 * 1024;
        var memoryMb = requestedLimits?.MemoryMb ?? options.MemoryMb;
        var cpuCount = requestedLimits?.CpuCount ?? options.CpuCount;
        var pidsLimit = requestedLimits?.PidsLimit ?? options.PidsLimit;

        return new DockerContainerSpecification
        {
            Image = options.Image!,
            Name = containerName,
            User = identity.UserSpecification,
            WorkingDirectory = options.WorkspaceMountTarget,
            // A long-lived idle process so the container stays up between execs. `sh -c 'while :; do sleep …'` rather
            // than `sleep infinity`: BusyBox and coreutils disagree on `sleep infinity`, and the image is the
            // operator's choice, not ours.
            Entrypoint = ["/bin/sh"],
            Command = ["-c", "while :; do sleep 3600; done"],
            NetworkMode = ResolveNetworkMode(networkPolicy),
            CapabilitiesToDrop = [DropAllCapabilities],
            SecurityOptions = [NoNewPrivileges],
            ReadOnlyRootFilesystem = true,
            // Two tmpfs mounts, for two different reasons. Scratch is the writable area the sandbox contract offers a
            // caller. The temp mount exists because the toolchain's shared-memory path is a compile-time constant that
            // honours no environment variable — see ContainerSandboxOptions.TempMountTarget for the measured evidence
            // and the upstream decision that makes it unrelocatable.
            TemporaryFilesystems = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [options.ScratchMountTarget] = BuildTmpfsOptions(scratchBytes),
                [options.TempMountTarget] = BuildTmpfsOptions(tempBytes)
            },
            BindMounts = bindMounts,
            MemoryBytes = (long)memoryMb * 1024 * 1024,
            NanoCpus = (long)Math.Round(cpuCount * 1_000_000_000d, MidpointRounding.AwayFromZero),
            PidsLimit = pidsLimit,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [OwnerLabel] = OwnerLabelValue,
                [SandboxIdLabel] = sandboxId
            }
        };
    }

    /// <summary>
    ///     Compare what the daemon applied against what was asked for, and return every violation found.
    ///     <para>
    ///         All violations are collected rather than short-circuiting on the first. An operator debugging a
    ///         misconfigured daemon needs the whole list; discovering them one restart at a time is how a
    ///         security control acquires the reputation that gets it disabled.
    ///     </para>
    /// </summary>
    /// <param name="requested">What the engine asked the daemon for.</param>
    /// <param name="observed">What the daemon read back.</param>
    /// <param name="daemonIsRootless">
    ///     Whether the daemon is rootless. It moves exactly one rule: under a rootless daemon container UID 0 is the
    ///     invoking user's unprivileged host account, not host root, so it is the identity §3.8's "not root" rule is
    ///     actually about — and the conventional non-root UID is the one that maps to a host account owning nothing.
    ///     Note what inspect can and cannot settle: it echoes back the UID that was <em>asked</em> for and can never
    ///     say what that UID maps to, so this flag relaxes a check the caller must then close with a real probe.
    /// </param>
    internal static IReadOnlyList<string> FindViolations(DockerContainerSpecification requested,
        DockerContainerSettings observed,
        bool daemonIsRootless = false)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(observed);

        var violations = new List<string>();

        VerifyUser(requested, observed, daemonIsRootless, violations);
        VerifyCapabilities(observed, violations);
        VerifySecurityOptions(observed, violations);
        VerifyPrivilegeAndDevices(observed, violations);
        VerifyNamespaces(observed, violations);
        VerifyNetwork(requested, observed, violations);
        VerifyFilesystem(requested, observed, violations);
        VerifyMounts(requested, observed, violations);
        VerifyResourceLimits(requested, observed, violations);

        return violations;
    }

    private static void VerifyUser(DockerContainerSpecification requested,
        DockerContainerSettings observed,
        bool daemonIsRootless,
        List<string> violations)
    {
        if (!string.Equals(requested.User, observed.User, StringComparison.Ordinal))
        {
            violations.Add($"non-root user: asked for '{requested.User}', the daemon reports '{Describe(observed.User)}'.");
            return;
        }

        // Belt and braces against a specification that was itself wrong: `0:0`, `0`, `root` and an empty value all
        // mean root, and Docker defaults to root whenever the field is unset. An empty value is refused under EITHER
        // daemon mode — an unset User is the daemon's default rather than a decision, and a rootless daemon does not
        // make "we never chose" acceptable.
        var uid = observed.User.Split(':', 2)[0];
        if (string.IsNullOrEmpty(uid))
        {
            violations.Add($"non-root user: the container would run as root ('{Describe(observed.User)}').");
            return;
        }

        var namesUidZero = string.Equals(uid, "0", StringComparison.Ordinal)
                           || string.Equals(uid, "root", StringComparison.OrdinalIgnoreCase);

        // Under a rootless daemon, UID 0 in the container is the invoking user's own unprivileged host account — it
        // still has every capability dropped, no-new-privileges set and a read-only root filesystem, and it is
        // strictly less privileged than the engine process that created it. Refusing it there would refuse the ONLY
        // identity that can use an engine-generated bind mount, so the rule inverts rather than relaxes. It stays a
        // hard refusal on a rootful daemon, where UID 0 is host root.
        if (namesUidZero && !daemonIsRootless)
        {
            violations.Add($"non-root user: the container would run as root ('{Describe(observed.User)}').");
        }
    }

    private static void VerifyCapabilities(DockerContainerSettings observed, List<string> violations)
    {
        if (!observed.CapabilitiesDropped.Any(capability => string.Equals(capability, DropAllCapabilities, StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add($"capability drop: '{DropAllCapabilities}' is not among the dropped capabilities "
                           + $"[{string.Join(", ", observed.CapabilitiesDropped)}].");
        }

        if (observed.CapabilitiesAdded.Count > 0)
        {
            violations.Add($"added capabilities: expected none, the daemon reports [{string.Join(", ", observed.CapabilitiesAdded)}].");
        }
    }

    private static void VerifySecurityOptions(DockerContainerSettings observed, List<string> violations)
    {
        // Matched by prefix: the daemon normalises this option's rendering between versions ("no-new-privileges",
        // "no-new-privileges:true", "no-new-privileges=true" have all appeared), and an exact match would turn a
        // cosmetic daemon change into a spurious fail-closed rejection.
        var present = observed.SecurityOptions.Any(option =>
            option.StartsWith("no-new-privileges", StringComparison.OrdinalIgnoreCase)
            && !option.EndsWith("false", StringComparison.OrdinalIgnoreCase));

        if (!present)
        {
            violations.Add($"no-new-privileges: not applied; the daemon reports [{string.Join(", ", observed.SecurityOptions)}].");
        }
    }

    private static void VerifyPrivilegeAndDevices(DockerContainerSettings observed, List<string> violations)
    {
        if (observed.Privileged)
        {
            violations.Add("privileged: the container is privileged.");
        }

        if (observed.DeviceCount > 0)
        {
            violations.Add($"devices: expected none, the daemon reports {observed.DeviceCount}.");
        }
    }

    private static void VerifyNamespaces(DockerContainerSettings observed, List<string> violations)
    {
        VerifyNotHostNamespace("PID", observed.PidMode, violations);
        VerifyNotHostNamespace("IPC", observed.IpcMode, violations);
        VerifyNotHostNamespace("UTS", observed.UtsMode, violations);
    }

    private static void VerifyNotHostNamespace(string namespaceName, string mode, List<string> violations)
    {
        // An empty mode is the daemon's "private" default and is correct. Only an explicit host share is a violation.
        if (mode.Equals(HostNamespaceMode, StringComparison.OrdinalIgnoreCase)
            || mode.StartsWith(HostNamespaceMode + ":", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add($"{namespaceName} namespace: shared with the host ('{mode}').");
        }
    }

    /// <summary>
    ///     Verifies the network mode that was <em>requested</em>, whatever it was — not a hardcoded "none". Egress
    ///     denial is served only when the caller asks for it, so pinning this check to "none" would fail every
    ///     legitimate <see cref="SandboxNetworkPolicy.Unrestricted" /> create while proving nothing extra about a
    ///     <see cref="SandboxNetworkPolicy.None" /> one. The host-namespace check is separate and unconditional
    ///     precisely because it is not a policy question: no requested policy makes sharing the host's network stack
    ///     acceptable, and it is the one mode that would let a container reach the daemon socket that created it.
    /// </summary>
    private static void VerifyNetwork(DockerContainerSpecification requested, DockerContainerSettings observed, List<string> violations)
    {
        if (!string.Equals(requested.NetworkMode, observed.NetworkMode, StringComparison.OrdinalIgnoreCase))
        {
            violations.Add($"network mode: asked for '{requested.NetworkMode}', the daemon reports '{Describe(observed.NetworkMode)}'.");
        }

        if (observed.NetworkMode.Equals(HostNamespaceMode, StringComparison.OrdinalIgnoreCase)
            || observed.NetworkMode.StartsWith(HostNamespaceMode + ":", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("network mode: the container shares the host network namespace.");
        }
    }

    private static void VerifyFilesystem(DockerContainerSpecification requested, DockerContainerSettings observed, List<string> violations)
    {
        if (!observed.ReadOnlyRootFilesystem)
        {
            violations.Add("read-only root filesystem: not applied.");
        }

        // Every requested tmpfs, not a named one. The set is engine-owned and has grown once already; a check pinned to
        // the scratch target would have silently stopped covering the mount that was added beside it.
        foreach (var (target, _) in requested.TemporaryFilesystems)
        {
            if (!observed.TemporaryFilesystems.TryGetValue(target, out var appliedOptions))
            {
                violations.Add($"tmpfs: '{target}' is absent from the created container.");
                continue;
            }

            if (!appliedOptions.Contains("size=", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"tmpfs: '{target}' has no size bound (options '{appliedOptions}'), so it is host memory.");
            }

            // Checked, not assumed. These options are the whole reason a writable tmpfs is acceptable under §3.8, and
            // until now only the size bound was read back — a daemon that dropped `noexec` would have produced a
            // container that passed verification while carrying the one property the mount was justified by. Same
            // fail-closed rule as everything else here: asking for a flag is not evidence the flag took.
            var missing = RequiredTmpfsOptions
                          .Where(option => !HasMountOption(appliedOptions, option))
                          .ToArray();

            if (missing.Length > 0)
            {
                violations.Add($"tmpfs: '{target}' is missing [{string.Join(", ", missing)}] (options '{Describe(appliedOptions)}'), "
                               + "so it is a writable mount without the restrictions it was created under.");
            }
        }
    }

    private static void VerifyMounts(DockerContainerSpecification requested, DockerContainerSettings observed, List<string> violations)
    {
        foreach (var expected in requested.BindMounts)
        {
            var applied = observed.Mounts.FirstOrDefault(mount =>
                string.Equals(mount.ContainerPath, expected.ContainerPath, StringComparison.Ordinal));

            if (applied is null)
            {
                violations.Add($"mount: '{expected.ContainerPath}' is absent from the created container.");
                continue;
            }

            if (!string.Equals(applied.Propagation, PrivateMountPropagation, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"mount propagation: '{expected.ContainerPath}' is '{Describe(applied.Propagation)}', not '{PrivateMountPropagation}'.");
            }

            if (expected.ReadOnly && !applied.ReadOnly)
            {
                violations.Add($"mount: '{expected.ContainerPath}' was asked for read-only and is writable.");
            }
        }

        var unexpected = observed.Mounts
                                 .Where(mount => !requested.BindMounts.Any(expected =>
                                     string.Equals(expected.ContainerPath, mount.ContainerPath, StringComparison.Ordinal)))
                                 .ToArray();

        if (unexpected.Length > 0)
        {
            // Not paranoia: the whole point of D7 is that only engine-generated mounts exist. A mount nobody asked for
            // is either a daemon-side default this code has not accounted for or a mount somebody else injected, and
            // both are reasons to refuse rather than to guess.
            violations.Add("mounts: the created container carries mounts the engine did not request "
                           + $"[{string.Join(", ", unexpected.Select(mount => mount.ContainerPath))}].");
        }
    }

    private static void VerifyResourceLimits(DockerContainerSpecification requested, DockerContainerSettings observed, List<string> violations)
    {
        if (observed.MemoryBytes != requested.MemoryBytes)
        {
            violations.Add($"memory limit: asked for {requested.MemoryBytes} bytes, the daemon reports {observed.MemoryBytes}"
                           + (observed.MemoryBytes == 0 ? " (unlimited)." : "."));
        }

        if (observed.NanoCpus != requested.NanoCpus)
        {
            violations.Add($"CPU limit: asked for {requested.NanoCpus} nano-CPUs, the daemon reports {observed.NanoCpus}"
                           + (observed.NanoCpus == 0 ? " (unlimited)." : "."));
        }

        if (observed.PidsLimit != requested.PidsLimit)
        {
            violations.Add($"PID limit: asked for {requested.PidsLimit}, the daemon reports {observed.PidsLimit}"
                           + (observed.PidsLimit == 0 ? " (unlimited)." : "."));
        }
    }

    /// <summary>
    ///     Whether a comma-separated mount-option string carries <paramref name="option" /> as a whole option.
    ///     <para>
    ///         Tokenized rather than substring-matched, because a substring match on these particular names is wrong in
    ///         both directions: <c>"noexec"</c> contains <c>"exec"</c>, so looking for the permissive form would find the
    ///         restrictive one, and an option like <c>"nodevfoo"</c> would satisfy a search for <c>"nodev"</c>. The
    ///         daemon renders these as a comma-separated list, so the whole-token comparison is the exact one.
    ///     </para>
    /// </summary>
    private static bool HasMountOption(string appliedOptions, string option)
    {
        return appliedOptions
               .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Any(token => string.Equals(token, option, StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(string value)
    {
        return string.IsNullOrEmpty(value) ? "<unset>" : value;
    }
}

/// <summary>
///     The UID/GID a sandbox container runs as, resolved per create against the daemon that will run it.
///     <para>
///         The invariant is not "never zero" — it is <em>the container must run as the identity that maps to the
///         engine's own host UID, and that identity must not map to host root</em>. Which UID satisfies that depends
///         on the daemon, and the two answers are opposites:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>Rootful daemon:</b> an in-container UID maps straight through, so the answer is the engine
///                 process's own effective UID/GID, and zero is host root and refused.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Rootless daemon:</b> container UID 0 maps to the invoking user and container UID <c>N&gt;0</c>
///                 maps to <c>subuid_base + N - 1</c>, so the answer is 0 — and the conventional 1000 is a host
///                 account that owns nothing of ours. Measured on Engine 29.6.1 rootless with
///                 <c>/etc/subuid</c> = <c>…:100000:65536</c>: <c>--user 1000:1000</c> could not create a file in the
///                 engine-generated workspace mount at all, while <c>--user 0:0</c> wrote files the engine then owned.
///             </description>
///         </item>
///     </list>
///     <para>
///         An operator-configured UID/GID still wins over both, because a daemon may map identities in a way neither
///         rule describes. And none of this is taken on trust: an inspect can only echo back the UID that was asked
///         for, never what it maps to, so the provider proves the mapping with a real probe file after creation.
///     </para>
/// </summary>
/// <param name="UserId">In-container UID.</param>
/// <param name="GroupId">In-container GID.</param>
public sealed record ResolvedContainerIdentity(int UserId, int GroupId)
{
    /// <summary>The <c>uid:gid</c> string Docker's <c>User</c> field takes.</summary>
    public string UserSpecification =>
        UserId.ToString(CultureInfo.InvariantCulture) + ":" + GroupId.ToString(CultureInfo.InvariantCulture);
}
