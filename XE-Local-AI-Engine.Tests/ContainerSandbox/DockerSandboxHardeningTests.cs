namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The §3.8 hardening contract, guarantee by guarantee.
///     <para>
///         Each case weakens exactly one setting in the daemon's read-back and asserts that the verifier names that
///         setting. One-at-a-time matters: a verifier that rejected everything would pass a test that only ever
///         showed it a fully-broken container, and would then be indistinguishable from one that had genuinely
///         checked each guarantee. The conformant case pinned first is what makes the rest meaningful.
///     </para>
/// </summary>
public sealed class DockerSandboxHardeningTests
{
    [Test]
    public void FindViolations_WhenTheDaemonAppliedEverythingAsked_ReportsNone()
    {
        AssertEx.Empty(DockerSandboxHardening.FindViolations(Specification(), Conformant()));
    }

    [Test]
    public void FindViolations_WhenTheContainerWouldRunAsRoot_RejectsIt()
    {
        var violations = DockerSandboxHardening.FindViolations(Specification(), Conformant() with { User = "0:0" });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("non-root user", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenTheUserFieldIsUnset_RejectsIt()
    {
        // Docker's own default. An unset User means root, so an empty read-back is the single most likely way this
        // guarantee is lost in practice and must not read as "nothing to check".
        var violations = DockerSandboxHardening.FindViolations(Specification(), Conformant() with { User = string.Empty });

        AssertEx.Contains(violations, violation => violation.Contains("non-root user", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_AgainstARootlessDaemon_AcceptsUidZeroBecauseItIsNotHostRoot()
    {
        // The §3.8 rule as written is inverted under a rootless daemon: container uid 0 maps to the INVOKING USER's
        // unprivileged host account, and it is the only identity that can use an engine-generated bind mount there
        // (measured on Engine 29.6.1 rootless: --user 1000:1000 could not create a file in the mount at all). It still
        // has every capability dropped, no-new-privileges set and a read-only rootfs.
        var specification = Specification() with { User = "0:0" };

        AssertEx.Empty(DockerSandboxHardening.FindViolations(specification,
            Conformant() with { User = "0:0" },
            daemonIsRootless: true));
    }

    [Test]
    public void FindViolations_AgainstARootfulDaemon_StillRejectsUidZero()
    {
        // The relaxation is scoped to the daemon that earns it, and to nothing else.
        var specification = Specification() with { User = "0:0" };
        var violations = DockerSandboxHardening.FindViolations(specification, Conformant() with { User = "0:0" });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("non-root user", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_AnUnsetUserIsRejectedEvenAgainstARootlessDaemon()
    {
        // An empty User is Docker's default, i.e. "nobody decided". Rootless makes uid 0 acceptable as a CHOICE; it
        // does not make the absence of a choice acceptable.
        var specification = Specification() with { User = string.Empty };
        var violations = DockerSandboxHardening.FindViolations(specification,
            Conformant() with { User = string.Empty },
            daemonIsRootless: true);

        AssertEx.Contains(violations, violation => violation.Contains("non-root user", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenCapabilitiesWereNotDropped_RejectsIt()
    {
        var violations = DockerSandboxHardening.FindViolations(Specification(), Conformant() with { CapabilitiesDropped = [] });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("capability drop", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenACapabilityWasAdded_RejectsIt()
    {
        var violations = DockerSandboxHardening.FindViolations(Specification(), Conformant() with { CapabilitiesAdded = ["SYS_ADMIN"] });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("added capabilities", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenNoNewPrivilegesIsMissing_RejectsIt()
    {
        var violations = DockerSandboxHardening.FindViolations(Specification(), Conformant() with { SecurityOptions = [] });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("no-new-privileges", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenNoNewPrivilegesIsExplicitlyFalse_RejectsIt()
    {
        var violations = DockerSandboxHardening.FindViolations(Specification(),
            Conformant() with { SecurityOptions = ["no-new-privileges:false"] });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("no-new-privileges", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_AcceptsTheDaemonsAlternativeRenderingOfNoNewPrivileges()
    {
        // Guards the opposite failure: a cosmetic change in how the daemon spells this option must not become a
        // spurious rejection, or the control acquires the reputation that gets it turned off.
        AssertEx.Empty(DockerSandboxHardening.FindViolations(Specification(),
            Conformant() with { SecurityOptions = ["no-new-privileges"] }));
    }

    [Test]
    public void FindViolations_WhenTheContainerIsPrivileged_RejectsIt()
    {
        var violations = DockerSandboxHardening.FindViolations(Specification(), Conformant() with { Privileged = true });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("privileged", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenADeviceWasMapped_RejectsIt()
    {
        var violations = DockerSandboxHardening.FindViolations(Specification(), Conformant() with { DeviceCount = 1 });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("devices", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenAHostNamespaceIsShared_RejectsEachOne()
    {
        AssertEx.Contains(DockerSandboxHardening.FindViolations(Specification(), Conformant() with { PidMode = "host" }),
            violation => violation.Contains("PID namespace", StringComparison.Ordinal));
        AssertEx.Contains(DockerSandboxHardening.FindViolations(Specification(), Conformant() with { IpcMode = "host" }),
            violation => violation.Contains("IPC namespace", StringComparison.Ordinal));
        AssertEx.Contains(DockerSandboxHardening.FindViolations(Specification(), Conformant() with { UtsMode = "host" }),
            violation => violation.Contains("UTS namespace", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_TreatsAnEmptyNamespaceModeAsPrivate()
    {
        // The daemon reports "" for the private default (measured against Engine 29.6.1). Reading that as "unknown"
        // would fail every conformant container.
        AssertEx.Empty(DockerSandboxHardening.FindViolations(Specification(),
            Conformant() with { PidMode = string.Empty, UtsMode = string.Empty }));
    }

    [Test]
    public void FindViolations_WhenTheNetworkIsNotTheOneAskedFor_RejectsIt()
    {
        var violations = DockerSandboxHardening.FindViolations(Specification(), Conformant() with { NetworkMode = "bridge" });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("network mode", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenTheNetworkIsTheHostNamespace_RejectsIt()
    {
        var violations = DockerSandboxHardening.FindViolations(Specification(), Conformant() with { NetworkMode = "host" });

        AssertEx.Contains(violations, violation => violation.Contains("host network namespace", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenTheRootFilesystemIsWritable_RejectsIt()
    {
        var violations = DockerSandboxHardening.FindViolations(Specification(), Conformant() with { ReadOnlyRootFilesystem = false });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("read-only root filesystem", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenATmpfsIsAbsent_RejectsEachOne()
    {
        // Every requested tmpfs, named individually. A container that came back missing either of them is not the one
        // that was verified — and the temp mount going missing is the difference between a working toolchain and one
        // that fails EROFS before doing any work.
        var violations = DockerSandboxHardening.FindViolations(Specification(),
            Conformant() with { TemporaryFilesystems = new Dictionary<string, string>(StringComparer.Ordinal) });

        AssertEx.ContainsSingle(violations,
            violation => violation.Contains("'/scratch' is absent", StringComparison.Ordinal));
        AssertEx.ContainsSingle(violations,
            violation => violation.Contains("'/tmp' is absent", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenATmpfsIsUnbounded_RejectsIt()
    {
        // An unbounded tmpfs is host RAM with extra steps, and it is applied without error, so nothing but this check
        // would notice.
        var violations = DockerSandboxHardening.FindViolations(Specification(),
            Conformant() with
            {
                TemporaryFilesystems = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/scratch"] = "rw,noexec,nosuid,nodev",
                    ["/tmp"] = "rw,noexec,nosuid,nodev,size=1024"
                }
            });

        AssertEx.ContainsSingle(violations,
            violation => violation.Contains("'/scratch'", StringComparison.Ordinal)
                         && violation.Contains("no size bound", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenATmpfsCameBackWithoutTheRestrictionsItWasCreatedUnder_RejectsIt()
    {
        // The size bound was checked here long before these options were, which left the mount's whole justification
        // unverified: a daemon that applied `size=` but dropped `noexec` produced a container that passed. Asking for a
        // flag has never been evidence the flag took, and that rule now covers the flags too.
        var violations = DockerSandboxHardening.FindViolations(Specification(),
            Conformant() with
            {
                TemporaryFilesystems = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/scratch"] = "rw,nosuid,nodev,size=67108864",
                    ["/tmp"] = "rw,noexec,nosuid,nodev,size=67108864"
                }
            });

        AssertEx.ContainsSingle(violations,
            violation => violation.Contains("'/scratch'", StringComparison.Ordinal)
                         && violation.Contains("noexec", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenAMountOptionOnlyAppearsAsASubstring_DoesNotCountItAsPresent()
    {
        // Tokenized, not substring-matched. `noexec` contains `exec`, and `nodevtmpfs` contains `nodev` — a contains
        // check would accept an option list that carries neither restriction.
        var violations = DockerSandboxHardening.FindViolations(Specification(),
            Conformant() with
            {
                TemporaryFilesystems = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/scratch"] = "rw,noexecfoo,nosuidbar,nodevbaz,size=67108864",
                    ["/tmp"] = "rw,noexec,nosuid,nodev,size=67108864"
                }
            });

        AssertEx.ContainsSingle(violations,
            violation => violation.Contains("'/scratch'", StringComparison.Ordinal)
                         && violation.Contains("noexec", StringComparison.Ordinal)
                         && violation.Contains("nosuid", StringComparison.Ordinal)
                         && violation.Contains("nodev", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenMountPropagationIsNotPrivate_RejectsIt()
    {
        var violations = DockerSandboxHardening.FindViolations(Specification(),
            Conformant() with { Mounts = [Mount() with { Propagation = "rshared" }] });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("mount propagation", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenTheWorkspaceMountIsMissing_RejectsIt()
    {
        var violations = DockerSandboxHardening.FindViolations(Specification(), Conformant() with { Mounts = [] });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("is absent", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenTheContainerCarriesAMountTheEngineDidNotRequest_RejectsIt()
    {
        // D7's whole premise is that only engine-generated mounts exist. A mount nobody asked for is either a
        // daemon-side default this code has not accounted for or one somebody injected, and both are refusals.
        var violations = DockerSandboxHardening.FindViolations(Specification(),
            Conformant() with
            {
                Mounts =
                [
                    Mount(),
                    new DockerBindMount
                    {
                        HostPath = "/var/run/docker.sock",
                        ContainerPath = "/var/run/docker.sock",
                        ReadOnly = false,
                        Propagation = "private"
                    }
                ]
            });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("did not request", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenAReadOnlyMountCameBackWritable_RejectsIt()
    {
        var specification = Specification() with { BindMounts = [Mount() with { ReadOnly = true }] };
        var violations = DockerSandboxHardening.FindViolations(specification, Conformant() with { Mounts = [Mount()] });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("asked for read-only", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenAResourceCeilingWasNotApplied_RejectsEachOne()
    {
        AssertEx.Contains(DockerSandboxHardening.FindViolations(Specification(), Conformant() with { MemoryBytes = 0 }),
            violation => violation.Contains("memory limit", StringComparison.Ordinal) && violation.Contains("unlimited", StringComparison.Ordinal));
        AssertEx.Contains(DockerSandboxHardening.FindViolations(Specification(), Conformant() with { NanoCpus = 0 }),
            violation => violation.Contains("CPU limit", StringComparison.Ordinal));
        AssertEx.Contains(DockerSandboxHardening.FindViolations(Specification(), Conformant() with { PidsLimit = 0 }),
            violation => violation.Contains("PID limit", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_CollectsEveryViolationRatherThanStoppingAtTheFirst()
    {
        // Debugging a misconfigured daemon one restart at a time is how a security control earns the reputation that
        // gets it disabled.
        var violations = DockerSandboxHardening.FindViolations(Specification(),
            Conformant() with
            {
                User = "0:0",
                Privileged = true,
                ReadOnlyRootFilesystem = false,
                CapabilitiesDropped = []
            });

        AssertEx.True(violations.Count >= 4, $"Expected at least four violations, got {violations.Count}: {string.Join(" | ", violations)}");
    }

    [Test]
    public void BuildSpecification_ProducesASpecificationThatSatisfiesItsOwnContract()
    {
        // Guards the direction the per-guarantee cases cannot: that the specification the provider actually sends is
        // itself conformant, rather than merely being checked against conformant-looking test data.
        var specification = DockerSandboxHardening.BuildSpecification(Options(),
            new ResolvedContainerIdentity(UserId: 1000, GroupId: 1000),
            "xe-dev-test",
            "sandbox-1",
            [Mount()]);

        AssertEx.Equal("1000:1000", specification.User);
        AssertEx.Equal("none", specification.NetworkMode);
        AssertEx.True(specification.ReadOnlyRootFilesystem);
        AssertEx.Contains(specification.CapabilitiesToDrop, "ALL");
        AssertEx.Contains(specification.SecurityOptions, "no-new-privileges:true");
        AssertEx.Contains(specification.TemporaryFilesystems["/scratch"], "size=");
        AssertEx.Contains(specification.TemporaryFilesystems["/tmp"], "size=");
        AssertEx.Equal(expected: 512L * 1024 * 1024, specification.MemoryBytes);
        AssertEx.Equal(expected: 2_000_000_000L, specification.NanoCpus);
        AssertEx.Equal(expected: 256L, specification.PidsLimit);
    }

    [Test]
    public void BuildSpecification_MountsABoundedTmpfsAtTheToolchainTemporaryDirectory()
    {
        // The regression guard for the finding that blocked Development Mode's container provider outright. The .NET
        // runtime backs a named Mutex with shared-memory files under a path compiled into the CoreCLR PAL, and the
        // `dotnet` CLI takes such a mutex on its first invocation. The path honours no environment variable — the
        // engine already redirects TMPDIR/TMP/TEMP elsewhere and the runtime ignores all three — so with a read-only
        // root filesystem and no tmpfs here, every `dotnet` command failed EROFS before touching the project.
        //
        // Asserted at /tmp itself rather than the narrower /tmp/.dotnet on purpose: the PAL creates its directory by
        // mkdtemp-and-rename, which needs the PARENT writable, and a tmpfs mounted precisely at /tmp/.dotnet was
        // measured to fail with `mkdtemp(…) == nullptr; errno == EROFS`.
        var specification = DockerSandboxHardening.BuildSpecification(Options() with { TempSizeMb = 64 },
            new ResolvedContainerIdentity(UserId: 1000, GroupId: 1000),
            "xe-dev-test",
            "sandbox-1",
            [Mount()]);

        AssertEx.True(specification.TemporaryFilesystems.ContainsKey("/tmp"),
            "The toolchain temporary directory must be mounted, or no `dotnet` command can run at all. Present: "
            + string.Join(", ", specification.TemporaryFilesystems.Keys));

        var options = specification.TemporaryFilesystems["/tmp"];
        AssertEx.Contains(options, "noexec");
        AssertEx.Contains(options, "nosuid");
        AssertEx.Contains(options, "nodev");
        AssertEx.Contains(options, "size=67108864");
    }

    [Test]
    public void BuildSpecification_GivesEveryTmpfsTheSameRestrictions()
    {
        // The two mounts exist for different reasons but carry one contract between them. Asserted over the whole set
        // rather than per-name so a third mount cannot be added on weaker terms without this failing.
        var specification = Specification();

        foreach (var (target, options) in specification.TemporaryFilesystems)
        {
            AssertEx.Contains(options, "size=");

            foreach (var required in DockerSandboxHardening.RequiredTmpfsOptions)
            {
                AssertEx.True(options.Split(',').Contains(required),
                    $"tmpfs '{target}' is missing '{required}' (options '{options}').");
            }
        }
    }

    [Test]
    public void BuildSpecification_WhenUnrestrictedEgressIsAskedFor_UsesTheDefaultBridgeRatherThanNone()
    {
        var specification = DockerSandboxHardening.BuildSpecification(Options(),
            new ResolvedContainerIdentity(UserId: 1000, GroupId: 1000),
            "xe-dev-test",
            "sandbox-1",
            [Mount()],
            requestedLimits: null,
            SandboxNetworkPolicy.Unrestricted);

        AssertEx.Equal("bridge", specification.NetworkMode);
        // Everything else the contract requires is unchanged: only egress moved.
        AssertEx.True(specification.ReadOnlyRootFilesystem);
        AssertEx.Contains(specification.CapabilitiesToDrop, "ALL");
        AssertEx.Contains(specification.SecurityOptions, "no-new-privileges:true");
    }

    [Test]
    public void FindViolations_VerifiesTheRequestedNetworkModeRatherThanAssumingNone()
    {
        // The read-back must follow what was asked for. Pinning it to "none" would fail every legitimate Unrestricted
        // create; leaving it unchecked would let a daemon silently substitute one policy for another.
        var bridge = DockerSandboxHardening.BuildSpecification(Options(),
            new ResolvedContainerIdentity(UserId: 1000, GroupId: 1000),
            "xe-dev-test",
            "sandbox-1",
            [Mount()],
            requestedLimits: null,
            SandboxNetworkPolicy.Unrestricted);

        AssertEx.Empty(DockerSandboxHardening.FindViolations(bridge, Conformant() with { NetworkMode = "bridge" }));
        AssertEx.ContainsSingle(DockerSandboxHardening.FindViolations(bridge, Conformant() with { NetworkMode = "none" }),
            violation => violation.Contains("network mode", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_TheHostNetworkIsRejectedEvenWhenItIsWhatWasAskedFor()
    {
        // Unconditional by design: no requested policy makes sharing the host's network stack acceptable, and it is
        // the one mode that would put the daemon socket that created the container within its reach.
        var specification = Specification() with { NetworkMode = "host" };
        var violations = DockerSandboxHardening.FindViolations(specification, Conformant() with { NetworkMode = "host" });

        AssertEx.ContainsSingle(violations, violation => violation.Contains("host network namespace", StringComparison.Ordinal));
    }

    [Test]
    public async Task ResolveNetworkMode_ARestrictedEgressAllowListIsFailClosedRejected()
    {
        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(
            () => Task.FromResult(DockerSandboxHardening.ResolveNetworkMode(SandboxNetworkPolicy.Restricted)));

        AssertEx.Contains(exception.Message, "no mechanism");
    }

    internal static ContainerSandboxOptions Options()
    {
        return new ContainerSandboxOptions
        {
            Image = "example@sha256:" + new string('a', count: 64),
            UserId = 1000,
            GroupId = 1000,
            WorkspaceMountTarget = "/workspace",
            ScratchMountTarget = "/scratch",
            ScratchSizeMb = 64,
            MemoryMb = 512,
            CpuCount = 2,
            PidsLimit = 256
        };
    }

    internal static DockerBindMount Mount()
    {
        return new DockerBindMount
        {
            HostPath = "/host/workspace",
            ContainerPath = "/workspace",
            ReadOnly = false,
            Propagation = "private"
        };
    }

    internal static DockerContainerSpecification Specification()
    {
        return DockerSandboxHardening.BuildSpecification(Options(),
            new ResolvedContainerIdentity(UserId: 1000, GroupId: 1000),
            "xe-dev-test",
            "sandbox-1",
            [Mount()]);
    }

    internal static DockerContainerSettings Conformant()
    {
        var specification = Specification();
        return new DockerContainerSettings
        {
            ContainerId = "container-1",
            User = specification.User,
            NetworkMode = specification.NetworkMode,
            Privileged = false,
            ReadOnlyRootFilesystem = true,
            CapabilitiesDropped = specification.CapabilitiesToDrop,
            CapabilitiesAdded = [],
            SecurityOptions = specification.SecurityOptions,
            TemporaryFilesystems = specification.TemporaryFilesystems,
            Mounts = specification.BindMounts,
            MemoryBytes = specification.MemoryBytes,
            NanoCpus = specification.NanoCpus,
            PidsLimit = specification.PidsLimit,
            DeviceCount = 0,
            PidMode = string.Empty,
            IpcMode = "private",
            UtsMode = string.Empty
        };
    }
}
