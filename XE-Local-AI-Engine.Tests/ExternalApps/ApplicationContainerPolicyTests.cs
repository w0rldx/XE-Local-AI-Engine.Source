namespace XE_Local_AI_Engine.Tests.ExternalApps;

using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     One test per violation class, each built by starting from an inspection that agrees with the specification
///     and then lying about exactly one field. "We passed the flag" is not verification; what the daemon says it
///     created is.
/// </summary>
public sealed class ApplicationContainerPolicyTests
{
    private const string InstallId = "install-1";

    private static readonly Guid InstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Test]
    public void Policy_SetsNoUser()
    {
        var specification = Specification();

        AssertEx.Null(specification.User, "The engine never passes a user; the images drop privileges themselves.");
    }

    [Test]
    public void Policy_DropsAllCapabilitiesAndAddsOnlyTheManifestsOwn()
    {
        var specification = Specification(service: ExternalAppTestManifests.Service("app", capAdd: ["CHOWN", "SETGID"]));

        AssertEx.Equal("ALL", specification.CapabilitiesToDrop.Single());
        AssertEx.Equal("CHOWN,SETGID", string.Join(",", specification.CapabilitiesToAdd));
    }

    [Test]
    public void Policy_WithACapabilityOutsideTheAllowList_RefusesBeforeAnyCreate()
    {
        var exception = AssertEx.Throws<ContainerPolicyException>(() => Specification(service: ExternalAppTestManifests.Service("app", capAdd: ["SYS_ADMIN"])));

        AssertEx.Equal(ApplicationContainerPolicy.CapabilityNotAllowedReason, exception.Reason);
        AssertEx.Contains(exception.Message, "SYS_ADMIN");
    }

    [Test]
    public void Policy_WithATaggedImage_RefusesBeforeAnyCreate()
    {
        var exception = AssertEx.Throws<ContainerPolicyException>(() => Specification(service: ExternalAppTestManifests.Service("app", image: "ghcr.io/example/app:1.0.0")));

        AssertEx.Equal(ApplicationContainerPolicy.ImageNotDigestPinnedReason, exception.Reason);
    }

    [Test]
    public void Policy_WithAnUndeclarableExtraHost_RefusesBeforeAnyCreate()
    {
        var exception = AssertEx.Throws<ContainerPolicyException>(() => Specification(service: ExternalAppTestManifests.Service("app", extraHosts: ["evil.invalid:10.0.0.1"])));

        AssertEx.Equal(ApplicationContainerPolicy.ExtraHostNotAllowedReason, exception.Reason);
    }

    [Test]
    public void Policy_WithHostGateway_EmitsTheOneRepresentableEntry()
    {
        var specification = Specification(service: ExternalAppTestManifests.Service("app", extraHosts: ["host-gateway"]));

        AssertEx.Equal("host.docker.internal:host-gateway", specification.ExtraHosts.Single());
    }

    [Test]
    public void Policy_LeavesMemoryAndCpuUnlimitedAndAppliesThePidsCeiling()
    {
        var specification = Specification();

        AssertEx.Equal(expected: 0L, specification.MemoryBytes);
        AssertEx.Equal(expected: 0L, specification.NanoCpus);
        AssertEx.Equal(expected: 512L, specification.PidsLimit);
        AssertEx.Equal(ContainerRestartMode.UnlessStopped, specification.RestartMode);
    }

    [Test]
    public void FindViolations_WhenTheDaemonAgrees_ReportsNothing()
    {
        var specification = Specification();

        AssertEx.Empty(ApplicationContainerPolicy.FindViolations(specification, Inspection(specification), daemonIsRootless: true, afterStart: false));
        AssertEx.Empty(ApplicationContainerPolicy.FindViolations(specification, Inspection(specification, afterStart: true), daemonIsRootless: true, afterStart: true));
    }

    [Test]
    public void FindViolations_WhenCapabilitiesWereNotDropped_ReportsIt()
    {
        var specification = Specification();
        var observed = Inspection(specification) with
        {
            CapabilitiesDropped = []
        };

        AssertEx.Contains(Violations(specification, observed), static violation => violation.Contains("drop ALL", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenAnUnrequestedCapabilityIsHeld_ReportsItByName()
    {
        var specification = Specification();
        var observed = Inspection(specification) with
        {
            CapabilitiesAdded = ["SYS_ADMIN"]
        };

        AssertEx.Contains(Violations(specification, observed), static violation => violation.Contains("SYS_ADMIN", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenSeccompOrNoNewPrivilegesIsMissing_ReportsIt()
    {
        var specification = Specification();

        AssertEx.Contains(Violations(specification, Inspection(specification) with
            {
                SecurityOptions = ["seccomp=profile.json"]
            }),
            static violation => violation.Contains("no-new-privileges", StringComparison.Ordinal));
        AssertEx.Contains(Violations(specification, Inspection(specification) with
            {
                SecurityOptions = ["no-new-privileges:true"]
            }),
            static violation => violation.Contains("seccomp", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A container whose confinement was switched off names both options. Reuse and boot adoption read this
    ///     verdict, so "the option is mentioned" is not evidence that the protection is on.
    /// </summary>
    [Test]
    public void FindViolations_WhenSeccompCameBackUnconfined_ReportsIt()
    {
        var specification = Specification();
        var observed = Inspection(specification) with
        {
            SecurityOptions = [ApplicationContainerPolicy.NoNewPrivileges, DockerSeccompProfile.OptionPrefix + DockerSeccompProfile.Unconfined]
        };

        AssertEx.Contains(Violations(specification, observed), static violation => violation.Contains("seccomp", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenNoNewPrivilegesCameBackFalse_ReportsIt()
    {
        var specification = Specification();
        var observed = Inspection(specification) with
        {
            SecurityOptions = ["no-new-privileges:false", "seccomp=profile.json"]
        };

        AssertEx.Contains(Violations(specification, observed), static violation => violation.Contains("no-new-privileges", StringComparison.Ordinal));
    }

    /// <summary>The accepted set is symmetric with what the specification writes, plus the daemon's bare rendering.</summary>
    [Test]
    public void FindViolations_AcceptsTheHardenedRenderingsOfBothOptions()
    {
        var specification = Specification();

        AssertEx.Contains(specification.SecurityOptions, static option => string.Equals(option, ApplicationContainerPolicy.NoNewPrivileges, StringComparison.Ordinal));
        AssertEx.Empty(Violations(specification, Inspection(specification)));
        AssertEx.Empty(Violations(specification,
            Inspection(specification) with
            {
                SecurityOptions = ["no-new-privileges", "seccomp=profile.json"]
            }));
    }

    [Test]
    public void FindViolations_WhenTheContainerIsPrivilegedOrHasADevice_ReportsIt()
    {
        var specification = Specification();

        AssertEx.Contains(Violations(specification, Inspection(specification) with
            {
                Privileged = true
            }),
            static violation => violation.Contains("privileged", StringComparison.Ordinal));
        AssertEx.Contains(Violations(specification, Inspection(specification) with
            {
                DeviceCount = 1
            }),
            static violation => violation.Contains("device", StringComparison.Ordinal));
    }

    [Test]
    [Arguments("pid")]
    [Arguments("ipc")]
    [Arguments("uts")]
    public void FindViolations_WhenAHostNamespaceIsShared_ReportsIt(string namespaceName)
    {
        var specification = Specification();
        var baseline = Inspection(specification);
        var observed = namespaceName switch
        {
            "pid" => baseline with
            {
                PidMode = "host"
            },
            "ipc" => baseline with
            {
                IpcMode = "host"
            },
            _ => baseline with
            {
                UtsMode = "host"
            }
        };

        AssertEx.Contains(Violations(specification, observed), violation => violation.Contains(namespaceName, StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenTheContainerJoinedAnotherNetwork_ReportsIt()
    {
        var specification = Specification();
        var observed = Inspection(specification) with
        {
            NetworkMode = "bridge"
        };

        AssertEx.Contains(Violations(specification, observed), static violation => violation.Contains("bridge", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenTheContainerIsOnTheHostNetwork_ReportsIt()
    {
        var specification = Specification();
        var observed = Inspection(specification) with
        {
            NetworkMode = "host"
        };

        AssertEx.Contains(Violations(specification, observed), static violation => violation.Contains("host network", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The mount an image's own <c>VOLUME</c> instruction creates appears in the inspect and in no request. It
    ///     would put application data outside the instance directory, where reset and uninstall cannot reach it, so
    ///     it is a violation naming the container path rather than a silent allowance.
    /// </summary>
    [Test]
    public void FindViolations_WhenTheImageCreatedAnAnonymousVolume_ReportsItByContainerPath()
    {
        var specification = Specification(storage: [new ApplicationStorage("data", "/app/data")]);
        var observed = Inspection(specification);
        observed = observed with
        {
            Mounts =
            [
                .. observed.Mounts,
                new ContainerMountView
                {
                    Type = "volume",
                    Source = "a2f9c1",
                    Destination = "/var/lib/hidden",
                    ReadOnly = false
                }
            ]
        };

        AssertEx.Contains(Violations(specification, observed), static violation => violation.Contains("/var/lib/hidden", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenAMountIsBackedFromOutsideTheInstanceDirectory_ReportsIt()
    {
        var specification = Specification(storage: [new ApplicationStorage("data", "/app/data")]);
        var observed = Inspection(specification);
        observed = observed with
        {
            Mounts =
            [
                new ContainerMountView
                {
                    Type = "bind",
                    Source = "/etc",
                    Destination = "/app/data",
                    ReadOnly = false
                }
            ]
        };

        AssertEx.Contains(Violations(specification, observed), static violation => violation.Contains("/etc", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The mount check reads what the daemon actually bound, and a path string resolves nothing: a component
    ///     replaced by a link leaves both sides reading as the instance's own directory while the container is bound
    ///     somewhere else entirely. The confinement is on the RESOLVED location, which is why the caller passes the
    ///     instance root — the only party that knows where the instance ends.
    /// </summary>
    [Test]
    public void FindViolations_WhenThePlannedMountResolvesOutsideTheInstanceDirectory_ReportsIt()
    {
        SymlinkSupport.EnsureSupported();

        var root = Path.Combine(Path.GetTempPath(), "xe-policy-confinement-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instanceRoot = Path.Combine(root, "instance");
            _ = Directory.CreateDirectory(Path.Combine(root, "outside", "data"));
            _ = Directory.CreateDirectory(Path.Combine(instanceRoot, "volumes", "app"));

            var hostPath = Path.Combine(instanceRoot, "volumes", "app", "data");
            Directory.CreateSymbolicLink(hostPath, Path.Combine(root, "outside", "data"));

            var specification = Specification(storage: [new ApplicationStorage("data", "/app/data")]) with
            {
                Mounts =
                [
                    new ContainerMount
                    {
                        HostPath = hostPath,
                        ContainerPath = "/app/data",
                        ReadOnly = false
                    }
                ]
            };
            var observed = Inspection(specification);

            AssertEx.Contains(ApplicationContainerPolicy.FindViolations(specification, observed, daemonIsRootless: true, afterStart: false, instanceRoot),
                violation => violation.Contains("outside this instance's own directory", StringComparison.Ordinal));

            // The negative control, and the defect this replaced: the two paths are the SAME string, so comparing
            // them lexically reports a container bound outside the instance directory as compliant.
            AssertEx.Empty(ApplicationContainerPolicy.FindViolations(specification, observed, daemonIsRootless: true, afterStart: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void FindViolations_WhenAReadOnlyAssetBecameWritable_ReportsIt()
    {
        var specification = Specification(files: [ExternalAppTestManifests.File("settings.yml", "/app/settings.yml", "body")]);
        var observed = Inspection(specification);
        observed = observed with
        {
            Mounts =
            [
                .. observed.Mounts.Select(static mount => mount with
                {
                    ReadOnly = false
                })
            ]
        };

        AssertEx.Contains(Violations(specification, observed), static violation => violation.Contains("writable", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenAPlannedMountWasNotApplied_ReportsIt()
    {
        var specification = Specification(storage: [new ApplicationStorage("data", "/app/data")]);
        var observed = Inspection(specification) with
        {
            Mounts = []
        };

        AssertEx.Contains(Violations(specification, observed), static violation => violation.Contains("not applied", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Pre-start reads what the daemon was ASKED to bind. Checking only this is how a widened binding gets
    ///     through, which is why the post-start pass below exists as well.
    /// </summary>
    [Test]
    public void FindViolations_PreStart_WhenTheRequestedBindingDiffersFromThePlan_ReportsIt()
    {
        var specification = Specification(ports: [ExternalAppTestManifests.UiPort(7000)]);
        var observed = Inspection(specification) with
        {
            RequestedPortBindings =
            [
                new ContainerPortPublication
                {
                    ContainerPort = 7000,
                    HostIp = "0.0.0.0",
                    HostPort = 40000
                }
            ]
        };

        AssertEx.NotEmpty(ApplicationContainerPolicy.FindViolations(specification, observed, daemonIsRootless: true, afterStart: false));
    }

    [Test]
    public void FindViolations_PostStart_WhenAPortIsBoundOnAllInterfaces_ReportsIt()
    {
        var specification = Specification(ports: [ExternalAppTestManifests.UiPort(7000)]);
        var observed = Inspection(specification, afterStart: true) with
        {
            PublishedPorts =
            [
                new ContainerPublishedPort
                {
                    ContainerPort = 7000,
                    Protocol = "tcp",
                    HostIp = "0.0.0.0",
                    HostPort = 40000
                }
            ]
        };

        AssertEx.Contains(ApplicationContainerPolicy.FindViolations(specification, observed, daemonIsRootless: false, afterStart: true),
            static violation => violation.Contains("0.0.0.0", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_PostStart_WhenAPortThePlanNeverRequestedIsPublished_ReportsIt()
    {
        var specification = Specification(ports: [ExternalAppTestManifests.UiPort(7000)]);
        var observed = Inspection(specification, afterStart: true);
        observed = observed with
        {
            PublishedPorts =
            [
                .. observed.PublishedPorts, new ContainerPublishedPort
                {
                    ContainerPort = 9000,
                    Protocol = "tcp",
                    HostIp = "127.0.0.1",
                    HostPort = 49000
                }
            ]
        };

        AssertEx.Contains(ApplicationContainerPolicy.FindViolations(specification, observed, daemonIsRootless: true, afterStart: true),
            static violation => violation.Contains("9000", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_PostStart_WhenAPlannedPortIsNotBound_ReportsIt()
    {
        var specification = Specification(ports: [ExternalAppTestManifests.UiPort(7000)]);
        var observed = Inspection(specification, afterStart: true) with
        {
            PublishedPorts = []
        };

        AssertEx.Contains(ApplicationContainerPolicy.FindViolations(specification, observed, daemonIsRootless: true, afterStart: true),
            static violation => violation.Contains("not bound", StringComparison.Ordinal));
    }

    [Test]
    public void FindViolations_WhenALimitOrTheRestartPolicyChanged_ReportsEachOne()
    {
        var specification = Specification();

        AssertEx.Contains(Violations(specification, Inspection(specification) with
            {
                MemoryBytes = 1
            }),
            static violation => violation.Contains("memory ceiling", StringComparison.Ordinal));
        AssertEx.Contains(Violations(specification, Inspection(specification) with
            {
                NanoCpus = 1
            }),
            static violation => violation.Contains("CPU ceiling", StringComparison.Ordinal));
        AssertEx.Contains(Violations(specification, Inspection(specification) with
            {
                PidsLimit = 99
            }),
            static violation => violation.Contains("process ceiling", StringComparison.Ordinal));
        AssertEx.Contains(Violations(specification, Inspection(specification) with
            {
                ReadOnlyRootFilesystem = true
            }),
            static violation => violation.Contains("root filesystem", StringComparison.Ordinal));
        AssertEx.Contains(Violations(specification, Inspection(specification) with
            {
                RestartMode = ContainerRestartMode.None
            }),
            static violation => violation.Contains("restart policy", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Every violation at once, not the first. A caller that learned about one violation per container-create
    ///     would need one deployment attempt per mistake to see the whole gap.
    /// </summary>
    [Test]
    public void FindViolations_ReportsEveryViolationTogether()
    {
        var specification = Specification();
        var observed = Inspection(specification) with
        {
            Privileged = true,
            DeviceCount = 2,
            SecurityOptions = [],
            MemoryBytes = 1024,
            RestartMode = ContainerRestartMode.None
        };

        var violations = Violations(specification, observed);

        AssertEx.True(violations.Count >= 6, $"Expected every violation, got {violations.Count}: {string.Join(" | ", violations)}");
    }

    /// <summary>There is no user check at all: the engine asks for no uid, so identity is not a policy control.</summary>
    [Test]
    public void FindViolations_DoesNotComplainAboutTheUserTheDaemonReports()
    {
        var specification = Specification();
        var observed = Inspection(specification) with
        {
            User = "0:0"
        };

        AssertEx.Empty(Violations(specification, observed));
    }

    private static IReadOnlyList<string> Violations(ContainerSpecification specification, ContainerInspection observed)
    {
        return ApplicationContainerPolicy.FindViolations(specification, observed, daemonIsRootless: true, afterStart: false);
    }

    private static ContainerSpecification Specification(ApplicationService? service = null,
        IReadOnlyList<ApplicationStorage>? storage = null,
        IReadOnlyList<ApplicationFile>? files = null,
        IReadOnlyList<ApplicationPort>? ports = null)
    {
        var resolved = service ?? ExternalAppTestManifests.Service("app", storage: storage, files: files, ports: ports);
        var manifest = ExternalAppTestManifests.Manifest([resolved]);
        var root = Path.Combine(Path.GetTempPath(), "xe-policy", InstanceId.ToString("N"));
        var paths = new ExternalAppStoragePaths(root, Path.Combine(root, "volumes"), Path.Combine(root, "files"));

        var plan = DeploymentPlanner.Plan(manifest,
            InstanceId,
            InstallId,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new ResolvedContainerIdentity(1000, 1000),
            [.. resolved.Ports.Select(static (port, index) => new ExternalAppHostPort("app", port.ContainerPort, 40000 + index))],
            paths);

        return plan.Services.Single().Specification;
    }

    /// <summary>An inspection that agrees with <paramref name="specification" /> in every checked field.</summary>
    private static ContainerInspection Inspection(ContainerSpecification specification, bool afterStart = false)
    {
        return new ContainerInspection
        {
            ContainerId = "c0ffee",
            Name = specification.Name,
            Image = specification.Image,
            Labels = specification.Labels,
            State = new ContainerRunState
            {
                Running = afterStart,
                Status = afterStart ? "running" : "created",
                ExitCode = 0,
                OutOfMemoryKilled = false,
                Health = ContainerHealthState.None
            },
            RequestedPortBindings = specification.PublishedPorts,
            PublishedPorts = afterStart
                ?
                [
                    .. specification.PublishedPorts.Select(static port => new ContainerPublishedPort
                    {
                        ContainerPort = port.ContainerPort,
                        Protocol = port.Protocol,
                        HostIp = port.HostIp,
                        HostPort = port.HostPort ?? 0
                    })
                ]
                : [],
            User = string.Empty,
            NetworkMode = specification.NetworkName,
            Privileged = false,
            ReadOnlyRootFilesystem = specification.ReadOnlyRootFilesystem,
            CapabilitiesDropped = ["ALL"],
            CapabilitiesAdded = specification.CapabilitiesToAdd,
            SecurityOptions = specification.SecurityOptions,
            Mounts =
            [
                .. specification.Mounts.Select(static mount => new ContainerMountView
                {
                    Type = "bind",
                    Source = mount.HostPath,
                    Destination = mount.ContainerPath,
                    ReadOnly = mount.ReadOnly
                })
            ],
            DeviceCount = 0,
            PidMode = string.Empty,
            IpcMode = "private",
            UtsMode = string.Empty,
            MemoryBytes = 0,
            NanoCpus = 0,
            PidsLimit = specification.PidsLimit,
            RestartMode = specification.RestartMode,
            ExtraHosts = specification.ExtraHosts
        };
    }
}

/// <summary>
///     The capability vocabulary is stated twice — once by the catalog validator that admits a manifest's
///     <c>requires[]</c>, once by the runtime that evaluates it. This slice is the first that can see both, and a
///     drift between them would mean a valid manifest answered "incompatible" by a runtime that offers the thing.
/// </summary>
public sealed class ContainerCapabilityNameParityTests
{
    [Test]
    public void TheCatalogAndTheRuntimeStateOneVocabulary()
    {
        AssertEx.Equal(string.Join(",", ContainerRuntimeCapabilities.Names),
            string.Join(",", ExternalAppCatalogValidator.CapabilityNames));
    }
}
