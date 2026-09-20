namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

using System.Globalization;

/// <summary>
///     The minimum Docker hardening contract, in one place: it builds the container specification and verifies the settings the daemon
///     read back against what was asked for.
/// </summary>
/// <remarks>
///     Fail-closed. Passing a flag is not evidence the flag took — a daemon may ignore a setting it does not understand, a newer API may
///     rename one, and a socket an operator did not intend may be a daemon configured to do neither — so every guarantee is checked
///     against the daemon's own inspect output and any single unverified one rejects the container. There is deliberately no "log a
///     warning and continue" path: a warning would leave the caller holding a sandbox weaker than it asked for while believing otherwise.
/// </remarks>
internal static class DockerSandboxHardening
{
    /// <summary>Label marking a container as owned by this engine, so a later reaper can find it.</summary>
    internal const string OwnerLabel = "com.xe-local-ai-engine.sandbox";

    /// <summary>Label value for Development Mode containers.</summary>
    internal const string OwnerLabelValue = "development";

    /// <summary>Label carrying the attach key's sandbox id, so an attach can find its container by query.</summary>
    internal const string SandboxIdLabel = "com.xe-local-ai-engine.sandbox-id";

    /// <summary>Label carrying the id of the engine INSTALLATION that created the container.</summary>
    /// <remarks>
    ///     <see cref="OwnerLabel" /> cannot answer "is this container mine?", its value being a constant every installation on one daemon
    ///     carries, so a sweep keyed on it would remove a second installation's live container; <see cref="SandboxIdLabel" /> cannot
    ///     either, being a hash over an attach key that does not exist at startup. This label's value derives from the node data
    ///     directory, the one identity both stable across restarts and distinct per installation. A container older than the label carries
    ///     no install id and is never swept — deliberate, an unattributable container being the one a sweep must not guess about.
    /// </remarks>
    internal const string InstallLabel = "com.xe-local-ai-engine.sandbox-install";

    internal const string DropAllCapabilities = "ALL";
    internal const string NoNewPrivileges = "no-new-privileges:true";
    internal const string PrivateMountPropagation = "private";
    internal const string NoNetworkMode = "none";

    /// <summary>Docker's default bridge network: a private namespace with NAT egress, and no host interface.</summary>
    internal const string BridgeNetworkMode = "bridge";

    internal const string HostNamespaceMode = "host";

    /// <summary>The mount options every engine-created <c>tmpfs</c> carries, and that the read-back then re-checks.</summary>
    internal static readonly string[] RequiredTmpfsOptions = ["noexec", "nosuid", "nodev"];

    /// <summary>The Docker network mode that serves a requested policy, or a rejection for one that has no mechanism here.</summary>
    /// <remarks>
    ///     <see cref="SandboxNetworkPolicy.None" /> is the network namespace with nothing in it.
    ///     <see cref="SandboxNetworkPolicy.Unrestricted" /> is the default bridge, still a private namespace with no host interface but
    ///     with NAT egress, which is what <c>dotnet restore</c> needs until package-proxy machinery exists.
    ///     <see cref="SandboxNetworkPolicy.Restricted" /> is an egress allow-list and stays fail-closed rejected: there is no mechanism
    ///     for it here, and returning a bridge to a caller believing it had an allow-list is the silent weakening this contract prevents.
    /// </remarks>
    internal static string ResolveNetworkMode(SandboxNetworkPolicy policy)
    {
        return policy switch
        {
            SandboxNetworkPolicy.None => NoNetworkMode,
            SandboxNetworkPolicy.Unrestricted => BridgeNetworkMode,
            _ => throw new SandboxCapabilityNotSupportedException($"The docker sandbox provider has no mechanism for '{policy}'. It serves "
                                                                  + $"{nameof(SandboxNetworkPolicy.None)} (an empty network namespace) and "
                                                                  + $"{nameof(SandboxNetworkPolicy.Unrestricted)} (the default bridge). Restricted egress allow-lists "
                                                                  + "are unsupported.")
        };
    }

    /// <summary>
    ///     Mount options for an engine-created <c>tmpfs</c>: <c>noexec</c>, <c>nosuid</c> and <c>nodev</c> because a writable place is
    ///     where a dropped payload lands, and <c>size=</c> because an unbounded <c>tmpfs</c> is host RAM.
    /// </summary>
    /// <remarks>
    ///     The honest accounting is what makes a second <c>tmpfs</c> defensible. These are NOT the container's only writable surface — the
    ///     workspace bind and every engine-generated runtime mount are writable, carry no <c>noexec</c>, and an ELF dropped into the
    ///     workspace was measured to execute — so a <c>noexec</c> <c>tmpfs</c> widens nothing. Nor is <c>size=</c> the only memory bound:
    ///     <c>tmpfs</c> pages are charged to the container's memory cgroup, and a 1 GB <c>tmpfs</c> under a 256 MB limit was measured to
    ///     OOM-kill at ~254 MB. It is kept as the tighter belt, a per-mount bound failing the write rather than the container.
    /// </remarks>
    internal static string BuildTmpfsOptions(long sizeBytes)
    {
        return "rw," + string.Join(',', RequiredTmpfsOptions) + ",size=" + sizeBytes.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Build the hardening-contract-conformant specification for one sandbox container.</summary>
    /// <remarks>
    ///     <paramref name="requestedLimits" /> are the caller's ceilings and they WIN over the configured defaults, field by field: this
    ///     provider advertises <see cref="SandboxProviderCapabilities.SupportsResourceLimits" />, and advertising a capability while
    ///     quietly substituting your own numbers is the silent-ignore the fail-closed contract exists to prevent. A null field means "no
    ///     opinion", and only then does the configured default apply.
    /// </remarks>
    internal static DockerContainerSpecification BuildSpecification(ContainerSandboxOptions options,
        ResolvedContainerIdentity identity,
        string containerName,
        string sandboxId,
        string installId,
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
            // A long-lived idle process so the container stays up between execs. A shell loop rather than `sleep infinity`, on which
            // BusyBox and coreutils disagree — and the image is the operator's choice, not ours.
            Entrypoint = ["/bin/sh"],
            Command = ["-c", "while :; do sleep 3600; done"],
            NetworkMode = ResolveNetworkMode(networkPolicy),
            CapabilitiesToDrop = [DropAllCapabilities],
            // The seccomp profile is passed EXPLICITLY even though the daemon applies its default anyway, because a container created
            // without it reads back with no security option at all, indistinguishable from a daemon with seccomp off.
            SecurityOptions = [NoNewPrivileges, DockerSeccompProfile.SecurityOption],
            ReadOnlyRootFilesystem = true,
            // Two tmpfs mounts for two reasons: scratch is the writable area the sandbox contract offers a caller, while the temp mount
            // exists because the toolchain's shared-memory path is a compile-time constant honouring no environment variable.
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
                [SandboxIdLabel] = sandboxId,
                [InstallLabel] = installId
            }
        };
    }

    /// <summary>Compare what the daemon applied against what was asked for, and return every violation found.</summary>
    /// <param name="requested">What the engine asked the daemon for.</param>
    /// <param name="observed">What the daemon read back.</param>
    /// <param name="daemonIsRootless">
    ///     Whether the daemon is rootless, which moves exactly one rule: container UID 0 is then the invoking user's unprivileged host
    ///     account rather than host root.
    /// </param>
    /// <remarks>
    ///     All violations are collected rather than short-circuiting on the first: an operator debugging a misconfigured daemon needs the
    ///     whole list, and discovering them one restart at a time is how a security control earns the reputation that gets it disabled.
    ///     <paramref name="daemonIsRootless" /> relaxes a check the caller must then close with a real probe, inspect echoing the UID that
    ///     was ASKED for and never what it maps to.
    /// </remarks>
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

        // Belt and braces against a specification that was itself wrong: `0:0`, `0`, `root` and empty all mean root, and Docker defaults
        // to root when the field is unset. Empty is refused under EITHER daemon mode — an unset User is a default, never a decision.
        var uid = observed.User.Split(':', 2)[0];
        if (string.IsNullOrEmpty(uid))
        {
            violations.Add($"non-root user: the container would run as root ('{Describe(observed.User)}').");
            return;
        }

        var namesUidZero = string.Equals(uid, "0", StringComparison.Ordinal)
                           || string.Equals(uid, "root", StringComparison.OrdinalIgnoreCase);

        // Under a rootless daemon, container UID 0 is the invoking user's own unprivileged host account, still capability-dropped and
        // read-only-rooted, and the ONLY identity that can use an engine-generated bind mount. A hard refusal on a rootful daemon.
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
        // Matched by prefix: the daemon normalises this option's rendering between versions, and an exact match would turn a cosmetic
        // daemon change into a spurious fail-closed rejection.
        var present = observed.SecurityOptions.Any(option =>
            option.StartsWith("no-new-privileges", StringComparison.OrdinalIgnoreCase)
            && !option.EndsWith("false", StringComparison.OrdinalIgnoreCase));

        if (!present)
        {
            violations.Add($"no-new-privileges: not applied; the daemon reports [{Describe(observed.SecurityOptions)}].");
        }

        // Matched on "names a profile" rather than equality: the daemon echoes it back as compacted JSON, never the path. The refused
        // renderings are no seccomp option at all, which a daemon with seccomp disabled also reports, and `seccomp=unconfined`.
        if (!observed.SecurityOptions.Any(DockerSeccompProfile.NamesAProfile))
        {
            violations.Add($"seccomp: no profile is applied; the daemon reports [{Describe(observed.SecurityOptions)}]. "
                           + "A container with no seccomp option reads back the same way as one on a daemon with seccomp disabled, "
                           + "so this cannot be accepted as the daemon default.");
        }
    }

    /// <summary>
    ///     Renders the observed security options for an operator. The seccomp profile is ~9 KB of JSON, so it is
    ///     summarised rather than printed: a violation message nobody can read is a violation message nobody acts on.
    /// </summary>
    private static string Describe(IReadOnlyList<string> securityOptions)
    {
        return string.Join(", ",
            securityOptions.Select(static option => option.Length > 64
                ? option[..64] + "\u2026 (" + option.Length.ToString(CultureInfo.InvariantCulture) + " chars)"
                : option));
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

    /// <summary>Verifies the network mode that was REQUESTED, whatever it was, rather than a hardcoded "none".</summary>
    /// <remarks>
    ///     Egress denial is served only when the caller asks for it, so pinning this to "none" would fail every legitimate
    ///     <see cref="SandboxNetworkPolicy.Unrestricted" /> create while proving nothing extra about a
    ///     <see cref="SandboxNetworkPolicy.None" /> one. The host-namespace check is separate and unconditional because it is not a policy
    ///     question: no policy makes sharing the host's network stack acceptable, and it is the one mode that would let a container reach
    ///     the daemon socket that created it.
    /// </remarks>
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

            // Checked, not assumed: these options are the whole reason a writable tmpfs is acceptable under the hardening contract, and a
            // daemon that dropped `noexec` would produce a container passing verification without the property it was justified by.
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
            // The whole point of this check is that only engine-generated mounts exist. A mount nobody asked for is either a daemon-side
            // default this code has not accounted for or one somebody else injected, and both are reasons to refuse rather than guess.
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

    /// <summary>Whether a comma-separated mount-option string carries <paramref name="option" /> as a whole option.</summary>
    /// <remarks>
    ///     Tokenized rather than substring-matched, because a substring match on these names is wrong in both directions:
    ///     <c>"noexec"</c> contains <c>"exec"</c>, so looking for the permissive form finds the restrictive one, and <c>"nodevfoo"</c>
    ///     would satisfy a search for <c>"nodev"</c>. The daemon renders these as a comma-separated list, so whole-token is exact.
    /// </remarks>
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

/// <summary>The UID/GID a sandbox container runs as, resolved per create against the daemon that will run it.</summary>
/// <remarks>
///     The invariant is not "never zero" but "the container runs as the identity mapping to the engine's own host UID, and that identity
///     must not map to host root", which the two daemons answer oppositely. Rootful: an in-container UID maps straight through, so the
///     answer is the engine's own effective UID/GID and zero is host root, refused. Rootless: UID 0 maps to the invoking user and
///     <c>N&gt;0</c> to <c>subuid_base + N - 1</c>, so the answer is 0 — measured, <c>--user 1000:1000</c> could not write the workspace
///     mount at all. An operator-configured UID/GID wins over both, and a probe file proves the mapping.
/// </remarks>
public sealed class ResolvedContainerIdentity
{
    /// <summary>In-container UID.</summary>
    public required int UserId { get; init; }

    /// <summary>In-container GID.</summary>
    public required int GroupId { get; init; }

    /// <summary>The <c>uid:gid</c> string Docker's <c>User</c> field takes.</summary>
    public string UserSpecification => UserId.ToString(CultureInfo.InvariantCulture) + ":" + GroupId.ToString(CultureInfo.InvariantCulture);
}
