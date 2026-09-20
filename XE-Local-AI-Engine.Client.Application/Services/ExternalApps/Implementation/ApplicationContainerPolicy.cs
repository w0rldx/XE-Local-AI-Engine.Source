namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>What an application container is allowed to be, stated once as the specification the engine asks for and verified against what the daemon says it created.</summary>
/// <remarks>
///     A sibling of Development Mode's hardening in STYLE only — build one specification, create, inspect, collect
///     every violation rather than the first, fail closed. Dev Mode's verifier is NOT an alternative to this one: it
///     reads a different record as the complete statement of what must be true about a Development Mode container,
///     so calling it from here would check a contract written for another consumer. The rules it enforces are in
///     <c>docs/adr/0010-external-apps-container-execution.md</c> ("Invariants the read-back verifier enforces").
/// </remarks>
internal static class ApplicationContainerPolicy
{
    /// <summary>Machine token for a service asking for a capability outside Docker's own default set.</summary>
    internal const string CapabilityNotAllowedReason = "CapabilityNotAllowed";

    /// <summary>Machine token for an image reference that is not digest-pinned.</summary>
    internal const string ImageNotDigestPinnedReason = "ImageNotDigestPinned";

    /// <summary>Machine token for an extra host entry outside the one value a manifest may declare.</summary>
    internal const string ExtraHostNotAllowedReason = "ExtraHostNotAllowed";

    /// <summary>The loopback address every published port binds to. Nothing at this layer can express a LAN binding.</summary>
    internal const string LoopbackHostIp = "127.0.0.1";

    /// <summary>The one <c>extraHosts</c> entry a manifest may declare, and the entry it becomes.</summary>
    internal const string HostGatewayValue = "host-gateway";

    private const string HostGatewayEntry = "host.docker.internal:" + HostGatewayValue;

    /// <summary>The one security option every container this feature creates carries, the storage helper included.</summary>
    internal const string NoNewPrivileges = "no-new-privileges:true";

    private const string DigestMarker = "@sha256:";

    private const string HostMode = "host";

    /// <summary>Builds the specification for one service, setting every field the policy cares about explicitly.</summary>
    /// <remarks>
    ///     Including the ones whose default would be correct: a field the engine never passes is a field the
    ///     read-back verifier cannot meaningfully assert, and "we did not set it" is not evidence that the daemon
    ///     did not.
    /// </remarks>
    internal static ContainerSpecification BuildSpecification(ApplicationService service,
        ApplicationManifest manifest,
        Guid instanceId,
        string installId,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<ContainerMount> mounts,
        IReadOnlyList<ContainerPortPublication> publishedPorts)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(mounts);
        ArgumentNullException.ThrowIfNull(publishedPorts);

        if (service.Image is null || !service.Image.Contains(DigestMarker, StringComparison.Ordinal))
        {
            throw new ContainerPolicyException(ImageNotDigestPinnedReason,
                $"Service '{service.Name}' declares the image '{service.Image}', which is not pinned to a digest.");
        }

        return new ContainerSpecification
        {
            Image = service.Image,
            Name = ExternalAppLabels.ContainerName(instanceId, service.Name),

            // No user is passed: the curated images start as in-container root and drop privileges in their own
            // entrypoints, so a forced uid breaks that and a port-80 bind. The boundary is the container, not the uid.
            User = null,
            Labels = ExternalAppLabels.For(installId, instanceId, service.Name),
            Environment = environment,
            Mounts = mounts,
            PublishedPorts = publishedPorts,
            CapabilitiesToDrop = ["ALL"],
            CapabilitiesToAdd = ValidateCapabilities(service),
            SecurityOptions = [NoNewPrivileges, DockerSeccompProfile.SecurityOption],
            ReadOnlyRootFilesystem = service.ReadOnlyRootFilesystem,
            NetworkName = ExternalAppLabels.NetworkName(instanceId),
            NetworkAliases = [service.Name],
            RestartMode = ContainerRestartMode.UnlessStopped,

            // Deliberately unlimited. The manifest's memory figures gate ADMISSION and are not per-container
            // ceilings; pidsLimit is the one figure that really is imposed, as a fork-bomb guard.
            MemoryBytes = 0,
            NanoCpus = 0,
            PidsLimit = manifest.Resources.PidsLimit,
            Entrypoint = service.Entrypoint,
            Command = service.Command,
            ExtraHosts = ResolveExtraHosts(service),
            Healthcheck = ToHealthcheck(service.Healthcheck)
        };
    }

    private static IReadOnlyList<string> ValidateCapabilities(ApplicationService service)
    {
        var requested = service.CapAdd ?? [];
        var offender = requested.FirstOrDefault(capability =>
            !ExternalAppCatalogValidator.AllowedCapAdd.Contains(capability, StringComparer.Ordinal));

        if (offender is not null)
        {
            throw new ContainerPolicyException(CapabilityNotAllowedReason,
                $"Service '{service.Name}' asks for capability '{offender}', which is outside Docker's own default set.");
        }

        return [.. requested];
    }

    private static IReadOnlyList<string> ResolveExtraHosts(ApplicationService service)
    {
        var declared = service.ExtraHosts ?? [];
        var offender = declared.FirstOrDefault(entry => !string.Equals(entry, HostGatewayValue, StringComparison.Ordinal));
        if (offender is not null)
        {
            throw new ContainerPolicyException(ExtraHostNotAllowedReason,
                $"Service '{service.Name}' declares the extra host '{offender}'; only '{HostGatewayValue}' is representable.");
        }

        return declared.Count == 0 ? [] : [HostGatewayEntry];
    }

    private static ContainerHealthcheck? ToHealthcheck(ApplicationHealthcheck? healthcheck)
    {
        if (healthcheck is null)
        {
            return null;
        }

        return new ContainerHealthcheck
        {
            Test = healthcheck.Test,
            Interval = TimeSpan.FromSeconds(healthcheck.IntervalSeconds),
            Timeout = TimeSpan.FromSeconds(healthcheck.TimeoutSeconds),
            Retries = healthcheck.Retries,
            StartPeriod = TimeSpan.FromSeconds(healthcheck.StartPeriodSeconds)
        };
    }

    /// <summary>One published port, always on loopback and always with the host port the allocator already holds.</summary>
    internal static ContainerPortPublication Publication(int containerPort, int hostPort)
    {
        return new ContainerPortPublication
        {
            ContainerPort = containerPort,
            HostIp = LoopbackHostIp,
            HostPort = hostPort
        };
    }

    /// <summary>Formats a published port for an operator-facing message without naming a host path or a value.</summary>
    internal static string Describe(ContainerPortPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);

        return string.Create(CultureInfo.InvariantCulture,
            $"{publication.HostIp}:{publication.HostPort?.ToString(CultureInfo.InvariantCulture) ?? "auto"}->{publication.ContainerPort}");
    }

    /// <summary>Every way the container the daemon actually created differs from the one that was asked for, returned ALL at once rather than one at a time.</summary>
    /// <remarks>
    ///     Run twice per container, because the two inspects see different things: <paramref name="afterStart" />
    ///     false reads what the daemon was ASKED to bind, true reads what it actually bound. Why that, and every
    ///     other rule enforced below, is in <c>docs/adr/0010-external-apps-container-execution.md</c> ("Invariants
    ///     the read-back verifier enforces").
    /// </remarks>
    /// <param name="daemonIsRootless">Shapes the operator prose only; every rule below reads the same on a rootless daemon.</param>
    /// <param name="instanceRoot">
    ///     The instance directory every bind source must physically live under, or <see langword="null" /> to compare
    ///     the mounts without confining them.
    /// </param>
    internal static IReadOnlyList<string> FindViolations(ContainerSpecification requested,
        ContainerInspection observed,
        bool daemonIsRootless,
        bool afterStart,
        string? instanceRoot = null)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(observed);

        var violations = new List<string>();
        var daemonMode = daemonIsRootless ? "rootless" : "rootful";

        VerifyCapabilities(requested, observed, violations);
        VerifySecurityOptions(observed, violations);
        VerifyPrivilegeAndNamespaces(observed, violations);
        VerifyNetwork(requested, observed, violations);
        VerifyMounts(requested, observed, daemonMode, instanceRoot, violations);
        VerifyPorts(requested, observed, afterStart, daemonMode, violations);
        VerifyLimitsAndRestartPolicy(requested, observed, violations);

        // Deliberately no user check: the engine asks for no uid, so identity is not a policy control here. The
        // boundary is the dropped capabilities, seccomp, no-new-privileges and the loopback-only network.
        return violations;
    }

    private static void VerifyCapabilities(ContainerSpecification requested, ContainerInspection observed, List<string> violations)
    {
        if (!observed.CapabilitiesDropped.Contains("ALL", StringComparer.OrdinalIgnoreCase))
        {
            violations.Add("the daemon did not drop ALL capabilities");
        }

        var allowed = new HashSet<string>(requested.CapabilitiesToAdd, StringComparer.OrdinalIgnoreCase);
        foreach (var capability in observed.CapabilitiesAdded.Where(capability => !allowed.Contains(capability)))
        {
            violations.Add($"the container holds capability '{capability}', which the manifest did not ask for");
        }
    }

    /// <summary>The two security options are verified by their VALUE, not by the presence of their name.</summary>
    /// <remarks>
    ///     A container reading back <c>no-new-privileges:false</c> or <c>seccomp=unconfined</c> names both options
    ///     and has neither protection, so a presence check would hand reuse and boot adoption a container with its
    ///     confinement switched off. The name is matched by prefix because daemons render it as
    ///     <c>no-new-privileges</c>, <c>no-new-privileges:true</c> and <c>no-new-privileges=true</c> across versions;
    ///     only the explicit false rendering means the protection is off.
    /// </remarks>
    private static void VerifySecurityOptions(ContainerInspection observed, List<string> violations)
    {
        if (!observed.SecurityOptions.Any(static option =>
                option.StartsWith("no-new-privileges", StringComparison.OrdinalIgnoreCase)
                && !option.EndsWith("false", StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add("no-new-privileges is not applied");
        }

        // No seccomp option reads back like a daemon with seccomp disabled, so the profile is passed explicitly and
        // `seccomp=unconfined` is refused. Matched on "names a profile", not equality — ADR 0010, same section.
        if (!observed.SecurityOptions.Any(DockerSeccompProfile.NamesAProfile))
        {
            violations.Add("the engine seccomp profile is not applied");
        }
    }

    private static void VerifyPrivilegeAndNamespaces(ContainerInspection observed, List<string> violations)
    {
        if (observed.Privileged)
        {
            violations.Add("the container is privileged");
        }

        if (observed.DeviceCount != 0)
        {
            violations.Add($"the container has {observed.DeviceCount.ToString(CultureInfo.InvariantCulture)} device mapping(s); applications get none");
        }

        foreach (var (name, mode) in new[]
                 {
                     ("pid", observed.PidMode),
                     ("ipc", observed.IpcMode),
                     ("uts", observed.UtsMode)
                 })
        {
            if (string.Equals(mode, HostMode, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"the container shares the host {name} namespace");
            }
        }
    }

    private static void VerifyNetwork(ContainerSpecification requested, ContainerInspection observed, List<string> violations)
    {
        if (string.Equals(observed.NetworkMode, HostMode, StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("the container is on the host network");
            return;
        }

        if (requested.PublishedPorts.Count > 0 && string.Equals(observed.NetworkMode, "none", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("the container publishes ports but has no network");
            return;
        }

        if (!string.Equals(observed.NetworkMode, requested.NetworkName, StringComparison.Ordinal))
        {
            violations.Add($"the container is on network '{observed.NetworkMode}' rather than this instance's '{requested.NetworkName}'");
        }
    }

    /// <summary>The EFFECTIVE mount set must equal the planned one exactly.</summary>
    /// <remarks>
    ///     That is what catches an anonymous volume created by an image's own <c>VOLUME</c> instruction: it appears
    ///     in the inspect and in no request, and it would put application data outside the instance directory, where
    ///     a reset and an uninstall cannot reach it.
    /// </remarks>
    private static void VerifyMounts(ContainerSpecification requested,
        ContainerInspection observed,
        string daemonMode,
        string? instanceRoot,
        List<string> violations)
    {
        var confinement = instanceRoot is null ? null : ResolveThroughLinks(Path.GetFullPath(instanceRoot));

        var planned = requested.Mounts.ToDictionary(static mount => DockerSandboxPaths.NormalizePosix(mount.ContainerPath), StringComparer.Ordinal);

        foreach (var applied in observed.Mounts)
        {
            var destination = DockerSandboxPaths.NormalizePosix(applied.Destination);
            if (!planned.TryGetValue(destination, out var expected))
            {
                violations.Add($"the container has an undeclared mount at '{destination}' ({daemonMode} daemon); declare it as a storage entry rather than allowing it");
                continue;
            }

            // Both sides go through their links before they are compared, and the planned side is confined by its REAL
            // location: a component swapped for a link between plan and create defeats a string comparison.
            var plannedReal = ResolveThroughLinks(Path.GetFullPath(expected.HostPath));
            if (confinement is not null && !IsInside(plannedReal, confinement))
            {
                violations.Add($"the mount at '{destination}' resolves to '{plannedReal}', which is outside this instance's own directory ({daemonMode} daemon)");
                continue;
            }

            if (!string.Equals(ResolveThroughLinks(Path.GetFullPath(applied.Source)), plannedReal, StringComparison.Ordinal))
            {
                violations.Add($"the mount at '{destination}' is backed by '{applied.Source}' rather than by this instance's own directory ({daemonMode} daemon)");
            }

            if (applied.ReadOnly != expected.ReadOnly)
            {
                violations.Add($"the mount at '{destination}' is {(applied.ReadOnly ? "read-only" : "writable")} rather than {(expected.ReadOnly ? "read-only" : "writable")}");
            }
        }

        var appliedDestinations = observed.Mounts.Select(static mount => DockerSandboxPaths.NormalizePosix(mount.Destination)).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in planned.Keys.Where(destination => !appliedDestinations.Contains(destination)))
        {
            violations.Add($"the planned mount at '{missing}' was not applied");
        }
    }

    /// <summary>Whether a resolved path is the instance directory itself or something beneath it.</summary>
    private static bool IsInside(string resolved, string root)
    {
        return string.Equals(resolved, root, StringComparison.Ordinal)
               || resolved.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal);
    }

    /// <summary>One absolute path with every component resolved through its links, which is what a path string cannot do for itself.</summary>
    /// <remarks>
    ///     A component that does not exist, or that cannot be read, resolves to itself: this verifies what the daemon
    ///     reported, and is never a reason to fail a container over a race with a deletion.
    /// </remarks>
    private static string ResolveThroughLinks(string path)
    {
        var current = Path.TrimEndingDirectorySeparator(path);
        var parent = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(parent))
        {
            return current;
        }

        var combined = Path.Combine(ResolveThroughLinks(parent), Path.GetFileName(current));

        try
        {
            return Directory.ResolveLinkTarget(combined, returnFinalTarget: true)?.FullName
                   ?? File.ResolveLinkTarget(combined, returnFinalTarget: true)?.FullName
                   ?? combined;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return combined;
        }
    }

    private static void VerifyPorts(ContainerSpecification requested,
        ContainerInspection observed,
        bool afterStart,
        string daemonMode,
        List<string> violations)
    {
        var planned = requested.PublishedPorts.ToDictionary(static port => port.ContainerPort);

        if (!afterStart)
        {
            foreach (var binding in observed.RequestedPortBindings)
            {
                if (!planned.TryGetValue(binding.ContainerPort, out var expected))
                {
                    violations.Add($"the daemon was asked to publish container port {binding.ContainerPort.ToString(CultureInfo.InvariantCulture)}, which the plan does not publish");
                    continue;
                }

                if (!string.Equals(binding.HostIp, LoopbackHostIp, StringComparison.Ordinal) || binding.HostPort != expected.HostPort)
                {
                    violations.Add($"container port {binding.ContainerPort.ToString(CultureInfo.InvariantCulture)} was requested on {Describe(binding)} rather than on {Describe(expected)}");
                }
            }

            foreach (var missing in planned.Values.Where(port => observed.RequestedPortBindings.All(binding => binding.ContainerPort != port.ContainerPort)))
            {
                violations.Add($"container port {missing.ContainerPort.ToString(CultureInfo.InvariantCulture)} was not requested at all");
            }

            return;
        }

        foreach (var bound in observed.PublishedPorts)
        {
            if (!planned.TryGetValue(bound.ContainerPort, out var expected))
            {
                violations.Add($"the daemon published container port {bound.ContainerPort.ToString(CultureInfo.InvariantCulture)}, which the plan does not publish");
                continue;
            }

            if (!string.Equals(bound.HostIp, LoopbackHostIp, StringComparison.Ordinal))
            {
                violations.Add($"container port {bound.ContainerPort.ToString(CultureInfo.InvariantCulture)} is bound on '{bound.HostIp}' rather than on loopback ({daemonMode} daemon)");
            }

            if (bound.HostPort == 0 || bound.HostPort != expected.HostPort)
            {
                violations.Add(
                    $"container port {bound.ContainerPort.ToString(CultureInfo.InvariantCulture)} is bound on host port {bound.HostPort.ToString(CultureInfo.InvariantCulture)} rather than on the one the engine reserved");
            }
        }

        foreach (var missing in planned.Values.Where(port => observed.PublishedPorts.All(bound => bound.ContainerPort != port.ContainerPort)))
        {
            violations.Add($"container port {missing.ContainerPort.ToString(CultureInfo.InvariantCulture)} is not bound at all");
        }
    }

    private static void VerifyLimitsAndRestartPolicy(ContainerSpecification requested, ContainerInspection observed, List<string> violations)
    {
        if (observed.MemoryBytes != 0)
        {
            violations.Add($"the container has a memory ceiling of {observed.MemoryBytes.ToString(CultureInfo.InvariantCulture)} bytes; V1 sets none");
        }

        if (observed.NanoCpus != 0)
        {
            violations.Add($"the container has a CPU ceiling of {observed.NanoCpus.ToString(CultureInfo.InvariantCulture)} nano-CPUs; V1 sets none");
        }

        if (observed.PidsLimit != requested.PidsLimit)
        {
            violations.Add(
                $"the container's process ceiling is {observed.PidsLimit.ToString(CultureInfo.InvariantCulture)} rather than the manifest's {requested.PidsLimit.ToString(CultureInfo.InvariantCulture)}");
        }

        if (observed.ReadOnlyRootFilesystem != requested.ReadOnlyRootFilesystem)
        {
            violations.Add(
                $"the container's root filesystem is {(observed.ReadOnlyRootFilesystem ? "read-only" : "writable")} rather than {(requested.ReadOnlyRootFilesystem ? "read-only" : "writable")}");
        }

        if (observed.RestartMode != requested.RestartMode)
        {
            violations.Add($"the container's restart policy is {observed.RestartMode} rather than {requested.RestartMode}");
        }
    }
}
