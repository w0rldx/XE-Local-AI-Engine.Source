namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

internal static class DevelopmentContractMapper
{
    /// <summary>
    ///     Projects the profile as its id, build target and digest rather than the stored blob. The build target is
    ///     repository-relative by construction, so nothing host-identifying crosses the boundary; the full profile's
    ///     argument vectors would, and buy the operator nothing.
    /// </summary>
    public static DevelopmentProjectResponse ToResponse(this DevelopmentProjectSnapshot value)
    {
        var profile = DevelopmentProfileSummary.TryFrom(value.CommandProfileJson);
        return new DevelopmentProjectResponse(value.Id,
            value.Objective,
            value.SelectedFolderId,
            value.SelectedFolderId is null,
            value.BaseBranch,
            value.Status.ToString(),
            value.EgressPolicy.ToString(),
            value.CoderModelId,
            value.ReviewerModelId,
            value.MaxTokens,
            value.MaxDurationSeconds,
            value.CreatedAtUtc,
            value.UpdatedAtUtc,
            value.Version,
            profile?.ProfileId,
            profile?.BuildTarget,
            profile?.Digest);
    }

    public static DevelopmentRepositoryResponse ToResponse(this DevelopmentRepositoryReference value) =>
        new(value.Id, value.Alias, value.Availability);

    public static DevelopmentTemplateResponse ToResponse(this DevelopmentTemplateReference value) =>
        new(value.Id, value.Alias, value.Availability);

    public static DevelopmentTaskResponse ToResponse(this DevelopmentTaskSnapshot value) =>
        new(value.Id,
            value.ProjectId,
            value.Title,
            value.Requirements,
            value.AcceptanceCriteriaJson,
            value.Status.ToString(),
            value.CurrentReviewRound,
            value.MaxReviewRounds,
            value.BlockedReason,
            value.ApprovedSubjectHash,
            value.Version);

    public static DevelopmentAttemptResponse ToResponse(this DevelopmentAttemptSnapshot value) =>
        new(value.Id,
            value.TaskId,
            value.PredecessorAttemptId,
            value.Role.ToString(),
            value.ModelId,
            value.Provider,
            value.Status.ToString(),
            value.StartedAtUtc,
            value.EndedAtUtc,
            value.TerminalReason,
            value.InputTokens,
            value.OutputTokens,
            value.Version);

    public static DevelopmentArtifactResponse ToResponse(this DevelopmentArtifactSnapshot value) =>
        new(value.Id,
            value.ProjectId,
            value.TaskId,
            value.AttemptId,
            value.Kind.ToString(),
            value.ContentHash,
            value.ByteCount,
            value.CreatedAtUtc,
            value.BaseCommit,
            value.SubjectHash,
            value.ChangedFilesManifestHash,
            value.CommandProfileVersion,
            value.CommandProfileDigest,
            value.IsValid);

    public static DevelopmentEventResponse ToResponse(this DevelopmentEventSnapshot value) =>
        new(value.Id,
            value.ProjectId,
            value.TaskId,
            value.AttemptId,
            value.Sequence,
            value.EventType,
            value.OccurredAtUtc,
            value.OperationId,
            value.OperationPhase,
            value.Outcome);

    public static DevelopmentTaskDetailResponse ToResponse(this DevelopmentTaskAggregate value) =>
        new(value.Task.ToResponse(),
            value.Attempts.Select(ToResponse).ToArray(),
            value.Artifacts.Select(ToResponse).ToArray());

    public static DevelopmentProjectDetailResponse ToResponse(this DevelopmentProjectAggregate value) =>
        new(value.Project.ToResponse(),
            value.Tasks.Select(ToResponse).ToArray(),
            value.Events.Select(ToResponse).ToArray());

    /// <summary>
    ///     Projects one sandbox role's SERVED isolation posture — what the role's own declaration asks for, intersected
    ///     with what its provider advertises — into the operator-facing summary.
    ///     <para>
    ///         THE DERIVATION RULE, in one place. It is an INTERSECTION, not a capability readout, and that is the
    ///         correction this method exists in its current shape to carry: reading the provider's flags alone reported
    ///         Development Mode on this Linux box as filesystem-isolated and <c>Isolated</c>, because the process
    ///         backend advertises a boundary — while <see cref="SandboxWorkloads.DevelopmentModeHostToolchain" />
    ///         declares <see cref="SandboxIsolationMode.None" /> and the feature runs the host toolchain with the
    ///         worktree mounted. A capability a role never requests is not a boundary the role is behind.
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <term>Filesystem</term>
    ///             <description>
    ///                 the provider advertises
    ///                 <see cref="SandboxProviderCapabilities.SupportsHostFilesystemBoundary" /> AND the role's
    ///                 <see cref="SandboxRequirements.IsolationFloor" /> is
    ///                 <see cref="SandboxIsolationMode.Filesystem" />. The PROPERTY flag, not
    ///                 <see cref="SandboxProviderCapabilities.SupportsFilesystemIsolation" />: this surface answers
    ///                 "can a command here see the host filesystem", and a hardened container cannot — read-only
    ///                 rootfs, engine-generated mounts only, no host namespaces, all read back and fail-closed on
    ///                 mismatch — while that narrower flag means "serves the bubblewrap chain's own create-request
    ///                 contract", which the container backend refuses. <c>run_python</c> is the one role in this engine
    ///                 that declares the floor (<see cref="SandboxWorkloads.RunPython" />), so on a host with a working
    ///                 chain it is the one role this column says <c>Yes</c> for.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <term>Network</term>
    ///             <description>
    ///                 the provider advertises <see cref="SandboxProviderCapabilities.SupportsNetworkPolicy" />. Not
    ///                 intersected with <see cref="SandboxRequirements.NetworkFloor" />, and deliberately: the floor is
    ///                 the weakest posture a workload will ACCEPT (<c>Unrestricted</c> for AgentHome and Development
    ///                 Mode, so that a node without the mechanism still runs), while every consumer REQUESTS
    ///                 <see cref="SandboxNetworkPolicy.None" /> per call exactly where the flag is advertised —
    ///                 <c>AgentHomeService.ResolveNetworkPolicy</c>,
    ///                 <c>DevelopmentWorkspaceProvider.ResolveAgentFacingNetworkPolicy</c> (G1c Option B), and
    ///                 <c>ComputeToolGateway.BuildCreateRequest</c> unconditionally. So the flag IS the served posture
    ///                 here, and reading the floor instead would report egress as unrestricted on a node that denies
    ///                 it.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <term>Resource limits</term>
    ///             <description>
    ///                 the provider advertises <see cref="SandboxProviderCapabilities.SupportsResourceLimits" />. No
    ///                 role declares a ceiling axis, so there is nothing to intersect with.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         <see cref="SandboxIsolationSummaryResponse.Level" /> counts those three SERVED axes, unchanged in rule
    ///         and changed in inputs: <c>Isolated</c> for three, <c>Confined</c> for one or two, <c>None</c> for zero.
    ///         There is no fourth term for a hardware or VM boundary because nothing in this tree can prove one.
    ///     </para>
    ///     <para>
    ///         Counting advertised flags rather than trusting a provider's name stays the other half of the point: the
    ///         process provider advertises a flag only where the containment probe EXERCISED the mechanism, so a
    ///         Windows host reports <c>None</c> with the measured reason on exactly the same code path that reports a
    ///         served boundary on Linux.
    ///     </para>
    ///     <para>
    ///         <see cref="SandboxIsolationSummaryResponse.FilesystemIsolationUnavailableReason" /> therefore carries
    ///         two different sentences, and telling them apart is the operator's whole action: NOT REQUESTED by the
    ///         role (nothing to fix — the workload declares no boundary) versus REQUESTED AND UNAVAILABLE (the measured
    ///         probe reason — install the missing mechanism, or leave the tool off).
    ///     </para>
    /// </summary>
    /// <param name="role">The wire role name, as the panel keys its rows on.</param>
    /// <param name="requirements">
    ///     The role's ADR 0007 declaration from <see cref="SandboxWorkloads" />, which is the source of truth for what
    ///     the role asks for. Passed in rather than looked up from <paramref name="role" /> so this projection owns no
    ///     second per-role table that could drift from the constants the selector resolves against.
    /// </param>
    /// <param name="provider">The provider actually resolved for that role.</param>
    /// <param name="containment">The host containment measurement, for the probe reason.</param>
    public static SandboxIsolationSummaryResponse ToIsolationSummary(string role,
        SandboxRequirements requirements,
        ISandboxRuntimeProvider provider,
        SandboxContainment containment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(containment);

        var capabilities = provider.Capabilities;
        var boundaryRequested = requirements.IsolationFloor == SandboxIsolationMode.Filesystem;
        var boundaryAdvertised = capabilities.HasFlag(SandboxProviderCapabilities.SupportsHostFilesystemBoundary);
        var filesystem = boundaryRequested && boundaryAdvertised;
        var network = capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy);
        var limits = capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits);
        var enforced = (filesystem ? 1 : 0) + (network ? 1 : 0) + (limits ? 1 : 0);
        var boundaryReason = boundaryRequested
            ? ToFilesystemIsolationUnavailableReason(provider.ProviderName, containment)
            : ToBoundaryNotRequestedReason(requirements);

        return new SandboxIsolationSummaryResponse(role,
            provider.ProviderName,
            ToIsolationBackend(provider.ProviderName, filesystem),
            enforced switch
            {
                3 => "Isolated",
                > 0 => "Confined",
                _ => "None"
            },
            filesystem,
            network,
            limits,
            capabilities.HasFlag(SandboxProviderCapabilities.SupportsReadOnlyMounts),
            filesystem ? null : boundaryReason);
    }

    // Derived from the declaration rather than written per role, so a workload added to SandboxWorkloads gets a true
    // sentence without touching this file. Every role that declares SandboxIsolationMode.None gets the same one
    // because that value has exactly one meaning — a working-directory jail on the host filesystem, readable wherever
    // the engine's own user can read.
    private static string ToBoundaryNotRequestedReason(SandboxRequirements requirements)
    {
        return $"not requested by this role: '{requirements.Workload}' declares an isolation floor of "
               + $"{requirements.IsolationFloor}, so its commands run in a working-directory jail on the host "
               + "filesystem and can read whatever the account running the engine can read";
    }

    private static string ToIsolationBackend(string providerName, bool filesystemIsolation)
    {
        return providerName switch
        {
            ProcessSandboxRuntimeProvider.Name => filesystemIsolation ? "bwrap" : "process",
            DockerSandboxRuntimeProvider.Name => "docker",
            _ => "none"
        };
    }

    // The containment probe measures the HOST bubblewrap chain, which is the process provider's boundary and nobody
    // else's. Attributing its reason to another provider would tell an operator that a container role is unisolated
    // because this host lacks bwrap, which is not why.
    private static string ToFilesystemIsolationUnavailableReason(string providerName, SandboxContainment containment)
    {
        if (string.Equals(providerName, ProcessSandboxRuntimeProvider.Name, StringComparison.Ordinal))
        {
            return containment.FilesystemIsolationUnavailableReason
                   ?? "the supervised process sandbox did not advertise a filesystem boundary on this host";
        }

        // The container provider no longer reaches here: it advertises the boundary, so this projection reports no
        // reason for it. The generic arm stays for a backend added later that advertises neither.
        return string.Equals(providerName, FakeSandboxRuntimeProvider.Name, StringComparison.Ordinal)
            ? "the deterministic in-memory provider has no mount namespace and never will"
            : $"the '{providerName}' sandbox provider does not advertise a filesystem boundary";
    }
}

/// <summary>Projects the container-runtime preflight onto its wire contract.</summary>
internal static class DevelopmentContainerRuntimeMapper
{
    public static DevelopmentContainerRuntimeResponse ToResponse(this DockerDaemonPreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        return new DevelopmentContainerRuntimeResponse(preflight.Ready,
            ToStatusCode(preflight.Status),
            preflight.Message,
            preflight.RequiresOperatorConfirmation,
            preflight.Endpoint?.Display,
            preflight.Endpoint is null ? null : ToSourceCode(preflight.Endpoint.Source),
            preflight.ObservedDaemon is null
                ? null
                : new DevelopmentContainerDaemonResponse(preflight.ObservedDaemon.DaemonId,
                    preflight.ObservedDaemon.ServerVersion,
                    preflight.ObservedDaemon.Endpoint.Display,
                    ConfirmedAtUtc: null),
            preflight.PinnedDaemon is null
                ? null
                : new DevelopmentContainerDaemonResponse(preflight.PinnedDaemon.DaemonId,
                    preflight.PinnedDaemon.ServerVersion,
                    preflight.PinnedDaemon.Endpoint,
                    preflight.PinnedDaemon.ConfirmedAtUtc));
    }

    // Mapped explicitly rather than by ToString(): these codes are a wire contract the React app branches on, and
    // renaming an enum member should not silently change what a client sees.
    private static string ToStatusCode(DockerDaemonPreflightStatus status)
    {
        return status switch
        {
            DockerDaemonPreflightStatus.Ready => "ready",
            DockerDaemonPreflightStatus.DaemonUnreachable => "daemon_unreachable",
            DockerDaemonPreflightStatus.PermissionDenied => "permission_denied",
            DockerDaemonPreflightStatus.ApiVersionTooOld => "api_version_too_old",
            DockerDaemonPreflightStatus.DaemonIdentityChanged => "daemon_changed",
            DockerDaemonPreflightStatus.NotConfigured => "not_configured",
            _ => "probe_failed"
        };
    }

    private static string ToSourceCode(DockerDaemonEndpointSource source)
    {
        return source switch
        {
            DockerDaemonEndpointSource.Configuration => "configuration",
            DockerDaemonEndpointSource.DockerHostEnvironmentVariable => "docker_host",
            DockerDaemonEndpointSource.DefaultUnixSocket => "default_socket",
            DockerDaemonEndpointSource.UserRuntimeUnixSocket => "user_socket",
            DockerDaemonEndpointSource.WindowsNamedPipe => "windows_pipe",
            _ => "unknown"
        };
    }
}
