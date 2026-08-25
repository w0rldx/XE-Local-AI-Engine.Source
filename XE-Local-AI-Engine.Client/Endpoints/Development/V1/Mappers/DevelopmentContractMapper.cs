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
    ///     Projects one sandbox role's advertised capabilities into the operator-facing isolation summary.
    ///     <para>
    ///         THE DERIVATION RULE, in one place. Three enforcement axes are read straight off the provider's
    ///         <see cref="SandboxProviderCapabilities" />: a filesystem boundary
    ///         (<see cref="SandboxProviderCapabilities.SupportsFilesystemIsolation" />), egress denial
    ///         (<see cref="SandboxProviderCapabilities.SupportsNetworkPolicy" />), and real ceilings
    ///         (<see cref="SandboxProviderCapabilities.SupportsResourceLimits" />). The level is the count:
    ///     </para>
    ///     <list type="bullet">
    ///         <item><c>Isolated</c> — all three enforced.</item>
    ///         <item><c>Confined</c> — one or two enforced.</item>
    ///         <item><c>None</c> — none enforced; the role runs with no boundary this node can attest to.</item>
    ///     </list>
    ///     <para>
    ///         Counting the advertised flags rather than trusting the provider's name is the whole point: the process
    ///         provider advertises a flag only where the containment probe EXERCISED the mechanism, so a Windows host —
    ///         where the bubblewrap chain does not exist and isolation fails closed — reports <c>None</c> with the
    ///         measured reason, on exactly the same code path that reports <c>Isolated</c> here on Linux. There is no
    ///         fourth term for a hardware or VM boundary because nothing in this tree can prove one.
    ///     </para>
    ///     <para>
    ///         The container provider currently lands on <c>Confined</c>: it does not advertise
    ///         <see cref="SandboxProviderCapabilities.SupportsFilesystemIsolation" />, and inventing the boundary here
    ///         would be the same dishonesty the flag exists to prevent. Fix that in the provider, not in this
    ///         projection.
    ///     </para>
    /// </summary>
    public static SandboxIsolationSummaryResponse ToIsolationSummary(string role,
        ISandboxRuntimeProvider provider,
        SandboxContainment containment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(containment);

        var capabilities = provider.Capabilities;
        // The PROPERTY flag, not SupportsFilesystemIsolation. This surface answers "can a command here see the host
        // filesystem", and a hardened container cannot — read-only rootfs, engine-generated mounts only, no host
        // namespaces, all read back and fail-closed on mismatch. SupportsFilesystemIsolation means something narrower:
        // "serves SandboxIsolationMode.Filesystem", the bubblewrap chain's own create-request contract, which the
        // container provider refuses. Reading that flag here reported a container role as merely Confined, which
        // understated the boundary rather than overstating it — but a report that is wrong in the safe direction is
        // still wrong, and an operator reading "Confined" would go looking for a hardening step that is already done.
        var filesystem = capabilities.HasFlag(SandboxProviderCapabilities.SupportsHostFilesystemBoundary);
        var network = capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy);
        var limits = capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits);
        var enforced = (filesystem ? 1 : 0) + (network ? 1 : 0) + (limits ? 1 : 0);

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
            filesystem ? null : ToFilesystemIsolationUnavailableReason(provider.ProviderName, containment));
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
